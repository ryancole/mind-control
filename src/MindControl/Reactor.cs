using MindControl.Feed;
using MindControl.Policy;
using Misdirection.Client;

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
/// prints coaching feedback (and optionally records the ghost's input, and
/// when it happened for the viewer). It drives no device and sends nothing to the game. The one rule:
/// any doubt about the feed — disconnect, gap, climbing lag, collapsing fps,
/// silence — pauses coaching rather than advising off stale state.
/// The optional <see cref="GhostRecording"/> keeps the ghost's input -- key
/// presses and steps -- in the misdirection wire format; it is a file, and
/// this loop never opens a device. What it wrote for a key or a step rides
/// with that line of coaching -- plain text on the
/// console, data on the stream and in the trace -- so the log reads as the
/// demonstration and not only the advice.
/// </summary>
public sealed class Reactor(
    FeedClient feed, IPolicy policy, ReactorOptions options, TextWriter? log = null, GhostTrace? trace = null,
    CoachServer? coach = null, GhostRecording? recording = null)
{
    private static readonly TimeSpan HealthLogInterval = TimeSpan.FromSeconds(5);

    private long _lastSeq = -1;
    private int? _game;           // the match the policy is coaching; null until the first frame
    private bool _blind = true;   // until the first healthy frame arrives
    private bool _paused;
    private readonly List<double> _latencySamples = [];
    private DateTime _lastHealthLog = DateTime.UtcNow;

    public async Task RunAsync(CancellationToken ct)
    {
        var meta = await feed.GetMetaAsync(ct);
        FeedJson.EnsureSupported(meta);
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
        var newRun = frame.Seq < _lastSeq;
        if (newRun)
        {
            // A restarted run or replay; sequence numbers are transport-scoped.
            Log(Tone.Error, $"feed: seq went backwards ({_lastSeq} -> {frame.Seq}), treating as a new run");
            _blind = true;
        }
        _lastSeq = frame.Seq;

        // The envelope's game is the backstop for a new_game event that was
        // filtered out or lost to a gap. Within a run it never goes down, so
        // a frame from an earlier game is one the latest-wins mailbox still
        // held when the event overtook it: stale, and dropped. A new run may
        // start at any game.
        if (frame.Game != _game)
        {
            if (_game is null)
                _game = frame.Game;
            else if (frame.Game < _game && !newRun)
                return;
            else
                BeginGame(frame.Game, frame.VideoTime);
        }

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

        policy.OnFrame(frame);
        Apply(frame.GameTime);
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
            var activity = policy.DrainActivity() is { } said ? $"; coach: {said}" : "";
            Log($"health: e2e latency p50={p50 * 1000:0}ms max={max * 1000:0}ms " +
                $"over {_latencySamples.Count} frames, fps={frame.Fps?.ToString("0.0") ?? "?"} dropped={frame.Dropped}{activity}");
            _latencySamples.Clear();
        }
    }

    private void HandleNotice(FeedNotice notice)
    {
        switch (notice)
        {
            case EventNotice({ Kind: EventKind.NewGame } evt):
                // Before any other event on the new match's first frame, so
                // nothing about it reaches the policy ahead of the reset. A
                // game the envelope already moved to is not begun twice.
                if (evt.Game is { } game && (_game is null || game > _game))
                    BeginGame(game, evt.VideoTime);
                break;
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
                {
                    policy.OnEvent(evt);
                    Apply(evt.GameTime);
                }
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

    /// <summary>
    /// A new match: the policy forgets the last one whole, and the next
    /// healthy frame is its baseline, as after a gap. Events that come
    /// before that frame are dropped as they are while blind; the frame
    /// carries their state (names, turrets, a waiting point) again.
    /// Coaching is not paused: the feed is healthy, only the game changed.
    /// </summary>
    private void BeginGame(int game, double videoTime)
    {
        Log($"feed: game {game} began at video_time={videoTime:0.000}; forgetting game {_game}");
        _game = game;
        policy.NewGame(game);
        _blind = true;
        Apply(null);
        coach?.PublishGame(game, videoTime);
    }

    private void PauseBecause(string reason)
    {
        if (!_blind)
            policy.Resync(null);
        _blind = true;
        if (!_paused)
        {
            _paused = true;
            Log(Tone.Error, $"coaching paused: {reason}");
            coach?.PublishStatus("paused", reason);
        }
    }

    private void Apply(int? gameTime)
    {
        // A cue reaches neither the recording nor the trace: it is coaching
        // in words only, with nothing for the hands to do.
        foreach (var cue in policy.DrainCues())
        {
            Coach($"cue[p{cue.Priority}]: {cue.Reason}", tone: cue.Failure ? Tone.Error : Tone.Advice);
            coach?.PublishCue(cue, gameTime);
        }
        // A key press is the keyboard half of the demonstration, so unlike a
        // cue it does reach the recording (as a tap, after the gap since the
        // last input) and the trace (for its absolute video time and reason).
        foreach (var key in policy.DrainKeys())
        {
            var input = recording?.Press(key);
            Coach($"key[p{key.Priority}]: {key.Sentence}", input);
            trace?.WriteKey(key, input);
            coach?.PublishKey(key, gameTime, input);
        }
        // A step is the movement half: it reaches the recording as a
        // right-click on the ground and the trace for its video time. A dodge is
        // stamped at the bolt's first sighting, which is before the event
        // that reports it, so in the trace it lands out of order and a
        // reader sorts by video_time; the recording, one sequence with no
        // negative gaps, puts it right after whatever it wrote last.
        foreach (var step in policy.DrainMoves())
        {
            var input = recording?.Step(step);
            Coach($"step[p{step.Priority}]: {step.Sentence}", input);
            trace?.WriteStep(step, input);
            coach?.PublishStep(step, gameTime, input);
        }
    }

    /// <summary>
    /// A line of coaching feedback: to the console, and to the --log file if
    /// one is open. <paramref name="input"/> is what the recording wrote for
    /// it, named plainly after the advice; null when nothing was recorded.
    /// Advice is green on the console and a failure red; the file stays plain.
    /// </summary>
    private void Coach(string message, IReadOnlyList<Message>? input = null, Tone tone = Tone.Advice)
    {
        if (input is not null)
            message = $"{message}  recorded: {GhostRecording.Show(input)}";
        Log(tone, message);
        log?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
    }

    private static void Log(string message) => Log(Tone.Plain, message);

    private static void Log(Tone tone, string message) =>
        ConsoleTone.WriteLine(tone, $"{DateTime.Now:HH:mm:ss.fff} {message}");
}
