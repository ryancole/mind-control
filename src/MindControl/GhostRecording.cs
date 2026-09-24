using MindControl.Policy;
using Misdirection.Client;

namespace MindControl;

/// <summary>
/// Records the ghost's input -- what the coach would have done with the mouse
/// and keyboard -- as a misdirection protocol file (<c>.msdr</c>), one frame per
/// message, in the order the coaching produced them. The file opens with a
/// <see cref="ScreenSizeMessage"/>, as a device session would, so the mouse
/// coordinates that follow are anchored to the screen they were meant for.
///
/// <para>This is a recording, not a connection. Nothing here opens a port or
/// touches a device; the file is the demonstration, kept in the wire format so
/// it needs no translation later. The format carries no timestamps -- a frame
/// is only what to do, never when -- so the ghost trace (<see cref="GhostTrace"/>)
/// remains the record of timing.</para>
/// </summary>
public sealed class GhostRecording : IDisposable
{
    private readonly ProtocolFileWriter _writer;

    private GhostRecording(ProtocolFileWriter writer) => _writer = writer;

    /// <summary>
    /// Opens <paramref name="path"/> for appending, creating it (and its
    /// directory) if needed, and writes the screen size the coordinates that
    /// follow are in. Appending to a file from an earlier run is fine: each run
    /// restates its screen size, so a reader always knows which one applies.
    /// </summary>
    public static GhostRecording Append(string path, ushort screenWidth, ushort screenHeight)
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
        return new GhostRecording(writer);
    }

    /// <summary>Frames written through this recording, the screen size included.</summary>
    public long FramesWritten => _writer.FramesWritten;

    /// <summary>The ghost's attention moved: a mouse move to where it now sits.</summary>
    public void Move(GhostCursor cursor) => Write(new MouseMoveMessage(cursor.X, cursor.Y));

    /// <summary>
    /// The coach pressed a key: a down and an up, back to back. The format has
    /// no timing, so a tap is the only press there is; a held key would need
    /// the trace to say how long, and no policy holds one.
    /// </summary>
    public void Press(KeyPress key)
    {
        var usage = UsageOf(key.Key);
        Write(new KeyDownMessage(usage));
        Write(new KeyUpMessage(usage));
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
    /// Any input the coach would have made. <see cref="Move"/> and
    /// <see cref="Press"/> are the callers; button changes would come through
    /// here too once a policy has reason to demonstrate them.
    /// </summary>
    public void Write(Message message)
    {
        _writer.Write(message);
        // Flushed per frame, like the trace and the log: a run ends with
        // Ctrl-C, and the last thing the ghost did should be on disk by then.
        _writer.Flush();
    }

    public void Dispose() => _writer.Dispose();
}
