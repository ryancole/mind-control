namespace MindControl.Device;

/// <summary>
/// What the policy wants the device to do, one wire message per intent.
/// KEY_DOWN/KEY_UP stay separate (holding a modifier while dragging must be
/// possible); mouse buttons are one absolute mask so a dropped frame
/// self-corrects on the next update.
/// </summary>
public abstract record Intent
{
    public static readonly Intent Panic = new PanicIntent();
    public static readonly Intent Ping = new PingIntent();
}

public sealed record PanicIntent : Intent;

public sealed record PingIntent : Intent;

/// <summary>Usage is a HID usage code (a = 0x04), never ASCII or a VK code.</summary>
public sealed record KeyDown(byte Usage) : Intent;

public sealed record KeyUp(byte Usage) : Intent;

/// <summary>Screen pixels in the resolution last sent via <see cref="ScreenSize"/>.</summary>
public sealed record MouseMove(ushort X, ushort Y) : Intent;

/// <summary>Absolute button state: bit0 left, bit1 right, bit2 middle, bit3 back, bit4 forward.</summary>
public sealed record MouseButtons(byte Mask) : Intent;

public sealed record MouseWheel(sbyte Vert, sbyte Horiz) : Intent;

/// <summary>Must be sent before the first MOUSE_MOVE and on any resolution change.</summary>
public sealed record ScreenSize(ushort Width, ushort Height) : Intent;
