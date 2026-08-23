using MindControl.Device;
using MindControl.Feed;
using MindControl.Policy;

namespace MindControl;

public sealed record ReactorOptions
{
    public ushort ScreenWidth { get; init; } = 1920;
    public ushort ScreenHeight { get; init; } = 1080;

    /// <summary>No frame for this long means we are blind: PANIC.</summary>
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The feed's own staleness bound; beyond it we are acting on the past.</summary>
    public double MaxLagSeconds { get; init; } = 0.5;

    /// <summary>The pipeline runs ~10 Hz; below this it is wedged, not quiet.</summary>
    public double MinFps { get; init; } = 4.0;
}

/// <summary>
/// The decision loop. Everything here is plumbing and safety; game sense lives
/// in the policy. The one non-negotiable rule: any doubt about the feed —
/// disconnect, gap, climbing lag, collapsing fps, silence — sends PANIC before
/// anything else, because a held key while blind is the failure mode.
/// </summary>
public sealed class Reactor(FeedClient feed, IDeviceLink link, IPolicy policy, ReactorOptions options)
{
    private long _lastSeq = -1;
    private bool _blind = true;   // until the first healthy frame arrives
    private bool _panicReported;
    private DateTime _lastDisarmedLog = DateTime.MinValue;

    public async Task RunAsync(CancellationToken ct)
    {
        link.MessageReceived += OnDeviceMessage;
        await HandshakeAsync(ct);

        var meta = await feed.GetMetaAsync(ct);
        if (meta.Schema > FeedJson.MaxSchema)
            throw new InvalidOperationException(
                $"Feed schema {meta.Schema} is newer than this reactor understands ({FeedJson.MaxSchema})");
        Log($"feed: {meta.Source} {meta.Width}x{meta.Height}, game_time={meta.HasGameTime} " +
            $"liveness={meta.HasLiveness} nameplates={meta.HasNameplates}");

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
                PanicBecause($"no frame for {options.FrameTimeout.TotalMilliseconds:0}ms");
        }

        await feedTask;
    }

    /// <summary>
    /// PING until the board answers PONG (it answers even while disarmed),
    /// then SCREEN_SIZE before any MOUSE_MOVE can go out.
    /// </summary>
    private async Task HandshakeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pong = _pongSignal = new TaskCompletionSource<Pong>(TaskCreationOptions.RunContinuationsAsynchronously);
            link.Send(Intent.Ping);
            var completed = await Task.WhenAny(pong.Task, Task.Delay(TimeSpan.FromSeconds(2), ct));
            if (completed == pong.Task)
            {
                Log($"device: PONG v{pong.Task.Result.Version}");
                break;
            }
            Log("device: no PONG yet, retrying");
        }
        _pongSignal = null;
        ct.ThrowIfCancellationRequested();
        link.Send(new ScreenSize(options.ScreenWidth, options.ScreenHeight));
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
            PanicBecause($"lag {frame.Lag:0.000}s");
            return;
        }
        if (frame.Fps is > 0 && frame.Fps < options.MinFps)
        {
            PanicBecause($"fps collapsed to {frame.Fps:0.0}");
            return;
        }

        if (_blind)
        {
            // Frames are self-contained, so the first healthy one after any
            // doubt is a complete resync baseline.
            policy.Resync(frame);
            _blind = false;
            _panicReported = false;
            Log($"resynced at video_time={frame.VideoTime:0.000} seq={frame.Seq}");
            return;
        }

        Send(policy.OnFrame(frame));
    }

    private void HandleNotice(FeedNotice notice)
    {
        switch (notice)
        {
            case EventNotice(var evt):
                Log($"event: {evt.Kind} {evt.Team}/{evt.Champion ?? $"track {evt.TrackId}"} " +
                    $"at video_time={evt.VideoTime:0.000}");
                // Events that arrive while blind predate the resync baseline;
                // acting on them would mean acting on a past we cannot see.
                if (!_blind)
                    Send(policy.OnEvent(evt));
                break;
            case GapNotice(var from, var to):
                PanicBecause($"feed gap, lost ids {from}..{to}");
                break;
            case FeedLost(var reason):
                PanicBecause($"feed lost: {reason}");
                break;
            case FeedConnected(var resumed):
                Log(resumed ? "feed reconnected (resuming)" : "feed connected");
                break;
        }
    }

    private void PanicBecause(string reason)
    {
        // Re-sent every timeout while blind — 4 bytes of cheap insurance in
        // case an earlier PANIC was lost — but logged once per blind episode.
        link.Send(Intent.Panic);
        if (!_blind)
            policy.Resync(null);
        _blind = true;
        if (!_panicReported)
        {
            _panicReported = true;
            Log($"PANIC: {reason}");
        }
    }

    private void Send(IReadOnlyList<Intent> intents)
    {
        foreach (var intent in intents)
            link.Send(intent);
    }

    private volatile TaskCompletionSource<Pong>? _pongSignal;

    private void OnDeviceMessage(DeviceMessage message)
    {
        switch (message)
        {
            case Pong pong when _pongSignal is { } signal:
                signal.TrySetResult(pong);
                break;
            case Pong pong:
                // Unsolicited PONG means the board just reset: its screen
                // scaling and key state are gone.
                Log($"device: rebooted (PONG v{pong.Version}), re-sending SCREEN_SIZE");
                link.Send(new ScreenSize(options.ScreenWidth, options.ScreenHeight));
                break;
            case Nack { Reason: Nack.Disarmed }:
                // The arm gate (pin 2) is the physical safety; disarmed is a
                // normal state, worth a periodic note and nothing more.
                if (DateTime.UtcNow - _lastDisarmedLog > TimeSpan.FromSeconds(10))
                {
                    _lastDisarmedLog = DateTime.UtcNow;
                    Log("device: disarmed (NACK 4), input is being dropped");
                }
                break;
            case Nack nack:
                Log($"device: NACK reason {nack.Reason}");
                break;
            case UnknownDeviceMessage unknown:
                Log($"device: unknown message type 0x{unknown.Type:X2}");
                break;
        }
    }

    private static void Log(string message) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
}
