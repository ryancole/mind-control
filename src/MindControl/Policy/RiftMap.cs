namespace MindControl.Policy;

/// <summary>
/// A corner of Summoner's Rift: blue is the lower left, where x and y are
/// smallest, and red the upper right. It is the map's, not the feed's: the
/// feed's "blue" team is always the player's own, whichever corner that is.
/// </summary>
public enum MapSide { Blue, Red }

/// <summary>
/// Summoner's Rift in game units, y growing north, blue's base in the lower
/// left and red's in the upper right, as spectral-sight's world positions
/// are: the three lanes as the lines their turrets lie on, the 22 turret
/// spots, the fountains, and the two bases. The geometry is fixed; an
/// instance (<see cref="Blue"/>, <see cref="Red"/>) is the map seen from the
/// side the player plays, which is what makes a turret "your" or "their",
/// a base home, and a lane run from the player's own nexus. Pure geometry,
/// with the turret positions Riot publishes for the map: it says where a
/// point is and how far each lane is from it, and decides nothing about
/// whether anyone should be there.
/// </summary>
public sealed class RiftMap
{
    public static readonly string[] Lanes = ["top", "mid", "bot"];

    /// <summary>
    /// Each lane's centre line from blue's nexus to red's. Mid
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

    /// <summary>
    /// The two fountains, where each side spawns and shops, and how near one
    /// a point has to be to stand in it.
    /// </summary>
    private static readonly (double X, double Y) BlueFountain = (450, 450), RedFountain = (14350, 14350);
    private const double FountainRadius = 1000;

    /// <summary>The bases are the squares behind the inhibitors, in the map's two corners.</summary>
    private const double BlueBaseEdge = 3800, RedBaseEdge = 11100;

    /// <summary>
    /// How far from a lane's centre line still counts as standing in it. The
    /// lanes are about 900 units wide and a laner ranges a little beyond
    /// their edges, so this is generous; the recorded laning cloud sits
    /// within 600 of the line.
    /// </summary>
    public const double LaneHalfWidth = 800;

    /// <summary>
    /// The river runs corner to corner across mid, where x + y is the map's
    /// width, and counts as the river this far either side of that line: its
    /// entrances and the two pits lie within it. Blue's jungle is below it and
    /// red's above.
    /// </summary>
    private const double RiverLine = 14800, RiverHalfWidth = 1100;

    /// <summary>What <see cref="Place"/> calls the player's fountain, where the shop is.</summary>
    public const string FountainPlace = "the fountain";

    /// <summary>What <see cref="Place"/> calls the rest of the player's own base.</summary>
    public const string BasePlace = "their own base";

    /// <summary>
    /// The side whose fountain a point stands in, or null when it stands in
    /// neither. Nobody stands in the enemy's fountain and lives, so a player
    /// or ally seen in one says which side they play from.
    /// </summary>
    public static MapSide? FountainOf(double x, double y) =>
        double.Hypot(x - BlueFountain.X, y - BlueFountain.Y) <= FountainRadius ? MapSide.Blue
        : double.Hypot(x - RedFountain.X, y - RedFountain.Y) <= FountainRadius ? MapSide.Red
        : null;

    /// <summary>The side whose base a point stands in, fountain included, or null.</summary>
    private static MapSide? BaseOf(double x, double y) =>
        x < BlueBaseEdge && y < BlueBaseEdge ? MapSide.Blue
        : x > RedBaseEdge && y > RedBaseEdge ? MapSide.Red
        : null;

    /// <summary>
    /// The lane a point stands in, or null when it is in either base or off
    /// every lane: the lanes meet at each base, so a point there belongs to
    /// none of them. The same from either side.
    /// </summary>
    public static string? LaneOf(double x, double y)
    {
        if (BaseOf(x, y) is not null)
            return null;
        var nearest = Lanes.MinBy(lane => Toward(lane, x, y).Distance)!;
        return Toward(nearest, x, y).Distance <= LaneHalfWidth ? nearest : null;
    }

    /// <summary>The lane turret tiers, from the enemy's side of a lane in toward the owner's base.</summary>
    public static readonly string[] Tiers = ["outer", "inner", "inhibitor"];

    /// <summary>A turret's attack range, in game units: a wave this near one is fighting it.</summary>
    public const double TurretRange = 750;

    /// <summary>
    /// One of the map's 22 turret spots, named for good by the side it
    /// belongs to (<c>blue-bot-outer</c>, <c>red-base-nexus-top</c>): its
    /// lane ("base" for the two nexus turrets), tier, which side of the nexus
    /// for a nexus turret, and where it stands. Whether it is the player's or
    /// the enemy's depends on the side they play from (<see cref="Owner"/>).
    /// </summary>
    public sealed record TurretSpot(MapSide Owner, string Lane, string Tier, string? NexusSide, double X, double Y)
    {
        public string Id => Tier == "nexus"
            ? $"{Owner.ToString().ToLowerInvariant()}-base-nexus-{NexusSide}"
            : $"{Owner.ToString().ToLowerInvariant()}-{Lane}-{Tier}";

