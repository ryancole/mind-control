using MindControl.Feed;
using MindControl.Policy;
using Misdirection.Client;

namespace MindControl;

/// <summary>
/// Records the ghost's input -- what the coach would have done with the mouse
/// and keyboard: key presses, steps and walks -- as a misdirection protocol
/// file (<c>.msdr</c>), one frame per message, in the order the coaching
/// produced them. The file opens with a
/// <see cref="ScreenSizeMessage"/>, as a device session would, so the mouse
/// coordinates that follow are anchored to the screen they were meant for.
///
/// <para>This is a recording, not a connection. Nothing here opens a port or
/// touches a device; the file is the demonstration, kept in the wire format so
/// it needs no translation later.</para>
///
/// <para>Timing goes in the file too, as the format's <c>FILE_DELAY</c>
/// records (<see cref="DelayMessage"/>): before each press or step, the
/// video time that passed since the previous one, so the file replays at the
/// pace the coach acted. The clock is the VOD's, not the wall's -- a replay at
/// speed 4 records the same gaps as one at speed 1 -- and it starts at the
/// first press or step of a run, since nothing before it is anchored to the
/// video. A step is stamped at the bolt's first sighting, which can be
/// earlier than the press written before it; the file is one sequence and a
/// gap cannot be negative, so such a step follows at no gap and the clock
/// does not move back. The ghost trace (<see cref="GhostTrace"/>) keeps the
/// absolute video time of every press and step, out-of-order stamps included.
/// </para>
///
/// <para>Each of <see cref="Press"/> and <see cref="Step"/>
/// hands back the frames it wrote, the delay before them included, so the
/// coaching output can show not just "pressed Q" but the KeyDown and KeyUp
/// that went into the file for it:
/// <see cref="Show(IEnumerable{Message})"/> as plain text for the console,
/// <see cref="AsData(IEnumerable{Message})"/> as data for the stream and the
/// trace. Neither decorates; an icon for the hand is a front end's choice.</para>
/// </summary>
public sealed class GhostRecording : IDisposable
{
    /// <summary>
    /// How far from the player's model a step's click lands, in screen
    /// pixels. Far enough to clear a bolt's line with a margin (the fixture's
    /// dodges moved 40–74px across it), near enough to still be a sidestep
    /// and not a retreat. Unmeasured beyond that: nothing has yet replayed a
    /// recording into a game. A walk across the map is not sized by this: it
    /// is ordered from the minimap, however far it goes.
    /// </summary>
    public const int StepPx = 200;

    private readonly ProtocolFileWriter _writer;
    private readonly ushort _width, _height;
    private readonly (ushort X, ushort Y) _anchor;
    // The world bounds the feed's positions are in, once its meta has said;
    // until then a walk has nowhere on the minimap to land.
    private WorldBounds? _bounds;
    // Video time of the last press or step, or null before a run's first:
    // the reference the next delay is measured from.
    private double? _lastVideoTime;

    private GhostRecording(
        ProtocolFileWriter writer, ushort width, ushort height, (ushort X, ushort Y) anchor, MinimapRect minimap)
    {
        _writer = writer;
        _width = width;
        _height = height;
        _anchor = anchor;
        Minimap = minimap;
        Header = Show(new ScreenSizeMessage(width, height));
    }

    /// <summary>The <see cref="ScreenSizeMessage"/> this run opened with, as <see cref="Show(IEnumerable{Message})"/> prints it.</summary>
    public string Header { get; }

    /// <summary>Where the minimap sits on the player's screen: where a walk's right-click lands.</summary>
    public MinimapRect Minimap { get; }

