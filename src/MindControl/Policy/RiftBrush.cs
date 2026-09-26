namespace MindControl.Policy;

/// <summary>
/// The brush on Summoner's Rift: the 39 patches of tall grass, where a
/// champion standing inside cannot be seen by an enemy outside. Pure
/// geometry, in the same game units as <see cref="RiftMap"/> (the player's
/// base at the origin, y growing north): which patch a point stands in, which
/// are near it and how far, and where on the map each lies. It decides
/// nothing about whether anyone should be in one.
/// </summary>
/// <remarks>
/// Brush is fixed map geometry the player knows by heart, not something the
/// feed perceives, so it is written down here like the turret spots are.
/// The cells come from the map's own navigation grid: the brush flag of the
/// 2024 Summoner's Rift vision-pathing layer (295 by 296 cells, 50 units
/// each), as exported by FrankTheBoxMonster's LoL-NGRID-converter
/// (SR_2024/navgrids/Base/AIPath_SRX_3.VisionPathing.bmp). Pixels were put in
/// game units by fitting the layer's eighteen lane turrets to the coordinates
/// Riot publishes for them, to within 13 units: cell (column c, row r) has
/// its centre at x = 50c + 25, y = 14810 - 50r. Each patch is the connected
/// brush cells, with the brush-edge walls (see-through but not walkable)
/// joining pieces a wall splits and then left out, so only cells a champion
/// can stand on are listed.
/// </remarks>
public static class RiftBrush
{
    /// <summary>
    /// How near a brush cell a point has to be to stand in the brush. A cell
    /// is 50 units square and the minimap read jitters by tens of units, so a
    /// player on a brush's edge is counted in it.
    /// </summary>
    public const double InBrushUnits = 75;

    /// <summary>
    /// How far off a lane's centre line a brush still belongs to the lane.
    /// The lanes' own brush sits along their edges, up to about a thousand
    /// units off the line where the lane is widest; the nearest jungle patch
    /// is half as far again.
    /// </summary>
    private const double LaneBrushReach = 1050;

    /// <summary>
    /// How far either side of the river's middle a brush is in the river. The
    /// river runs corner to corner across mid, where x + y is the map's
    /// width; its entrance brush lies up to about this far off that line.
    /// </summary>
    private const double RiverHalfWidth = 1100;

    private const double RiverLine = 14800;

    /// <summary>One patch of brush: every cell a champion can stand on in it, and its middle.</summary>
    public sealed record Patch((double X, double Y)[] Cells, double X, double Y)
    {
        /// <summary>
        /// Where on the map the patch lies, as a coach says it: "bot lane",
        /// "the river", "your jungle", "their jungle", the last three with
        /// the side of mid ("the river, bot side").
        /// </summary>
        public string Place { get; } = PlaceOf(X, Y);

        /// <summary>As a coach names it: "the bot lane brush", "a river brush, bot side", "a brush in your jungle, top side".</summary>
        public string Name => Place.EndsWith(" lane") ? $"the {Place} brush"
            : Place.StartsWith("the river") ? $"a river brush{Place["the river".Length..]}"
            : $"a brush in {Place}";

        /// <summary>The lane the patch belongs to, or null when it is in the river or a jungle.</summary>
        public string? Lane => Place.EndsWith(" lane") ? Place[..^" lane".Length] : null;

        /// <summary>How far a point is from the patch's nearest cell, and that cell.</summary>
        public (double Distance, double X, double Y) Nearest(double x, double y)
        {
            var best = (Distance: double.PositiveInfinity, X: 0.0, Y: 0.0);
            foreach (var (cx, cy) in Cells)
            {
                var distance = double.Hypot(cx - x, cy - y);
                if (distance < best.Distance)
                    best = (distance, cx, cy);
            }
            return best;
        }

        /// <summary>
        /// Where a walk into the patch from a point goes: the nearest cell with
        /// brush on all four sides, so the click lands inside the grass and
        /// not on its edge, or the nearest cell when the patch is too thin
        /// to have one.
        /// </summary>
        public (double X, double Y) Inside(double x, double y)
        {
            var cells = _inner.Length > 0 ? _inner : Cells;
            return cells.MinBy(c => double.Hypot(c.X - x, c.Y - y));
        }