        /// <summary>
        /// As a coach from <paramref name="side"/> names it: "your bot outer
        /// turret", "their top nexus turret".
        /// </summary>
        public string NameFrom(MapSide side) =>
            (Owner == side ? "your " : "their ")
            + (Tier == "nexus" ? $"{NexusSide} nexus turret" : $"{Lane} {Tier} turret");
    }

    /// <summary>
    /// All 22 turrets where Riot's map data puts them (the same spots
    /// spectral-sight reads them at): each side's lanes, outer to inhibitor,
    /// then its nexus pair.
    /// </summary>
    public static readonly TurretSpot[] Turrets =
    [
        new(MapSide.Blue, "top", "outer", null, 981, 10441), new(MapSide.Blue, "top", "inner", null, 1512, 6699),
        new(MapSide.Blue, "top", "inhibitor", null, 1169, 4287),
        new(MapSide.Blue, "mid", "outer", null, 5846, 6396), new(MapSide.Blue, "mid", "inner", null, 5048, 4812),
        new(MapSide.Blue, "mid", "inhibitor", null, 3651, 3696),
        new(MapSide.Blue, "bot", "outer", null, 10504, 1029), new(MapSide.Blue, "bot", "inner", null, 6919, 1483),
        new(MapSide.Blue, "bot", "inhibitor", null, 4281, 1253),
        new(MapSide.Blue, "base", "nexus", "top", 1748, 2270), new(MapSide.Blue, "base", "nexus", "bot", 2177, 1807),
        new(MapSide.Red, "top", "outer", null, 4318, 13875), new(MapSide.Red, "top", "inner", null, 7943, 13411),
        new(MapSide.Red, "top", "inhibitor", null, 10481, 13650),
        new(MapSide.Red, "mid", "outer", null, 8955, 8510), new(MapSide.Red, "mid", "inner", null, 9767, 10113),
        new(MapSide.Red, "mid", "inhibitor", null, 11134, 11207),
        new(MapSide.Red, "bot", "outer", null, 13866, 4505), new(MapSide.Red, "bot", "inner", null, 13327, 8226),
        new(MapSide.Red, "bot", "inhibitor", null, 13624, 10572),
        new(MapSide.Red, "base", "nexus", "top", 12611, 13084), new(MapSide.Red, "base", "nexus", "bot", 13052, 12612),
    ];

    /// <summary>The map as seen from the blue side, the lower-left corner.</summary>
    public static readonly RiftMap Blue = new(MapSide.Blue);

    /// <summary>The map as seen from the red side, the upper-right corner.</summary>
    public static readonly RiftMap Red = new(MapSide.Red);

    /// <summary>The map as seen from <paramref name="side"/>.</summary>
    public static RiftMap From(MapSide side) => side == MapSide.Blue ? Blue : Red;

    private RiftMap(MapSide side) => Side = side;

    /// <summary>The side the player plays from: whose fountain is home, whose turrets are theirs.</summary>
    public MapSide Side { get; }

    /// <summary>The middle of the player's own fountain, where they respawn: home.</summary>
    public (double X, double Y) Fountain => Side == MapSide.Blue ? BlueFountain : RedFountain;

    /// <summary>
    /// Where a point is, named the way a coach says it: "the fountain",
    /// "their own base", "the enemy base", "bot lane", and off the lanes "the
    /// river, top side", "your jungle, bot side" or "their jungle, top side".
    /// </summary>
    public string Place(double x, double y)
    {
        if (FountainOf(x, y) == Side)
            return FountainPlace;
        if (BaseOf(x, y) is { } side)
            return side == Side ? BasePlace : "the enemy base";
        return LaneOf(x, y) is { } lane ? $"{lane} lane" : OffLanePlace(x, y);
    }

    /// <summary>
    /// Where a point off the lanes lies, as <see cref="Place"/> names it: the
    /// river or whose jungle, with the side of mid ("the river, bot side",
    /// "their jungle, top side").
    /// </summary>
    public string OffLanePlace(double x, double y) => OffLane(x, y) switch
    {
        (var half, null) => $"the river, {half}",
        var (half, jungle) => $"{(jungle == Side ? "your" : "their")} jungle, {half}",
    };

    /// <summary>
    /// Where a point off the lanes lies, the same from either side: which
    /// side of mid lane's line it is on ("top side", "bot side") and whose
    /// jungle it is in, null in the river.
    /// </summary>
    public static (string Half, MapSide? Jungle) OffLane(double x, double y)
    {
        var half = y > x ? "top side" : "bot side";
        var offRiver = (x + y - RiverLine) / Math.Sqrt(2);
        return Math.Abs(offRiver) <= RiverHalfWidth ? (half, null)
            : (half, offRiver < 0 ? MapSide.Blue : MapSide.Red);
    }

