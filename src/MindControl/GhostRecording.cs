using MindControl.Policy;
using Misdirection.Client;

namespace MindControl;

/// <summary>
/// Records the ghost's input -- what the coach would have done with the mouse
/// and keyboard: glances, key presses and steps -- as a misdirection protocol
/// file (<c>.msdr</c>), one frame per message, in the order the coaching
/// produced them. The file opens with a
/// <see cref="ScreenSizeMessage"/>, as a device session would, so the mouse
/// coordinates that follow are anchored to the screen they were meant for.
///
/// <para>This is a recording, not a connection. Nothing here opens a port or
/// touches a device; the file is the demonstration, kept in the wire format so
/// it needs no translation later. The format carries no timestamps -- a frame
/// is only what to do, never when -- so the ghost trace (<see cref="GhostTrace"/>)
/// remains the record of timing.</para>
///
/// <para>Each of <see cref="Move"/>, <see cref="Press"/> and <see cref="Step"/>
/// hands back the frames it wrote, so the coaching output can show not just
/// "pressed Q" but the KeyDown and KeyUp that went into the file for it:
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
    /// recording into a game.
    /// </summary>
    public const int StepPx = 200;

    private readonly ProtocolFileWriter _writer;
    private readonly ushort _width, _height;
    private readonly (ushort X, ushort Y) _anchor;

    private GhostRecording(ProtocolFileWriter writer, ushort width, ushort height, (ushort X, ushort Y) anchor)
    {
        _writer = writer;
        _width = width;
        _height = height;
        _anchor = anchor;
        Header = Show(new ScreenSizeMessage(width, height));
    }

    /// <summary>The <see cref="ScreenSizeMessage"/> this run opened with, as <see cref="Show(IEnumerable{Message})"/> prints it.</summary>
    public string Header { get; }

    /// <summary>
    /// Opens <paramref name="path"/> for appending, creating it (and its
    /// directory) if needed, and writes the screen size the coordinates that
    /// follow are in. Appending to a file from an earlier run is fine: each run
    /// restates its screen size, so a reader always knows which one applies.
    /// <paramref name="playerAnchor"/> is where the player's model sits on
    /// their screen, which a step is taken from; the camera is locked, so it
    /// is one place, and the default is the screen's centre.
    /// </summary>
    public static GhostRecording Append(
        string path, ushort screenWidth, ushort screenHeight, (ushort X, ushort Y)? playerAnchor = null)
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
        return new GhostRecording(writer, screenWidth, screenHeight, anchor);
    }

    /// <summary>Frames written through this recording, the screen size included.</summary>
    public long FramesWritten => _writer.FramesWritten;

    /// <summary>The ghost's attention moved: a mouse move to where it now sits.</summary>
    public IReadOnlyList<Message> Move(GhostCursor cursor) => Write(new MouseMoveMessage(cursor.X, cursor.Y));

    /// <summary>
    /// The coach pressed a key: a down and an up, back to back. The format has
    /// no timing, so a tap is the only press there is; a held key would need
    /// the trace to say how long, and no policy holds one.
    /// </summary>
    public IReadOnlyList<Message> Press(KeyPress key)
    {
        var usage = UsageOf(key.Key);
        return Write(new KeyDownMessage(usage), new KeyUpMessage(usage));
    }

    /// <summary>
    /// The coach stepped: a move to the ground <see cref="StepPx"/> from the
    /// player's model in the step's direction, then a right-click there -- a
    /// press and a release, as with a key. The click is a move order, which
    /// is how a step is taken in the game; the cursor is left where it was
    /// clicked, as a player's would be, until attention moves it again.
    /// </summary>
    public IReadOnlyList<Message> Step(MoveStep step)
    {
        var x = (ushort)Math.Clamp(Math.Round(_anchor.X + step.Dx * StepPx), 0, _width - 1);
        var y = (ushort)Math.Clamp(Math.Round(_anchor.Y + step.Dy * StepPx), 0, _height - 1);
        return Write(
            new MouseMoveMessage(x, y),
            new MouseButtonsMessage(MouseButtons.Right),
            new MouseButtonsMessage(MouseButtons.None));
    }

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
    /// The keycap a wire usage stands for, the inverse of <see cref="UsageOf"/>;
    /// "?" for a usage this recording never writes.
    /// </summary>
    public static string KeycapOf(byte usage) => usage switch
    {
        >= HidUsage.A and <= HidUsage.Z => ((char)('A' + (usage - HidUsage.A))).ToString(),
        HidUsage.Digit0 => "0",
        >= HidUsage.Digit1 and <= HidUsage.Digit9 => ((char)('1' + (usage - HidUsage.Digit1))).ToString(),
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
        _ => frame.ToFrame().ToString(),
    };

    /// <summary>
    /// Any input the coach would have made, in order; returns the frames so
    /// the caller can show what went into the file. <see cref="Move"/>,
    /// <see cref="Press"/> and <see cref="Step"/> are the callers.
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