        private readonly (double X, double Y)[] _inner = Cells
            .Where(c => new[] { (50.0, 0.0), (-50.0, 0.0), (0.0, 50.0), (0.0, -50.0) }
                .All(d => Cells.Contains((c.X + d.Item1, c.Y + d.Item2))))
            .ToArray();
    }

    /// <summary>Where a patch at (<paramref name="x"/>, <paramref name="y"/>) lies, as <see cref="Patch.Place"/> says it.</summary>
    private static string PlaceOf(double x, double y)
    {
        var lane = RiftMap.Lanes.MinBy(l => RiftMap.Toward(l, x, y).Distance)!;
        if (RiftMap.Toward(lane, x, y).Distance <= LaneBrushReach)
            return $"{lane} lane";
        var side = y > x ? "top side" : "bot side";
        var offRiver = (x + y - RiverLine) / Math.Sqrt(2);
        if (Math.Abs(offRiver) <= RiverHalfWidth)
            return $"the river, {side}";
        return offRiver < 0 ? $"your jungle, {side}" : $"their jungle, {side}";
    }

    /// <summary>The patch a point stands in, or null when it stands in none.</summary>
    public static Patch? At(double x, double y) =>
        All.FirstOrDefault(p => Math.Abs(p.X - x) < 1000 && Math.Abs(p.Y - y) < 1000 && p.Nearest(x, y).Distance <= InBrushUnits);

    /// <summary>The patches within <paramref name="within"/> units of a point, by their nearest cell, nearest first.</summary>
    public static IEnumerable<(Patch Patch, double Distance)> Near(double x, double y, double within) =>
        All.Select(p => (Patch: p, p.Nearest(x, y).Distance))
            .Where(p => p.Distance <= within)
            .OrderBy(p => p.Distance);

    /// <summary>Every patch on the map, north to south.</summary>
    public static readonly Patch[] All = Runs.Select(Unpack).ToArray();

    private static Patch Unpack(int[] runs)
    {
        List<(double X, double Y)> cells = [];
        for (var i = 0; i < runs.Length; i += 3)
            for (var column = runs[i + 1]; column <= runs[i + 2]; column++)
                cells.Add((50 * column + 25, 14810 - 50 * runs[i]));
        return new Patch(cells.ToArray(), cells.Average(c => c.X), cells.Average(c => c.Y));
    }

