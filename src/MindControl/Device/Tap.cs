namespace MindControl.Device;

public static class DeviceLinkExtensions
{
    /// <summary>
    /// A press the game's input sampling reliably sees; back-to-back wire
    /// frames would release within a millisecond.
    /// </summary>
    public const int DefaultHoldMs = 40;

    /// <summary>
    /// A tap — press, hold, release — is an idiom over the wire protocol, not
    /// a frame of its own: the board only knows KEY_DOWN and KEY_UP. Blocks
    /// the caller for the hold; taps are rare (a demo keypress, an eventual
    /// coached input), never per-frame traffic.
    /// </summary>
    public static void Tap(this IDeviceLink link, byte usage, int holdMs = DefaultHoldMs)
    {
        link.Send(new KeyDown(usage));
        if (holdMs > 0)
            Thread.Sleep(holdMs);
        link.Send(new KeyUp(usage));
    }
}
