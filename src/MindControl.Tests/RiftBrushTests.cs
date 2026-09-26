using MindControl.Policy;

namespace MindControl.Tests;

/// <summary>
/// The brush is geometry off the map's navigation grid: how many patches,
/// where each lies, and whether a point stands in one. Nothing here is a
/// coaching decision.
/// </summary>
[TestClass]
public sealed class RiftBrushTests
{
    private static RiftBrush.Patch Nearest(double x, double y) => RiftBrush.All.MinBy(p => double.Hypot(p.X - x, p.Y - y))!;

    [TestMethod]
    public void The_rift_has_its_39_patches()
    {
        Assert.HasCount(39, RiftBrush.All);
        foreach (var patch in RiftBrush.All)
            Assert.IsGreaterThanOrEqualTo(20, patch.Cells.Length, "the smallest patch is twenty cells");
    }

    [TestMethod]
    public void Patches_are_named_by_where_they_lie()
    {
        Assert.AreEqual("the bot lane brush", Nearest(12455, 1321).Name, "bot lane's corner");
        Assert.AreEqual("bot", Nearest(12455, 1321).Lane);
        Assert.AreEqual("the top lane brush", Nearest(1127, 12405).Name, "top lane's corner");
        Assert.AreEqual("a river brush, top side", Nearest(6267, 8351).Name, "the long one beside mid");
        Assert.AreEqual("a river brush, bot side", Nearest(8674, 6404).Name);
        Assert.AreEqual("a brush in their jungle, top side", Nearest(6238, 10301).Name);
        Assert.AreEqual("a brush in your jungle, bot side", Nearest(6544, 4660).Name);
        Assert.IsNull(Nearest(6544, 4660).Lane);
    }

    [TestMethod]
    public void A_point_stands_in_a_patch_on_its_cells_and_a_little_past_its_edge()
    {
        var corner = Nearest(12455, 1321);
        var (x, y) = corner.Inside(12455, 1321);
        Assert.AreSame(corner, RiftBrush.At(x, y));
        var edge = corner.Cells.MaxBy(c => c.X);
        Assert.AreSame(corner, RiftBrush.At(edge.X + 50, edge.Y), "a cell past the edge is the minimap's jitter");
        Assert.IsNull(RiftBrush.At(edge.X + 200, edge.Y));
        Assert.IsNull(RiftBrush.At(7400, 7400), "the middle of mid lane is open");
        Assert.IsNull(RiftBrush.At(400, 460), "nor is the fountain");
    }

    [TestMethod]
    public void A_walk_in_goes_well_inside_the_grass()
    {
        foreach (var patch in RiftBrush.All)
        {
            var (x, y) = patch.Inside(7400, 7400);
            Assert.AreSame(patch, RiftBrush.At(x, y));
            var cells = patch.Cells.ToHashSet();
            var inner = new[] { (50.0, 0.0), (-50.0, 0.0), (0.0, 50.0), (0.0, -50.0) }.All(d => cells.Contains((x + d.Item1, y + d.Item2)));
            var anyInner = patch.Cells.Any(c => new[] { (50.0, 0.0), (-50.0, 0.0), (0.0, 50.0), (0.0, -50.0) }
                .All(d => cells.Contains((c.X + d.Item1, c.Y + d.Item2))));
            Assert.AreEqual(anyInner, inner, $"{patch.Name} at ({patch.X:0}, {patch.Y:0})");
        }
    }

    [TestMethod]
    public void Near_patches_come_nearest_first_by_their_nearest_cell()
    {
        var near = RiftBrush.Near(12400, 2400, 1200).ToArray();
        Assert.IsGreaterThanOrEqualTo(2, near.Length);
        CollectionAssert.AreEqual(near.OrderBy(n => n.Distance).ToArray(), near);
        foreach (var (patch, distance) in near)
        {
            Assert.IsLessThanOrEqualTo(1200, distance);
            Assert.AreEqual(distance, patch.Nearest(12400, 2400).Distance);
        }
        Assert.IsEmpty(RiftBrush.Near(400, 460, 1200), "no brush near the fountain");
    }
}
