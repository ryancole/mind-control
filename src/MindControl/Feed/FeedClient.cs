using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Threading.Channels;

namespace MindControl.Feed;

public abstract record FeedNotice;

/// <summary>An event of a subscribed kind.</summary>
public sealed record EventNotice(GameEvent Event) : FeedNotice;

/// <summary>The server dropped ring history we can never have. Resync from /state.</summary>
public sealed record GapNotice(long From, long To) : FeedNotice;

public sealed record FeedConnected(bool Resumed) : FeedNotice;

/// <summary>The stream dropped. The reactor must treat this as going blind.</summary>
public sealed record FeedLost(string Reason) : FeedNotice;

public sealed record StateSnapshot(Meta Meta, FrameEnvelope? Frame);

/// <summary>
/// Reads the spectral-sight SSE feed and splits it into two channels: a
/// latest-wins mailbox of frames (capacity 1, drop-oldest — stale game state
/// is never queued) and an ordered queue of notices (events, gaps, connection
/// changes). Reconnects forever with Last-Event-ID resume; every drop is
/// surfaced as a <see cref="FeedLost"/> before the retry.
/// </summary>
public sealed class FeedClient(Uri baseUri, IReadOnlySet<string>? eventKinds = null)
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(500);

    private readonly HttpClient _http = new() { BaseAddress = baseUri, Timeout = Timeout.InfiniteTimeSpan };

    private readonly Channel<FrameEnvelope> _frames = Channel.CreateBounded<FrameEnvelope>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    private readonly Channel<FeedNotice> _notices = Channel.CreateBounded<FeedNotice>(
        new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    private string? _lastEventId;

    public ChannelReader<FrameEnvelope> Frames => _frames.Reader;
    public ChannelReader<FeedNotice> Notices => _notices.Reader;

    public async Task<Meta> GetMetaAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("/meta", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<Meta>(stream, FeedJson.Options, ct)
            ?? throw new InvalidOperationException("/meta returned null");
    }

    /// <summary>What a late joiner reads first, and what a resync reads after a gap.</summary>
    public async Task<StateSnapshot> GetStateAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("/state", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<StateSnapshot>(stream, FeedJson.Options, ct)
            ?? throw new InvalidOperationException("/state returned null");
    }

    /// <summary>Runs until cancelled, reconnecting on every drop.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await StreamOnceAsync(ct);
                Notify(new FeedLost("stream ended"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Notify(new FeedLost(ex.Message));
            }

            try
            {
                await Task.Delay(ReconnectDelay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task StreamOnceAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/stream");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (_lastEventId is not null)
            request.Headers.TryAddWithoutValidation("Last-Event-ID", _lastEventId);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        Notify(new FeedConnected(Resumed: _lastEventId is not null));

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(ct))
        {
            if (!string.IsNullOrEmpty(item.EventId))
                _lastEventId = item.EventId;
            Dispatch(item.EventType, item.Data);
        }
    }

    private void Dispatch(string eventType, string data)
    {
        switch (eventType)
        {
            case "frame":
                var frame = JsonSerializer.Deserialize<FrameEnvelope>(data, FeedJson.Options);
                if (frame is not null)
                    _frames.Writer.TryWrite(frame);
                break;
            case "event":
                var evt = JsonSerializer.Deserialize<GameEvent>(data, FeedJson.Options);
                if (evt is not null && (eventKinds is null || eventKinds.Contains(evt.Kind)))
                    Notify(new EventNotice(evt));
                break;
            case "gap":
                using (var doc = JsonDocument.Parse(data))
                {
                    Notify(new GapNotice(
                        doc.RootElement.GetProperty("from").GetInt64(),
                        doc.RootElement.GetProperty("to").GetInt64()));
                }
                break;
            // Unknown record types are ignored, per the format's forward-compat rule.
        }
    }

    private void Notify(FeedNotice notice) => _notices.Writer.TryWrite(notice);
}
