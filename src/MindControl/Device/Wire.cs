namespace MindControl.Device;

/// <summary>Wire message types, host→device below 0x80, device→host above.</summary>
public enum MsgType : byte
{
    Panic = 0x00,
    KeyDown = 0x01,
    KeyUp = 0x02,
    MouseMove = 0x03,
    MouseBtn = 0x04,
    MouseWheel = 0x05,
    ScreenSize = 0x06,
    Ping = 0x07,
    Pong = 0x80,
    Nack = 0x81,
}

/// <summary>
/// The misdirection wire protocol: [0xAB][type][len][payload 0..255][sum],
/// sum = (type + len + payload bytes) &amp; 0xFF, little-endian payloads.
/// Pinned byte-for-byte against misdirection's etc/protocol-vectors.json.
/// </summary>
public static class Wire
{
    public const byte Sof = 0xAB;

    /// <summary>The firmware rejects payloads over 16 bytes with NACK(3).</summary>
    public const int MaxPayload = 16;

    public static byte[] Encode(Intent intent) => intent switch
    {
        PanicIntent => Frame(MsgType.Panic, []),
        PingIntent => Frame(MsgType.Ping, []),
        KeyDown k => Frame(MsgType.KeyDown, [k.Usage]),
        KeyUp k => Frame(MsgType.KeyUp, [k.Usage]),
        MouseMove m => Frame(MsgType.MouseMove, [Lo(m.X), Hi(m.X), Lo(m.Y), Hi(m.Y)]),
        MouseButtons b => Frame(MsgType.MouseBtn, [b.Mask]),
        MouseWheel w => Frame(MsgType.MouseWheel, [unchecked((byte)w.Vert), unchecked((byte)w.Horiz)]),
        ScreenSize s => Frame(MsgType.ScreenSize, [Lo(s.Width), Hi(s.Width), Lo(s.Height), Hi(s.Height)]),
        _ => throw new ArgumentException($"No wire encoding for {intent.GetType().Name}"),
    };

    private static byte Lo(ushort v) => (byte)(v & 0xFF);
    private static byte Hi(ushort v) => (byte)(v >> 8);

    private static byte[] Frame(MsgType type, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[4 + payload.Length];
        frame[0] = Sof;
        frame[1] = (byte)type;
        frame[2] = (byte)payload.Length;
        payload.CopyTo(frame.AsSpan(3));
        int sum = (byte)type + payload.Length;
        foreach (var b in payload)
            sum += b;
        frame[^1] = (byte)sum;
        return frame;
    }
}
