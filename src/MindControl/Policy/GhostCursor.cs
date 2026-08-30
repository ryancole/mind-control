namespace MindControl.Policy;

/// <summary>
/// Where the coaching overlay should place the ghost's attention on the
/// player's own minimap, in their screen pixels. This is a visualization cue —
/// the tool renders and logs it so a human can see where a good player would be
/// looking. It is never sent as input to the game or to any device.
/// </summary>
public sealed record GhostCursor(ushort X, ushort Y);
