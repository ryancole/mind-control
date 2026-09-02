using MindControl.Feed;
using MindControl.Policy;

namespace MindControl;

public sealed record ReactorOptions
{
    public ushort ScreenWidth { get; init; } = 1920;
    public ushort ScreenHeight { get; init; } = 1080;

    /// <summary>No frame for this long means we are blind: pause coaching.</summary>
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The feed's own staleness bound; beyond it advice would be about the past.</summary>
    public double MaxLagSeconds { get; init; } = 0.5;

    /// <summary>The pipeline runs ~10 Hz; below this it is wedged, not quiet.</summary>
    public double MinFps { get; init; } = 4.0;
}

/// <summary>
/// The decision loop. Everything here is plumbing and safety; game sense lives
/// in the policy. The tool observes and advises only — it consumes the feed and
/// prints coaching feedback (and optionally records the ghost cursor for the
/// viewer). It drives no device and sends nothing to the game. The one rule:
/// any doubt about the feed — disconnect, gap, climbing lag, collapsing fps,
/// silence — pauses coaching rather than advising off stale state.
/// </summary>
public sealed class Reactor(
    FeedClient feed, IPolicy policy, ReactorOptions options, TextWriter? log = null, GhostTrace? trace = null,
    CoachServer? coach = null)
{
    private static readonly TimeSpan HealthLogInterval = TimeSpan.FromSeconds(5);

    private long _lastSeq = -1;
    private bool _blind = true;   // until the first healthy frame arrives
    private bool _paused;
    private readonly List<double> _latencySamples = [];
    private DateTime _lastHealthLog = DateTime.UtcNow;

    public async Task RunAsync(CancellationToken ct)
    {
        var meta = await feed.GetMetaAsync(ct);
        if (meta.Schema > FeedJson.MaxSchema)
            throw new InvalidOperationException(
                $"Feed schema {meta.Schema} is newer than this reactor understands ({FeedJson.MaxSchema})");
        Log($"feed: {meta.Source} {meta.Width}x{meta.Height}, game_time={meta.HasGameTime} " +
            $"liveness={meta.HasLiveness} nameplates={meta.HasNameplates} " +
            $"world={(meta.WorldBounds is not null ? "calibrated" : "none")}");
        policy.Configure(meta);
        trace?.WriteMeta(meta);

        var feedTask = feed.RunAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            while (feed.Notices.TryRead(out var notice))
                HandleNotice(notice);

            // Latest wins: the channel holds at most one frame, but drain
            // anyway so a slow iteration never leaves us a frame behind.
            FrameEnvelope? frame = null;
            while (feed.Frames.TryRead(out var f))
                frame = f;

            if (frame is not null)
            {
                HandleFrame(frame);
                continue;
            }

            var frameWait = feed.Frames.WaitToReadAsync(ct).AsTask();
            var noticeWait = feed.Notices.WaitToReadAsync(ct).AsTask();
            var completed = await Task.WhenAny(frameWait, noticeWait, Task.Delay(options.FrameTimeout, ct));
            if (completed != frameWait && completed != noticeWait)
                PauseBecause($"no frame for {options.FrameTimeout.TotalMilliseconds:0}ms");
        }

        await feedTask;
    }

    private void HandleFrame(FrameEnvelope frame)
    {
        if (frame.Seq < _lastSeq)
        {
            // A restarted run or replay; sequence numbers are transport-scoped.
            Log($"feed: seq went backwards ({_lastSeq} -> {frame.Seq}), treating as a new run");
            _blind = true;
        }
        _lastSeq = frame.Seq;

        if (frame.Lag is > 0 && frame.Lag > options.MaxLagSeconds)
        {
            PauseBecause($"lag {frame.Lag:0.000}s");
            return;
        }
        if (frame.Fps is > 0 && frame.Fps < options.MinFps)
        {
            PauseBecause($"fps collapsed to {frame.Fps:0.0}");
            return;
        }

        SampleHealth(frame);

        if (_blind)
        {
            // Frames are self-contained, so the first healthy one after any
            // doubt is a complete resync baseline.
            policy.Resync(frame);
            _blind = false;
            _paused = false;
            Log($"resynced at video_time={frame.VideoTime:0.000} seq={frame.Seq}");
            coach?.PublishStatus("coaching");
            return;
        }

        Apply(policy.OnFrame(frame), frame.VideoTime, frame.GameTime);
    }

    /// <summary>
    /// End-to-end staleness: capture at the vision layer → this decision,
    /// measured from captured_at against our own clock (same machine).
    /// </summary>
    private void SampleHealth(FrameEnvelope frame)
    {
        if (frame.CapturedAt is { } capturedAt)
            _latencySamples.Add(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - capturedAt);

        var now = DateTime.UtcNow;
        if (now - _lastHealthLog < HealthLogInterval)
            return;
        _lastHealthLog = now;
        if (_latencySamples.Count > 0)
        {
            _latencySamples.Sort();
            var p50 = _latencySamples[_latencySamples.Count / 2];
            var max = _latencySamples[^1];
            Log($"health: e2e latency p50={p50 * 1000:0}ms max={max * 1000:0}ms " +
                $"over {_latencySamples.Count} frames, fps={frame.Fps?.ToString("0.0") ?? "?"} dropped={frame.Dropped}");
            _latencySamples.Clear();
        }
    }

    private void HandleNotice(FeedNotice notice)
    {
        switch (notice)
        {
            case EventNotice(var evt):
                Log($"event: {evt.Kind} {evt.Team}/{evt.Champion ?? $"track {evt.TrackId}"} " +
                    $"at video_time={evt.VideoTime:0.000}");
                // Rosters are durable identity, not advice about a moment, so
                // the blind gate below does not apply to them.
                if (evt.Kind == EventKind.Roster)
                    coach?.PublishRoster(evt);
                // Events that arrive while blind predate the resync baseline;
                // advising on them would mean advising on a past we cannot see.
                if (!_blind)
                    Apply(policy.OnEvent(evt), evt.VideoTime, evt.GameTime);
                break;
            case GapNotice(var from, var to):
                PauseBecause($"feed gap, lost ids {from}..{to}");
                break;
            case FeedLost(var reason):
                PauseBecause($"feed lost: {reason}");
                break;
            case FeedConnected(var resumed):
                Log(resumed ? "feed reconnected (resuming)" : "feed connected");
                break;
        }
    }

    private void PauseBecause(string reason)
    {
        if (!_blind)
            policy.Resync(null);
        _blind = true;
        if (!_paused)
        {
            _paused = true;
            Log($"coaching paused: {reason}");
            coach?.PublishStatus("paused", reason);
        }
    }

    private void Apply(GhostCursor? cursor, double videoTime, int? gameTime)
    {
        if (cursor is { } c)
        {
            trace?.WriteMove(videoTime, c);
            coach?.PublishMove(videoTime, c, gameTime);
        }
        foreach (var note in policy.DrainNotes())
        {
            Coach($"glance[p{note.Priority}]: {note.Reason}");
            trace?.WriteGlance(note);
            coach?.PublishGlance(note, gameTime);
        }
        // Cues are not glances and do not reach the trace: the ghost viewer
        // replays where attention went, and a cue is precisely the coaching
        // that has nowhere for it to go.
        foreach (var cue in policy.DrainCues())
        {
            Coach($"cue[p{cue.Priority}]: {cue.Reason}");
            coach?.PublishCue(cue, gameTime);
        }
    }

    /// <summary>A line of coaching feedback: to the console, and to the --log file if one is open.</summary>
    private void Coach(string message)
    {
        Log(message);
        log?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
    }

    private static void Log(string message) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
}