    /// <summary>
    /// Each patch's cells as runs along a navgrid row: (row, first column,
    /// last column), repeated. Generated from the vision-pathing layer named
    /// above; the comment on each is its middle in game units.
    /// </summary>
    private static int[][] Runs =>
    [
        // (7165, 14075), 20 cells
        [14, 139, 147, 15, 139, 146, 16, 142, 144],
        // (2292, 13563), 107 cells
        [21, 50, 51, 22, 36, 52, 23, 37, 52, 24, 38, 53, 25, 39, 53, 26, 40, 52, 27, 41, 51, 28, 42, 49, 29, 43, 47, 30, 44, 46, 31, 45, 45],
        // (1675, 13010), 59 cells
        [31, 34, 34, 32, 33, 35, 33, 32, 36, 34, 31, 37, 35, 30, 38, 36, 29, 37, 37, 28, 36, 38, 29, 35, 39, 30, 34, 40, 31, 33, 41, 32, 32],
        // (5321, 12999), 56 cells
        [34, 105, 111, 35, 100, 111, 36, 100, 111, 37, 100, 111, 38, 100, 112],
        // (1155, 12390), 86 cells
        [40, 20, 20, 41, 20, 21, 42, 20, 22, 43, 20, 23, 44, 20, 24, 45, 20, 25, 46, 20, 26, 47, 20, 27, 48, 20, 27, 49, 20, 26, 50, 20, 26, 51, 20, 26, 52, 20, 25, 53, 20, 25, 54, 20, 24, 55, 21, 24],
        // (8015, 11813), 45 cells
        [58, 157, 162, 59, 155, 165, 60, 154, 165, 61, 154, 165, 62, 154, 154, 62, 163, 165],
        // (4432, 11801), 84 cells
        [55, 90, 91, 56, 89, 93, 57, 83, 93, 58, 83, 93, 59, 83, 92, 60, 83, 91, 61, 83, 91, 62, 85, 91, 63, 85, 91, 64, 87, 91, 65, 88, 91, 66, 88, 90, 67, 89, 89],
        // (9265, 11441), 32 cells
        [64, 186, 188, 65, 186, 188, 66, 186, 188, 67, 186, 188, 68, 179, 188, 69, 179, 188],
        // (6754, 11416), 49 cells
        [66, 131, 139, 67, 130, 140, 68, 129, 141, 69, 128, 133, 69, 138, 141, 70, 129, 131, 70, 139, 140, 71, 130, 130],
        // (3364, 11349), 24 cells
        [67, 66, 68, 68, 65, 69, 69, 64, 69, 70, 65, 69, 71, 65, 68, 72, 67, 67],
        // (6221, 10285), 29 cells
        [86, 122, 124, 87, 122, 124, 88, 122, 124, 89, 123, 124, 90, 123, 125, 91, 123, 125, 92, 123, 125, 93, 123, 126, 94, 124, 126, 95, 125, 126],
        // (8286, 10240), 30 cells
        [88, 166, 167, 89, 164, 167, 90, 164, 167, 91, 163, 167, 92, 163, 167, 93, 163, 167, 94, 163, 167],
        // (2315, 9772), 76 cells
        [94, 44, 46, 95, 43, 46, 96, 43, 46, 97, 43, 46, 98, 43, 46, 99, 43, 47, 100, 43, 51, 101, 43, 51, 102, 43, 51, 103, 43, 51, 104, 43, 48, 105, 43, 46, 106, 43, 46, 107, 44, 45],
        // (3001, 9063), 30 cells
        [112, 57, 59, 113, 57, 60, 114, 57, 61, 115, 57, 62, 116, 58, 63, 117, 59, 62, 118, 60, 61],
        // (4824, 8665), 60 cells
        [117, 94, 97, 118, 93, 98, 119, 95, 99, 120, 96, 99, 121, 96, 99, 122, 96, 99, 123, 96, 99, 124, 96, 99, 125, 95, 99, 126, 91, 98, 127, 91, 97, 128, 92, 96],
        // (6270, 8356), 103 cells
        [120, 131, 131, 121, 131, 133, 122, 130, 134, 123, 129, 135, 124, 128, 133, 125, 127, 132, 126, 126, 131, 127, 125, 130, 128, 124, 129, 129, 122, 128, 130, 121, 127, 131, 120, 126, 132, 119, 125, 133, 117, 123, 134, 116, 122, 135, 115, 121, 136, 116, 119, 137, 117, 119, 138, 119, 119],
        // (797, 8144), 47 cells
        [127, 14, 14, 128, 14, 16, 129, 14, 17, 130, 14, 17, 131, 14, 17, 132, 14, 17, 133, 14, 17, 134, 14, 17, 135, 14, 17, 136, 14, 17, 137, 14, 18, 138, 14, 17, 139, 14, 15],
        // (9975, 7867), 39 cells
        [135, 197, 200, 136, 197, 200, 137, 197, 200, 138, 197, 201, 139, 197, 201, 140, 197, 201, 141, 197, 202, 142, 197, 202],
        // (3393, 7785), 27 cells
        [139, 64, 70, 140, 64, 70, 141, 65, 70, 142, 65, 71],
        // (11486, 7138), 26 cells
        [152, 226, 232, 153, 226, 232, 154, 227, 232, 155, 227, 232],
        // (4800, 7110), 30 cells
        [151, 93, 95, 152, 93, 96, 153, 93, 97, 154, 93, 98, 155, 94, 98, 156, 95, 98, 157, 96, 98],
        // (14081, 6978), 25 cells
        [152, 282, 282, 153, 281, 282, 154, 280, 282, 155, 280, 282, 156, 280, 282, 157, 280, 282, 158, 280, 282, 159, 280, 282, 160, 280, 282, 161, 282, 282],
        // (8686, 6417), 102 cells
        [159, 180, 181, 160, 179, 182, 161, 178, 183, 162, 177, 182, 163, 176, 181, 164, 175, 180, 165, 174, 179, 166, 173, 178, 167, 172, 177, 168, 171, 176, 169, 169, 175, 170, 168, 174, 171, 167, 172, 172, 166, 171, 173, 165, 170, 174, 163, 169, 175, 164, 168, 176, 165, 167, 177, 167, 167],
        // (10178, 6031), 55 cells
        [171, 203, 207, 172, 202, 208, 173, 201, 208, 174, 200, 204, 175, 200, 203, 176, 200, 203, 177, 200, 203, 178, 200, 203, 179, 200, 204, 180, 201, 205, 181, 202, 205],
        // (11821, 5966), 30 cells
        [173, 234, 234, 174, 233, 235, 175, 233, 236, 176, 233, 237, 177, 234, 238, 178, 235, 239, 179, 236, 239, 180, 237, 239],
        // (12978, 5929), 31 cells
        [175, 257, 261, 176, 257, 261, 177, 257, 261, 178, 257, 261, 179, 257, 261, 180, 257, 261, 181, 261, 261],
        // (12087, 4730), 75 cells
        [195, 242, 243, 196, 242, 244, 197, 241, 244, 198, 240, 244, 199, 236, 244, 200, 236, 243, 201, 235, 243, 202, 237, 243, 203, 239, 243, 204, 240, 244, 205, 241, 245, 206, 241, 245, 207, 242, 245, 208, 242, 245],
        // (8583, 4726), 30 cells
        [198, 169, 171, 199, 169, 172, 200, 169, 172, 201, 169, 172, 202, 170, 173, 203, 171, 173, 204, 171, 173, 205, 171, 173, 206, 172, 173],
        // (6538, 4668), 41 cells
        [199, 129, 130, 200, 129, 133, 201, 128, 133, 202, 128, 133, 203, 128, 133, 204, 128, 132, 205, 128, 132, 206, 128, 132, 207, 128, 128],
        // (11550, 3896), 22 cells
        [216, 230, 231, 217, 229, 232, 218, 228, 233, 219, 228, 233, 220, 229, 232],
        // (8064, 3499), 37 cells
        [224, 156, 157, 224, 165, 166, 225, 156, 158, 225, 164, 166, 226, 156, 166, 227, 156, 165, 228, 158, 163],
        // (5556, 3455), 33 cells
        [225, 108, 116, 226, 108, 116, 227, 108, 110, 228, 108, 110, 229, 108, 110, 230, 108, 110, 231, 108, 110],
        // (6855, 3090), 39 cells
        [233, 131, 137, 233, 141, 142, 234, 131, 142, 235, 131, 142, 236, 136, 141],
        // (10378, 3045), 57 cells
        [231, 205, 207, 232, 205, 208, 233, 205, 208, 234, 205, 212, 235, 203, 212, 236, 203, 212, 237, 203, 212, 238, 203, 207, 239, 204, 206],
        // (13519, 2542), 64 cells
        [239, 270, 270, 240, 269, 271, 241, 268, 272, 242, 268, 273, 243, 267, 272, 244, 267, 272, 245, 266, 272, 246, 266, 272, 247, 267, 272, 248, 268, 272, 249, 269, 272, 250, 270, 272, 251, 271, 272, 252, 271, 272, 253, 272, 272],
        // (9217, 2151), 51 cells
        [250, 186, 187, 251, 183, 188, 252, 179, 188, 253, 179, 189, 254, 179, 189, 255, 180, 186, 256, 180, 183],
        // (12991, 1943), 56 cells
        [253, 260, 261, 254, 259, 263, 255, 258, 264, 256, 257, 264, 257, 256, 263, 258, 255, 262, 259, 255, 261, 260, 256, 260, 261, 256, 259, 262, 257, 258],
        // (12441, 1351), 68 cells
        [265, 248, 249, 266, 246, 250, 267, 245, 251, 268, 244, 252, 269, 243, 254, 270, 242, 255, 271, 243, 256, 272, 244, 247, 273, 245, 245],
        // (7807, 807), 23 cells
        [279, 153, 159, 280, 152, 159, 281, 152, 159],
    ];
}
