using MindControl.Feed;

namespace MindControl;

/// <summary>
/// Where the minimap sits on the coached player's screen, in their screen
/// pixels, and where a point of the map falls on it. A walk across the map is
/// ordered from here: a player going to lane right-clicks the minimap, one
/// click for the whole trip, and so does the ghost. Pure geometry: it places
/// and decides nothing. The League minimap is square and shows the whole of
/// Summoner's Rift, so the feed's world bounds map onto it edge to edge, with
/// world y (north) flipped to screen y (down).
/// </summary>
public sealed record MinimapRect(double X, double Y, double Width, double Height)
{
    /// <summary>
    /// A placeholder for a screen nobody has calibrated: the default League
    /// HUD's minimap, a 300px square in the bottom-right corner at 1080p,
    /// scaled with the screen's height. etc/minimap-calibrator.html reads
    /// the real one off a screenshot, for <c>--minimap</c>.
    /// </summary>
    public static MinimapRect Default(ushort screenWidth, ushort screenHeight)
    {
        var side = Math.Round(300.0 * screenHeight / 1080);
        return new(screenWidth - side, screenHeight - side, side, side);
    }

    /// <summary>The screen pixel a point of the map falls on, clamped to the minimap's edges.</summary>
    public (ushort X, ushort Y) Place(WorldBounds bounds, double worldX, double worldY)
    {
        var fx = (worldX - bounds.MinX) / (bounds.MaxX - bounds.MinX);
        var fy = (worldY - bounds.MinY) / (bounds.MaxY - bounds.MinY);
        var x = X + Math.Clamp(fx, 0, 1) * Width;
        var y = Y + (1 - Math.Clamp(fy, 0, 1)) * Height;
        return ((ushort)Math.Round(x), (ushort)Math.Round(y));
    }

    /// <summary>As the flag takes it: "x,y,w,h".</summary>
    public override string ToString() => $"{X:0},{Y:0},{Width:0},{Height:0}";
}