    /// <summary>The player's own turret of a tier in a lane.</summary>
    public TurretSpot OurTurret(string lane, string tier) =>
        Turrets.First(t => t.Owner == Side && t.Lane == lane && t.Tier == tier);

    /// <summary>The enemy's eleven turrets.</summary>
    public IEnumerable<TurretSpot> TheirTurrets => Turrets.Where(t => t.Owner != Side);

    /// <summary>
    /// The enemy turret whose range (<see cref="TurretRange"/>) covers a
    /// point, the nearest when two do, and how far from it the point is; null
    /// when none does. <paramref name="standing"/> says whether one stands
    /// (null: not known); a turret known to have fallen covers nothing, and
    /// one not known either way is taken to stand, since walking under a
    /// turret that is there is the costly mistake.
    /// </summary>
    public (TurretSpot Turret, double Distance)? TheirTurretCovering(
        double x, double y, Func<TurretSpot, bool?>? standing = null) =>
        TheirTurrets
            .Where(t => standing?.Invoke(t) != false)
            .Select(t => (Turret: t, Distance: double.Hypot(x - t.X, y - t.Y)))
            .Where(p => p.Distance <= TurretRange)
            .OrderBy(p => p.Distance)
            .Select(p => ((TurretSpot, double)?)p)
            .FirstOrDefault();

    /// <summary>
    /// How much of a screen direction points toward the player's own base:
    /// +1 straight at it, -1 straight away. The camera never rotates, so
    /// home is down-left on the screen from the blue side and up-right from
    /// the red.
    /// </summary>
    public double TowardBase(double dx, double dy)
    {
        var length = double.Hypot(dx, dy);
        var toward = length == 0 ? 0 : (-dx + dy) / (length * Math.Sqrt(2));
        return Side == MapSide.Blue ? toward : -toward;
    }

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
    public string EnemyFrontPlace(string lane, double progress, Func<string, bool?>? standing = null)
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
    public bool AtOurTurret(string lane, double progress) => progress <= TurretReach(lane, 0);

    /// <summary>How far along the lane a turret's range reaches toward the enemy.</summary>
    private double TurretReach(string lane, int tier)
    {
        var turret = OurTurret(lane, Tiers[tier]);
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
    public (double Distance, double Progress) Along(string lane, double x, double y)
    {
        var (distance, progress) = AlongFromBlue(lane, x, y);
        return (distance, Side == MapSide.Blue ? progress : 1 - progress);
    }

    /// <summary>The point on a lane's centre line <paramref name="progress"/> of the way from the player's nexus to the enemy's.</summary>
    public (double X, double Y) At(string lane, double progress) =>
        AtFromBlue(lane, Side == MapSide.Blue ? progress : 1 - progress);

    private static (double Distance, double Progress) AlongFromBlue(string lane, double x, double y)
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

    private static (double X, double Y) AtFromBlue(string lane, double progress)
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
    /// How far behind the minions a walk up a lane stops, in game units: out
    /// of an enemy caster minion's reach (550) of their front, near enough.
    /// </summary>
    public const double WalkBehindUnits = 500;

    /// <summary>
    /// The farthest spot up a lane the player can walk to safely, as a
    /// fraction of the lane (<see cref="Along"/>), named as a coach says it:
    /// their farthest turret still standing, or, when their own minions have
    /// pushed beyond it, just behind those minions' front; and never within
    /// <see cref="WalkBehindUnits"/> of the enemy's front, so a walk stops
    /// short of an enemy wave rather than into it. <paramref name="standing"/>
    /// says whether the player's turret of a tier stands (null: not known),
    /// and a turret not known to have fallen is taken to stand, as it does
    /// before the minimap is read. The fronts are a wave's
    /// (<see cref="WaveFacts"/>), null when that side shows none.
    /// </summary>
    public (double Progress, string Name) WalkTo(
        string lane, Func<string, bool?>? standing, double? ourFront, double? theirFront)
    {
        var (progress, name) = (0.0, "your nexus");
        for (var tier = 0; tier < Tiers.Length; tier++)
        {
            if (standing?.Invoke(Tiers[tier]) == false)
                continue;
            var turret = OurTurret(lane, Tiers[tier]);
            (progress, name) = (Along(lane, turret.X, turret.Y).Progress, $"your {Tiers[tier]} turret");
            break;
        }
        var behind = WalkBehindUnits / Length(lane);
        if (ourFront is { } ours && ours - behind > progress)
            (progress, name) = (ours - behind, "behind your minions");
        if (theirFront is { } theirs && theirs - behind < progress)
            (progress, name) = (Math.Max(0, theirs - behind),
                ourFront >= theirs ? "behind your minions" : "short of the enemy minions");
        return (progress, name);
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
