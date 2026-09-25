using MindControl.Policy;

namespace MindControl.Tests;

/// <summary>
/// The map is geometry, checked against places the fixture recording put the
/// player: the fountain at spawn, bot lane where they laned for minutes, and
/// the diagonal for mid. Nothing here is a coaching decision.
/// </summary>
[TestClass]
public sealed class RiftMapTests
{
    [TestMethod]
    public void Places_are_named_as_a_coach_says_them()
    {
        Assert.AreEqual("the fountain", RiftMap.Place(400, 460));
        Assert.AreEqual("their own base", RiftMap.Place(2500, 1500));
        Assert.AreEqual("bot lane", RiftMap.Place(13064, 2051));   // the fixture's laning spot
        Assert.AreEqual("bot lane", RiftMap.Place(12111, 1283));
        Assert.AreEqual("top lane", RiftMap.Place(1100, 8000));
        Assert.AreEqual("mid lane", RiftMap.Place(7400, 7400));
        Assert.AreEqual("the jungle or river", RiftMap.Place(7000, 3000));
        Assert.AreEqual("the enemy base", RiftMap.Place(13500, 13500));
    }

    [TestMethod]
    public void The_way_to_a_lane_is_its_nearest_point()
    {
        var (distance, x, y) = RiftMap.Toward("bot", 400, 460);
        Assert.AreEqual((2200.0, 1800.0), (x, y), "the lane's mouth at the nexus");
        Assert.AreEqual(2244, Math.Round(distance));

        var onLane = RiftMap.Toward("bot", 12400, 1900);
        Assert.AreEqual(0, onLane.Distance);
        Assert.IsLessThan(RiftMap.LaneHalfWidth, RiftMap.Toward("bot", 12688, 3462).Distance, "where the fixture's player laned");
        Assert.IsLessThan(RiftMap.LaneHalfWidth, RiftMap.Toward("bot", 11162, 2115).Distance, "and waited for the first minions");

        var (mid, mx, my) = RiftMap.Toward("mid", 6000, 8000);
        Assert.IsGreaterThan(0, mid);
        Assert.IsGreaterThan(6000, mx, "the nearest point of the diagonal lies down-right of a point above it");
        Assert.IsLessThan(8000, my);
    }
}
