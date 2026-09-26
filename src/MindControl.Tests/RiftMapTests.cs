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

    [TestMethod]
    public void A_point_is_in_a_lane_only_outside_both_bases()
    {
        Assert.AreEqual("bot", RiftMap.LaneOf(13064, 2051));
        Assert.AreEqual("mid", RiftMap.LaneOf(7400, 7400));
        Assert.IsNull(RiftMap.LaneOf(2500, 1500), "the lanes meet in the base");
        Assert.IsNull(RiftMap.LaneOf(400, 460));
        Assert.IsNull(RiftMap.LaneOf(13500, 13500));
        Assert.IsNull(RiftMap.LaneOf(7000, 3000), "the jungle");
    }

    [TestMethod]
    public void How_far_along_a_lane_runs_from_our_nexus_to_theirs()
    {
        foreach (var lane in RiftMap.Lanes)
        {
            Assert.AreEqual(0, RiftMap.Along(lane, 1000, 1000).Progress, 1e-9, $"{lane}: behind our end is its start");
            Assert.AreEqual(1, RiftMap.Along(lane, 14000, 14000).Progress, 1e-9, $"{lane}: past theirs is its end");
            foreach (var progress in new[] { 0.1, 0.37, 0.5, 0.82 })
            {
                var (x, y) = RiftMap.At(lane, progress);
                var along = RiftMap.Along(lane, x, y);
                Assert.AreEqual(0, along.Distance, 1e-6);
                Assert.AreEqual(progress, along.Progress, 1e-9, $"{lane} at {progress} and back");
            }
        }
        Assert.AreEqual(0.5, RiftMap.Along("mid", 7400, 7400).Progress, 0.01, "the map's centre is the middle of mid");
        var (behind, ahead) = (RiftMap.Along("bot", 8600, 1400).Progress, RiftMap.Along("bot", 9300, 1400).Progress);
        Assert.IsLessThan(ahead, behind, "further east on bot's straight is further toward the enemy");
    }

    [TestMethod]
    public void Each_lane_is_played_at_a_spot_on_it_far_from_the_base()
    {
        foreach (var lane in RiftMap.Lanes)
        {
            var (x, y) = RiftMap.LaningSpot(lane);
            Assert.AreEqual($"{lane} lane", RiftMap.Place(x, y));
            Assert.IsGreaterThan(9000, double.Hypot(x - 450, y - 450), $"{lane}: a walk from the fountain goes the whole way");
        }
    }
}
