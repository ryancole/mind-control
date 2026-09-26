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
        return LaneOf(x, y) is { } lane ? $"{lane} lane" : "the jungle or river";
    }

    /// <summary>
    /// The lane a point stands in, or null when it is in the fountain, either
    /// base, or off every lane: the lanes meet at each base, so a point there
    /// belongs to none of them.
    /// </summary>
    public static string? LaneOf(double x, double y)
    {
        if (double.Hypot(x - FountainX, y - FountainY) <= FountainRadius
            || (x < BaseEdge && y < BaseEdge) || (x > EnemyBaseEdge && y > EnemyBaseEdge))
            return null;
        var nearest = Lanes.MinBy(lane => Toward(lane, x, y).Distance)!;
        return Toward(nearest, x, y).Distance <= LaneHalfWidth ? nearest : null;
    }

    /// <summary>The lane turret tiers, from the enemy's side of a lane in toward the player's base.</summary>
    public static readonly string[] Tiers = ["outer", "inner", "inhibitor"];

    /// <summary>
    /// The player's own lane turrets, outer to inhibitor, as the points of
    /// each lane's path they stand beside. Whether each still stands is the
    /// feed's (<c>turrets</c>); this is only where they are.
    /// </summary>
    private static readonly Dictionary<string, (double X, double Y)[]> OurTurrets = new()
    {
        ["top"] = [(1250, 10504), (1483, 6919), (1253, 4281)],
        ["mid"] = [(5846, 6396), (5048, 4812), (3651, 3696)],
        ["bot"] = [(10504, 1250), (6919, 1483), (4281, 1253)],
    };

    /// <summary>A turret's attack range, in game units: a wave this near one is fighting it.</summary>
    public const double TurretRange = 750;

    /// <summary>One of the enemy's turrets: its lane ("base" for the two nexus turrets), tier, nexus side, and spot.</summary>
    public sealed record TurretSpot(string Lane, string Tier, string? Side, double X, double Y)
    {
        /// <summary>As a coach names it: "their bot outer turret", "their top nexus turret".</summary>
        public string Name => Tier == "nexus" ? $"their {Side} nexus turret" : $"their {Lane} {Tier} turret";
    }

    /// <summary>The enemy's eleven turrets where Riot's map data puts them, lanes outer to inhibitor, then the nexus pair.</summary>
    public static readonly TurretSpot[] TheirTurrets =
    [
        new("top", "outer", null, 4318, 13875), new("top", "inner", null, 7943, 13411), new("top", "inhibitor", null, 10481, 13650),
        new("mid", "outer", null, 8955, 8510), new("mid", "inner", null, 9767, 10113), new("mid", "inhibitor", null, 11134, 11207),
        new("bot", "outer", null, 13866, 4505), new("bot", "inner", null, 13327, 8226), new("bot", "inhibitor", null, 13624, 10572),
        new("base", "nexus", "top", 12611, 13084), new("base", "nexus", "bot", 13052, 12612),
    ];

    /// <summary>
    /// The enemy turret whose range (<see cref="TurretRange"/>) covers a
    /// point, the nearest when two do, and how far from it the point is; null
    /// when none does. <paramref name="standing"/> says whether one stands
    /// (null: not known); a turret known to have fallen covers nothing, and
    /// one not known either way is taken to stand, since walking under a
    /// turret that is there is the costly mistake.
    /// </summary>
    public static (TurretSpot Turret, double Distance)? TheirTurretCovering(
        double x, double y, Func<TurretSpot, bool?>? standing = null) =>
        TheirTurrets
            .Where(t => standing?.Invoke(t) != false)
            .Select(t => (Turret: t, Distance: double.Hypot(x - t.X, y - t.Y)))
            .Where(p => p.Distance <= TurretRange)
            .OrderBy(p => p.Distance)
            .Select(p => ((TurretSpot, double)?)p)
            .FirstOrDefault();

    /// <summary>
    /// Where an enemy wave's front is in a lane, named as a coach says it,
    /// by the player's own turrets: in the enemy's half, in the player's half
    /// short of their outer turret, or at one of their turrets, "at" being
    /// within a turret's range of its spot, along the lane.
    /// <paramref name="standing"/> says whether the player's turret of a tier
    /// in this lane stands (null: not known). A wave at the spot of a fallen
    /// turret is not fighting it: it is named past it, on the way to the next
    /// turret in that has not fallen, since that is the one it pressures.
    /// Without <paramref name="standing"/> every turret is named by its spot.
    /// </summary>
    public static string EnemyFrontPlace(string lane, double progress, Func<string, bool?>? standing = null)
    {
        var reached = -1;
        for (var tier = 0; tier < Tiers.Length; tier++)
            if (progress <= TurretReach(lane, tier))
                reached = tier;
        if (reached < 0)
            return progress < 0.5 ? "in your half of the lane, short of your outer turret" : "in their half of the lane";

        var next = reached;
        while (next < Tiers.Length && standing?.Invoke(Tiers[next]) == false)
            next++;
        if (next == reached)
            return At(Tiers[reached], standing?.Invoke(Tiers[reached]) is null && standing is not null);

        var fallen = Tiers[reached..next];
        var past = fallen.Length == 1
            ? $"past your fallen {fallen[0]} turret"
            : $"past your fallen {string.Join(", ", fallen[..^1])} and {fallen[^1]} turrets";
        if (next == Tiers.Length)
            return $"{past}, on the way to your nexus";
        return $"{past}, on the way to your {Tiers[next]} turret"
            + (standing!(Tiers[next]) is null ? " (the minimap has not shown whether it stands)" : "");

        static string At(string tier, bool unknown) =>
            (tier == "inhibitor" ? "at or past your inhibitor turret" : $"at your {tier} turret")
            + (unknown ? " (the minimap has not shown whether it stands)" : "");
    }

    /// <summary>
    /// Whether an enemy wave's front is at one of the player's own turrets, or
    /// at or past the spot of their outer turret when it has fallen.
    /// </summary>
    public static bool AtOurTurret(string lane, double progress) => progress <= TurretReach(lane, 0);

    /// <summary>How far along the lane a turret's range reaches toward the enemy.</summary>
    private static double TurretReach(string lane, int tier)
    {
        var turret = OurTurrets[lane][tier];
        return Along(lane, turret.X, turret.Y).Progress + TurretRange / Length(lane);
    }

    /// <summary>A lane's length along its centre line, nexus to nexus.</summary>
    public static double Length(string lane)
    {
        var path = Paths[lane];
        var length = 0.0;
        for (var i = 1; i < path.Length; i++)
            length += double.Hypot(path[i].X - path[i - 1].X, path[i].Y - path[i - 1].Y);
        return length;
    }

    /// <summary>
    /// How far along a lane a point is: the fraction of the lane's length
    /// from the player's nexus (0) to the enemy's (1) at the point's nearest
    /// place on it, and how far off the centre line it lies. How far a wave
    /// has pushed is this, for its front minion.
    /// </summary>
    public static (double Distance, double Progress) Along(string lane, double x, double y)
    {
        var path = Paths[lane];
        var best = (Distance: double.PositiveInfinity, Units: 0.0);
        var walked = 0.0;
        for (var i = 1; i < path.Length; i++)
        {
            var candidate = ToSegment(path[i - 1], path[i], x, y);
            if (candidate.Distance < best.Distance)
                best = (candidate.Distance, walked + double.Hypot(candidate.X - path[i - 1].X, candidate.Y - path[i - 1].Y));
            walked += double.Hypot(path[i].X - path[i - 1].X, path[i].Y - path[i - 1].Y);
        }
        return (best.Distance, best.Units / walked);
    }

    /// <summary>The point on a lane's centre line <paramref name="progress"/> of the way from the player's nexus to the enemy's.</summary>
    public static (double X, double Y) At(string lane, double progress)
    {
        var path = Paths[lane];
        var remaining = Math.Clamp(progress, 0, 1) * Length(lane);
        for (var i = 1; i < path.Length; i++)
        {
            var segment = double.Hypot(path[i].X - path[i - 1].X, path[i].Y - path[i - 1].Y);
            if (remaining <= segment)
            {
                var t = segment == 0 ? 0 : remaining / segment;
                return (path[i - 1].X + t * (path[i].X - path[i - 1].X), path[i - 1].Y + t * (path[i].Y - path[i - 1].Y));
            }
            remaining -= segment;
        }
        return path[^1];
    }

    /// <summary>
    /// Where each lane is played in the early game: the point a player walking
    /// to lane is headed for, and so where a walk's one minimap click goes,
    /// rather than the lane's nearest point (from the fountain that is the
    /// lane's mouth at the nexus, a few seconds' walk). Bot is its corner,
    /// between where the fixture's player waited for the first minions
    /// (11162, 2115) and where they laned (12688, 3462); top mirrors it
    /// across the diagonal; mid is the map's centre.
    /// </summary>
    public static (double X, double Y) LaningSpot(string lane) => lane switch
    {
        "top" => (1900, 12400),
        "mid" => (7400, 7400),
        "bot" => (12400, 1900),
        _ => throw new ArgumentException($"no lane \"{lane}\"", nameof(lane)),
    };

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
