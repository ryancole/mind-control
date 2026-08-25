using MindControl.Feed;

namespace MindControl.Policy;

/// <summary>Where the minimap sits on the coached player's screen, in their screen pixels.</summary>
public sealed record MinimapRect(double X, double Y, double Width, double Height);

/// <summary>
/// Maps Summoner's Rift world units onto the player's on-screen minimap. Pure
/// math, so policies that use it stay replay-testable. World y grows northward
/// while screen y grows downward, hence the flip.
/// </summary>
public sealed class ScreenMap(WorldBounds bounds, MinimapRect minimap)
{
    /// <summary>Null when the feed has no world calibration; callers degrade gracefully.</summary>
    public static ScreenMap? FromMeta(Meta meta, MinimapRect minimap) =>
        meta.WorldBounds is { } bounds ? new ScreenMap(bounds, minimap) : null;

    public (ushort X, ushort Y) WorldToScreen(double worldX, double worldY)
    {
        var fx = (worldX - bounds.MinX) / (bounds.MaxX - bounds.MinX);
        var fy = (worldY - bounds.MinY) / (bounds.MaxY - bounds.MinY);
        var x = minimap.X + Math.Clamp(fx, 0, 1) * minimap.Width;
        var y = minimap.Y + (1 - Math.Clamp(fy, 0, 1)) * minimap.Height;
        return ((ushort)Math.Round(x), (ushort)Math.Round(y));
    }
}