    /// <summary>
    /// Opens <paramref name="path"/> for appending, creating it (and its
    /// directory) if needed, and writes the screen size the coordinates that
    /// follow are in. Appending to a file from an earlier run is fine: each run
    /// restates its screen size, so a reader always knows which one applies.
    /// <paramref name="playerAnchor"/> is where the player's model sits on
    /// their screen, which a step is taken from; the camera is locked, so it
    /// is one place, and the default is the screen's centre.
    /// <paramref name="minimap"/> is where the minimap sits, which a walk is
    /// ordered from; the default is the stock HUD's corner
    /// (<see cref="MinimapRect.Default"/>), a placeholder until a screenshot
    /// has been through the calibrator.
    /// </summary>
    public static GhostRecording Append(
        string path, ushort screenWidth, ushort screenHeight, (ushort X, ushort Y)? playerAnchor = null,
        MinimapRect? minimap = null)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);
        var writer = ProtocolFileWriter.Append(path);
        try
        {
            writer.Write(new ScreenSizeMessage(screenWidth, screenHeight));
            writer.Flush();
        }
        catch
        {
            writer.Dispose();
            throw;
        }
        var anchor = playerAnchor ?? ((ushort)(screenWidth / 2), (ushort)(screenHeight / 2));
        return new GhostRecording(
            writer, screenWidth, screenHeight, anchor, minimap ?? MinimapRect.Default(screenWidth, screenHeight));
    }

    /// <summary>
    /// The world bounds the feed's positions are in, from its meta: what a
    /// walk's destination is placed on the minimap with. Before this is
    /// called, or with null (an uncalibrated feed, which produces no walks),
    /// a walk that does arrive is taken as a step on the ground its way.
    /// </summary>
    public void Calibrate(WorldBounds? bounds) => _bounds = bounds;

    /// <summary>Frames written through this recording, the screen size and any delays included.</summary>
    public long FramesWritten => _writer.FramesWritten;

    /// <summary>
    /// Video time of the last press or step, from which the next delay is
    /// measured; null before a run's first. Never moves backwards.
    /// </summary>
    public double? LastVideoTime => _lastVideoTime;

    /// <summary>
    /// The coach pressed a key: a down and an up, back to back, after the gap
    /// since the last press or step. A tap is the only press there is; a
    /// held key would need a policy to say how long, and none holds one.
    /// With <see cref="KeyPress.WithControl"/> the tap sits inside a Ctrl
    /// down and up -- the chord the game reads as a point into the ability
    /// rather than a cast of it. Ctrl goes on the wire as its own usage; the
    /// firmware folds it into the modifier byte.
    /// </summary>
    public IReadOnlyList<Message> Press(KeyPress key)
    {
        var usage = UsageOf(key.Key);
        return key.WithControl
            ? WriteAt(key.VideoTime,
                new KeyDownMessage(HidUsage.LeftControl), new KeyDownMessage(usage),
                new KeyUpMessage(usage), new KeyUpMessage(HidUsage.LeftControl))
            : WriteAt(key.VideoTime, new KeyDownMessage(usage), new KeyUpMessage(usage));
    }

    /// <summary>
    /// The coach stepped: a move to the ground <see cref="StepPx"/> from the
    /// player's model in the step's direction, then a right-click there -- a
    /// press and a release, as with a key. The click is a move order, which
    /// is how a step is taken in the game; the cursor is left where it was
    /// clicked, as a player's would be, until the next step moves it. A walk
    /// (a step with a <see cref="MoveStep.Destination"/>) is the same click
    /// on the minimap instead, at the place the coach is going: one order for
    /// the whole trip, which is how a player sets off for lane, and as broad
    /// as a move can be.
    /// </summary>
    public IReadOnlyList<Message> Step(MoveStep step)
    {
        var (x, y) = step.Destination is { } to && _bounds is { } bounds
            ? Minimap.Place(bounds, to.X, to.Y)
            : OnTheGround(step);
        return WriteAt(step.VideoTime,
            new MouseMoveMessage(x, y),
            new MouseButtonsMessage(MouseButtons.Right),
            new MouseButtonsMessage(MouseButtons.None));
    }

    private (ushort X, ushort Y) OnTheGround(MoveStep step) => (
        (ushort)Math.Clamp(Math.Round(_anchor.X + step.Dx * StepPx), 0, _width - 1),
        (ushort)Math.Clamp(Math.Round(_anchor.Y + step.Dy * StepPx), 0, _height - 1));

    /// <summary>
    /// The gap the file records before an input at <paramref name="videoTime"/>:
    /// the video time since the last press or step; none before a run's
    /// first, and none for a stamp earlier than the last (the clock does not
    /// move back). What <see cref="WriteAt"/> writes ahead of the frames.
    /// </summary>
    public TimeSpan DelayBefore(double videoTime) =>
        _lastVideoTime is { } last && videoTime > last ? TimeSpan.FromSeconds(videoTime - last) : TimeSpan.Zero;

    /// <summary>
    /// The keycap the coach names, as the HID usage the wire carries. Letters
    /// and digits, which is every ability and summoner slot on the default
    /// bindings; anything else is a policy naming a key this recording was
    /// not taught, and that is a bug to hear about, not a frame to guess at.
    /// </summary>
    public static byte UsageOf(string key) => key switch
    {
        [>= 'A' and <= 'Z' and var letter] => (byte)(HidUsage.A + (letter - 'A')),
        [>= 'a' and <= 'z' and var letter] => (byte)(HidUsage.A + (letter - 'a')),
        ['0'] => HidUsage.Digit0,
        [>= '1' and <= '9' and var digit] => (byte)(HidUsage.Digit1 + (digit - '1')),
        _ => throw new ArgumentException($"no HID usage known for key \"{key}\"", nameof(key)),
    };

    /// <summary>
    /// The keycap a wire usage stands for, the inverse of <see cref="UsageOf"/>,
    /// plus "Ctrl" for the modifier a chord holds; "?" for a usage this
    /// recording never writes.
    /// </summary>
    public static string KeycapOf(byte usage) => usage switch
    {
        >= HidUsage.A and <= HidUsage.Z => ((char)('A' + (usage - HidUsage.A))).ToString(),
        HidUsage.Digit0 => "0",
        >= HidUsage.Digit1 and <= HidUsage.Digit9 => ((char)('1' + (usage - HidUsage.Digit1))).ToString(),
        HidUsage.LeftControl => "Ctrl",
        _ => "?",
    };

    /// <summary>
    /// Frames as the console prints them: plain text, comma separated, one
    /// entry per frame. A key carries its keycap and the usage on the wire;
    /// a move its screen pixels; a button change the mask it set. Anything
    /// this recording does not write is shown as the library shows a frame,
    /// type and hex. No decoration: the text is the data, and a front end
    /// that wants an icon for the hand picks it from <see cref="AsData"/>.
    /// </summary>
    public static string Show(IEnumerable<Message> frames) => string.Join(", ", frames.Select(Describe));

    public static string Show(params Message[] frames) => Show((IEnumerable<Message>)frames);

    /// <summary>
    /// Frames as the stream and the trace carry them: one object per frame
    /// with a <c>type</c> naming the message (<c>key_down</c>,
    /// <c>mouse_move</c>, ...) and its fields by name, so a front end can
    /// decorate by type without parsing text.
    /// </summary>
    public static IReadOnlyList<object> AsData(IEnumerable<Message> frames) => frames.Select(AsData).ToList();

    public static object AsData(Message frame) => frame switch
    {
        KeyDownMessage k => new { Type = "key_down", Key = KeycapOf(k.Usage), k.Usage },
        KeyUpMessage k => new { Type = "key_up", Key = KeycapOf(k.Usage), k.Usage },
        MouseMoveMessage m => new { Type = "mouse_move", m.X, m.Y },
        MouseButtonsMessage b => new { Type = "mouse_buttons", Buttons = b.Buttons.ToString() },
        MouseWheelMessage w => new { Type = "mouse_wheel", w.Vertical, w.Horizontal },
        ScreenSizeMessage s => new { Type = "screen_size", s.Width, s.Height },
        DelayMessage d => new { Type = "delay", d.Microseconds },
        _ => new { Type = frame.Type.ToString().ToLowerInvariant(), Payload = Convert.ToHexString(frame.ToFrame().Payload) },
    };

    private static string Describe(Message frame) => frame switch
    {
        KeyDownMessage k => $"KeyDown {KeycapOf(k.Usage)} (0x{k.Usage:X2})",
        KeyUpMessage k => $"KeyUp {KeycapOf(k.Usage)} (0x{k.Usage:X2})",
        MouseMoveMessage m => $"MouseMove {m.X},{m.Y}",
        MouseButtonsMessage b => $"MouseButtons {b.Buttons}",
        MouseWheelMessage w => $"MouseWheel {w.Vertical},{w.Horizontal}",
        ScreenSizeMessage s => $"ScreenSize {s.Width}x{s.Height}",
        // Seconds to the millisecond: gaps between inputs are tenths to tens
        // of seconds, and a microsecond is below anything a hand can tell apart.
        DelayMessage d => $"Delay {d.Duration.TotalSeconds:0.000}s",
        _ => frame.ToFrame().ToString(),
    };

    /// <summary>
    /// Input the coach made at <paramref name="videoTime"/>: the gap since the
    /// last press or step as a <see cref="DelayMessage"/> when there is one
    /// (see <see cref="DelayBefore"/>), then the frames, in order. Returns
    /// everything written, delay first, so the caller can show what went into
    /// the file. <see cref="Press"/> and <see cref="Step"/> are the callers.
    /// </summary>
    public IReadOnlyList<Message> WriteAt(double videoTime, params Message[] messages)
    {
        var delay = DelayBefore(videoTime);
        var written = new List<Message>(messages.Length + 1);
        if (delay > TimeSpan.Zero)
        {
            // One record in practice: the writer splits a gap only past 71
            // minutes, and no two coached moments are that far apart. Read
            // back what it split rather than guess, so the frames handed
            // back are the frames in the file.
            var before = _writer.FramesWritten;
            _writer.WriteDelay(delay);
            written.AddRange(DelaysWritten(delay, _writer.FramesWritten - before));
        }
        written.AddRange(Write(messages));
        if (_lastVideoTime is null || videoTime > _lastVideoTime)
            _lastVideoTime = videoTime;
        return written;
    }

    /// <summary>
    /// The <see cref="DelayMessage"/> records <see cref="ProtocolFileWriter.WriteDelay"/>
    /// wrote for <paramref name="delay"/>: <paramref name="records"/> of them,
    /// the maximum each but the last, rounded to the microsecond as it rounds.
    /// </summary>
    private static IEnumerable<Message> DelaysWritten(TimeSpan delay, long records)
    {
        var micros = Math.DivRem(delay.Ticks, TimeSpan.TicksPerMicrosecond, out var remainder);
        if (remainder * 2 >= TimeSpan.TicksPerMicrosecond) micros++;
        for (var i = 0; i < records; i++)
        {
            var chunk = (uint)Math.Min(micros, uint.MaxValue);
            yield return new DelayMessage(chunk);
            micros -= chunk;
        }
    }

    /// <summary>
    /// Frames as given, in order, with no delay before them; returns them so
    /// the caller can show what went into the file. For input with no moment
    /// of its own, and for tests; the coach's presses and steps go through
    /// <see cref="WriteAt"/>.
    /// </summary>
    public IReadOnlyList<Message> Write(params Message[] messages)
    {
        foreach (var message in messages)
            _writer.Write(message);
        // Flushed per call, like the trace and the log: a run ends with
        // Ctrl-C, and the last thing the ghost did should be on disk by then.
        _writer.Flush();
        return messages;
    }

    public void Dispose() => _writer.Dispose();
}
