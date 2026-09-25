namespace MindControl.Policy;

/// <summary>
/// Summoner's Rift as the coached player's own minimap shows it, in game
/// units with their base at the origin and y growing north (blue is always
/// the local team, as <see cref="ScreenDirections.TowardBase"/> assumes):
/// the three lanes as the lines their turrets lie on, the fountain, and the
/// two bases. Pure geometry, with the turret positions Riot publishes for the
/// map: it says where a point is and how far each lane is from it, and
/// decides nothing about whether anyone should be there.
/// </summary>
public static class RiftMap
{
    public static readonly string[] Lanes = ["top", "mid", "bot"];

    /// <summary>
    /// Each lane's centre line from the player's nexus to the enemy's. Mid
    /// runs through its turrets; the side lanes follow their turrets to the
    /// outer one and then bend round the map's corner on the inside of the
    /// enemy's outer turret, where the fixture recording shows the laning
    /// happening (the bot lane's cloud of positions runs from about
    /// (12300, 1600) to (13300, 4200), a few hundred units inside the
    /// turret line). Top mirrors bot across the diagonal.
    /// </summary>
    private static readonly Dictionary<string, (double X, double Y)[]> Paths = new()
    {
        ["top"] =
        [
            (1800, 2200), (1253, 4281), (1483, 6919), (1250, 10504), (1500, 11700), (1900, 12400),
            (3600, 13100), (5200, 13600), (8226, 13327), (10572, 13624), (12612, 13052),
        ],
        ["mid"] =
        [
            (2000, 2000), (3651, 3696), (5048, 4812), (5846, 6396), (7400, 7400), (8955, 8510),
            (9767, 10113), (11134, 11207), (12900, 12900),
        ],
        ["bot"] =
        [
            (2200, 1800), (4281, 1253), (6919, 1483), (10504, 1250), (11700, 1500), (12400, 1900),
            (13100, 3600), (13600, 5200), (13327, 8226), (13624, 10572), (13052, 12612),
        ],
    };

    private const double FountainX = 450, FountainY = 450, FountainRadius = 1000;

    /// <summary>The base is the square behind the inhibitors; the far corner mirrors it.</summary>
    private const double BaseEdge = 3800, EnemyBaseEdge = 11100;

    /// <summary>
    /// How far from a lane's centre line still counts as standing in it. The
    /// lanes are about 900 units wide and a laner ranges a little beyond
    /// their edges, so this is generous; the recorded laning cloud sits
    /// within 600 of the line.
    /// </summary>
    public const double LaneHalfWidth = 800;

    /// <summary>Where a point is, named the way a coach says it: "the fountain", "bot lane", "the jungle or river".</summary>
    public static string Place(double x, double y)
    {
        if (double.Hypot(x - FountainX, y - FountainY) <= FountainRadius)
            return "the fountain";
        if (x < BaseEdge && y < BaseEdge)
            return "their own base";
        if (x > EnemyBaseEdge && y > EnemyBaseEdge)
            return "the enemy base";
        var nearest = Lanes.MinBy(lane => Toward(lane, x, y).Distance)!;
        return Toward(nearest, x, y).Distance <= LaneHalfWidth ? $"{nearest} lane" : "the jungle or river";
    }

    /// <summary>The nearest point of a lane to (<paramref name="x"/>, <paramref name="y"/>), and how far it is.</summary>
    public static (double Distance, double X, double Y) Toward(string lane, double x, double y)
    {
        var path = Paths[lane];
        var best = (Distance: double.PositiveInfinity, X: 0.0, Y: 0.0);
        for (var i = 1; i < path.Length; i++)
        {
            var candidate = ToSegment(path[i - 1], path[i], x, y);
            if (candidate.Distance < best.Distance)
                best = candidate;
        }
        return best;
    }

    private static (double Distance, double X, double Y) ToSegment((double X, double Y) a, (double X, double Y) b, double x, double y)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var length2 = dx * dx + dy * dy;
        var t = length2 == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length2, 0, 1);
        var (px, py) = (a.X + t * dx, a.Y + t * dy);
        return (double.Hypot(x - px, y - py), px, py);
    }
}
