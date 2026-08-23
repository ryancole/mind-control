namespace MindControl.Device;

/// <summary>
/// HID keyboard usage codes (usage page 0x07). This mapping is the host's job:
/// the wire carries these raw bytes, never ASCII and never Windows VK codes.
/// </summary>
public static class HidUsage
{
    public const byte A = 0x04;
    public const byte B = 0x05;
    public const byte C = 0x06;
    public const byte D = 0x07;
    public const byte E = 0x08;
    public const byte F = 0x09;
    public const byte G = 0x0A;
    public const byte H = 0x0B;
    public const byte I = 0x0C;
    public const byte J = 0x0D;
    public const byte K = 0x0E;
    public const byte L = 0x0F;
    public const byte M = 0x10;
    public const byte N = 0x11;
    public const byte O = 0x12;
    public const byte P = 0x13;
    public const byte Q = 0x14;
    public const byte R = 0x15;
    public const byte S = 0x16;
    public const byte T = 0x17;
    public const byte U = 0x18;
    public const byte V = 0x19;
    public const byte W = 0x1A;
    public const byte X = 0x1B;
    public const byte Y = 0x1C;
    public const byte Z = 0x1D;

    public const byte Digit1 = 0x1E;
    public const byte Digit2 = 0x1F;
    public const byte Digit3 = 0x20;
    public const byte Digit4 = 0x21;
    public const byte Digit5 = 0x22;
    public const byte Digit6 = 0x23;
    public const byte Digit7 = 0x24;
    public const byte Digit8 = 0x25;
    public const byte Digit9 = 0x26;
    public const byte Digit0 = 0x27;

    public const byte Enter = 0x28;
    public const byte Escape = 0x29;
    public const byte Backspace = 0x2A;
    public const byte Tab = 0x2B;
    public const byte Space = 0x2C;

    public const byte F1 = 0x3A;
    public const byte F2 = 0x3B;
    public const byte F3 = 0x3C;
    public const byte F4 = 0x3D;
    public const byte F5 = 0x3E;
    public const byte F6 = 0x3F;
    public const byte F7 = 0x40;
    public const byte F8 = 0x41;
    public const byte F9 = 0x42;
    public const byte F10 = 0x43;
    public const byte F11 = 0x44;
    public const byte F12 = 0x45;

    public const byte RightArrow = 0x4F;
    public const byte LeftArrow = 0x50;
    public const byte DownArrow = 0x51;
    public const byte UpArrow = 0x52;

    // 0xE0..0xE7 map to modifier bits in the firmware (1 << (usage - 0xE0)).
    public const byte LeftCtrl = 0xE0;
    public const byte LeftShift = 0xE1;
    public const byte LeftAlt = 0xE2;
    public const byte LeftGui = 0xE3;
    public const byte RightCtrl = 0xE4;
    public const byte RightShift = 0xE5;
    public const byte RightAlt = 0xE6;
    public const byte RightGui = 0xE7;

    /// <summary>US-layout mapping for the characters a policy plausibly types.</summary>
    public static bool TryFromChar(char c, out byte usage)
    {
        usage = c switch
        {
            >= 'a' and <= 'z' => (byte)(A + (c - 'a')),
            >= 'A' and <= 'Z' => (byte)(A + (c - 'A')),
            >= '1' and <= '9' => (byte)(Digit1 + (c - '1')),
            '0' => Digit0,
            ' ' => Space,
            '\n' => Enter,
            '\t' => Tab,
            _ => 0,
        };
        return usage != 0;
    }
}
