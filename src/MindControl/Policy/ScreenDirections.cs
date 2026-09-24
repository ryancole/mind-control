namespace MindControl.Policy;

/// <summary>
/// Directions on the player's screen, named the way a coach says them.
/// Screen space throughout: x to the right, y <em>down</em>, which is the
/// space a threat's heading is in. Eight names, 45 degrees each. Pure
/// geometry: it names and measures, and decides nothing.
/// </summary>
public static class ScreenDirections
{
    private static readonly string[] Names =
        ["right", "down-right", "down", "down-left", "left", "up-left", "up", "up-right"];

    private static readonly string[] Sources =
        ["the right", "the lower right", "below", "the lower left",
         "the left", "the upper left", "above", "the upper right"];

    /// <summary>The way to go: "left", "up-right", ...</summary>
    public static string Name(double dx, double dy) => Names[Sector(dx, dy)];

    /// <summary>
    /// The screen direction of a world offset: world y grows northward and
    /// screen y grows downward, and the camera never rotates.
    /// </summary>
    public static string NameOfWorldOffset(double dx, double dy) => Name(dx, -dy);

    /// <summary>Where a thing travelling along (<paramref name="ux"/>, <paramref name="uy"/>) came from: "the upper right", "above", ...</summary>
    public static string Source(double ux, double uy) => Sources[Sector(-ux, -uy)];

    private static int Sector(double dx, double dy)
    {
        // atan2 in screen space: 0 to the right, +90 straight down.
        var degrees = Math.Atan2(dy, dx) * 180 / Math.PI;
        return (int)Math.Floor((degrees + 22.5 + 360) / 45) % 8;
    }

    /// <summary>
    /// The two unit vectors across a line travelling along (<paramref name="ux"/>,
    /// <paramref name="uy"/>). Either clears the line by the same margin; which
    /// of them a coach takes is a judgement, and not made here.
    /// </summary>
    public static ((double Dx, double Dy) A, (double Dx, double Dy) B) Across(double ux, double uy)
    {
        var length = double.Hypot(ux, uy);
        ux /= length;
        uy /= length;
        return ((-uy, ux), (uy, -ux));
    }

    /// <summary>
    /// How much of a screen direction points toward the player's own base:
    /// +1 straight at it, -1 straight away. Blue is always the local team,
    /// its base is the bottom-left of the map, and the camera never rotates,
    /// so toward home is down-left on the screen.
    /// </summary>
    public static double TowardBase(double dx, double dy)
    {
        var length = double.Hypot(dx, dy);
        return length == 0 ? 0 : (-dx + dy) / (length * Math.Sqrt(2));
    }
}
