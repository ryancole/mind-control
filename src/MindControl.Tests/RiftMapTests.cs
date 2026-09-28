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
        Assert.AreEqual("your fountain", RiftMap.Blue.Place(400, 460));
        Assert.AreEqual("your base", RiftMap.Blue.Place(2500, 1500));
        Assert.AreEqual("bot lane", RiftMap.Blue.Place(13064, 2051));   // the fixture's laning spot
        Assert.AreEqual("bot lane", RiftMap.Blue.Place(12111, 1283));
        Assert.AreEqual("top lane", RiftMap.Blue.Place(1100, 8000));
        Assert.AreEqual("mid lane", RiftMap.Blue.Place(7400, 7400));
        Assert.AreEqual("your jungle, bot side", RiftMap.Blue.Place(7000, 3000));
        Assert.AreEqual("their jungle, top side", RiftMap.Blue.Place(7000, 11000));
        Assert.AreEqual("the river, bot side", RiftMap.Blue.Place(9866, 4414));   // the dragon pit
        Assert.AreEqual("the river, top side", RiftMap.Blue.Place(5007, 10471));  // the baron pit
        Assert.AreEqual("their base", RiftMap.Blue.Place(13500, 13500));
    }

    [TestMethod]
    public void A_player_in_a_lanes_brush_is_in_that_lane()
    {
        var far = RiftBrush.All
            .Where(p => p.Lane is not null)
            .SelectMany(p => p.Cells.Select(c => (p.Lane, c.X, c.Y)))
            .Where(c => RiftMap.LaneOf(c.X, c.Y) is null)
            .ToArray();
        Assert.IsNotEmpty(far, "some lane brush lies farther off the line than a lane's half width");
        foreach (var (lane, x, y) in far)
            Assert.IsTrue(RiftMap.Blue.Place(x, y) is var place && (place == $"{lane} lane" || place.EndsWith(" base")),
                $"({x}, {y}) in the {lane} lane brush");
    }

    [TestMethod]
    public void The_jungles_are_named_by_whose_they_are()
    {
        Assert.AreEqual("their jungle, bot side", RiftMap.Red.Place(7000, 3000));
        Assert.AreEqual("your jungle, top side", RiftMap.Red.Place(7000, 11000));
        Assert.AreEqual("the river, bot side", RiftMap.Red.Place(9866, 4414), "the river is nobody's");
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
            Assert.AreEqual(0, RiftMap.Blue.Along(lane, 1000, 1000).Progress, 1e-9, $"{lane}: behind our end is its start");
            Assert.AreEqual(1, RiftMap.Blue.Along(lane, 14000, 14000).Progress, 1e-9, $"{lane}: past theirs is its end");
            foreach (var progress in new[] { 0.1, 0.37, 0.5, 0.82 })
            {
                var (x, y) = RiftMap.Blue.At(lane, progress);
                var along = RiftMap.Blue.Along(lane, x, y);
                Assert.AreEqual(0, along.Distance, 1e-6);
                Assert.AreEqual(progress, along.Progress, 1e-9, $"{lane} at {progress} and back");
            }
        }
        Assert.AreEqual(0.5, RiftMap.Blue.Along("mid", 7400, 7400).Progress, 0.01, "the map's centre is the middle of mid");
        var (behind, ahead) = (RiftMap.Blue.Along("bot", 8600, 1400).Progress, RiftMap.Blue.Along("bot", 9300, 1400).Progress);
        Assert.IsLessThan(ahead, behind, "further east on bot's straight is further toward the enemy");
    }

    [TestMethod]
    public void A_walk_up_a_lane_goes_to_the_farthest_turret_standing()
    {
        var outer = Progress(RiftMap.Blue, "bot", "outer");
        var inner = Progress(RiftMap.Blue, "bot", "inner");
        Assert.AreEqual((outer, "your outer turret"), RiftMap.Blue.WalkTo("bot", null, null, null), "nothing read: all stand");
        Assert.AreEqual((outer, "your outer turret"), RiftMap.Blue.WalkTo("bot", _ => null, null, null), "not called is not fallen");
        Assert.AreEqual((inner, "your inner turret"), RiftMap.Blue.WalkTo("bot", t => t != "outer", null, null));
        Assert.AreEqual((0.0, "your nexus"), RiftMap.Blue.WalkTo("bot", _ => false, null, null));
    }

    [TestMethod]
    public void A_walk_up_a_lane_goes_behind_our_minions_beyond_the_turret_but_never_into_theirs()
    {
        var outer = Progress(RiftMap.Blue, "bot", "outer");
        var behind = RiftMap.WalkBehindUnits / RiftMap.Length("bot");

        Assert.AreEqual((0.8 - behind, "behind your minions"), RiftMap.Blue.WalkTo("bot", null, 0.8, null));
        Assert.AreEqual((outer, "your outer turret"), RiftMap.Blue.WalkTo("bot", null, outer - 0.05, null),
            "minions short of the turret: the turret is farther");
        Assert.AreEqual((0.7 - behind, "behind your minions"), RiftMap.Blue.WalkTo("bot", null, 0.75, 0.7),
            "the waves fighting: behind the enemy's front, not at ours inside it");
        Assert.AreEqual((outer - 0.05 - behind, "short of the enemy minions"), RiftMap.Blue.WalkTo("bot", null, null, outer - 0.05),
            "an enemy wave at the turret: stop short of it");
        Assert.AreEqual((outer, "your outer turret"), RiftMap.Blue.WalkTo("bot", null, null, 0.9), "their wave far up: no bar");
    }
    /// <summary>How far up a lane, from <paramref name="map"/>'s side, one of that side's own turrets stands.</summary>
    internal static double Progress(RiftMap map, string lane, string tier)
    {
        var turret = map.OurTurret(lane, tier);
        return map.Along(lane, turret.X, turret.Y).Progress;
    }

    [TestMethod]
    public void Every_turret_has_one_name_for_good_and_is_yours_or_theirs_by_side()
    {
        Assert.HasCount(22, RiftMap.Turrets);
        Assert.HasCount(22, RiftMap.Turrets.Select(t => t.Id).Distinct());
        var blueBotOuter = RiftMap.Turrets.Single(t => t.Id == "blue-bot-outer");
        Assert.AreSame(blueBotOuter, RiftMap.Blue.OurTurret("bot", "outer"));
        Assert.AreEqual("red-bot-outer", RiftMap.Red.OurTurret("bot", "outer").Id);
        Assert.AreEqual("your bot outer turret", blueBotOuter.NameFrom(MapSide.Blue));
        Assert.AreEqual("their bot outer turret", blueBotOuter.NameFrom(MapSide.Red));
        Assert.AreEqual("their top nexus turret",
            RiftMap.Turrets.Single(t => t.Id == "red-base-nexus-top").NameFrom(MapSide.Blue));
        CollectionAssert.AreEquivalent(RiftMap.Turrets.Where(t => t.Owner == MapSide.Blue).ToArray(), RiftMap.Red.TheirTurrets.ToArray());
    }

    [TestMethod]
    public void The_side_is_told_by_the_fountain_stood_in()
    {
        Assert.AreEqual(MapSide.Blue, RiftMap.FountainOf(400, 460));
        Assert.AreEqual(MapSide.Red, RiftMap.FountainOf(14300, 14400));
        Assert.IsNull(RiftMap.FountainOf(2500, 1500), "in the base, out of the fountain");
        Assert.IsNull(RiftMap.FountainOf(7400, 7400));
    }

    [TestMethod]
    public void From_the_red_side_home_is_the_upper_right()
    {
        Assert.AreEqual("your fountain", RiftMap.Red.Place(14300, 14400));
        Assert.AreEqual("your base", RiftMap.Red.Place(12500, 13500));
        Assert.AreEqual("their base", RiftMap.Red.Place(2500, 1500));
        Assert.AreEqual("their base", RiftMap.Blue.Place(14300, 14400), "and from blue the red fountain is theirs");
        Assert.AreEqual("bot lane", RiftMap.Red.Place(13064, 2051), "lanes are named the same from either side");
        Assert.AreEqual(RiftMap.Blue.Fountain, (RiftMap.Red.Fountain.X - 13900, RiftMap.Red.Fountain.Y - 13900));
    }

    [TestMethod]
    public void From_the_red_side_a_lane_runs_from_the_red_nexus()
    {
        foreach (var lane in RiftMap.Lanes)
            foreach (var (x, y) in new[] { (1000.0, 1000.0), (7400.0, 7400.0), (13064.0, 2051.0), (1100.0, 8000.0) })
                Assert.AreEqual(1 - RiftMap.Blue.Along(lane, x, y).Progress, RiftMap.Red.Along(lane, x, y).Progress, 1e-9);
        Assert.AreEqual(1, RiftMap.Red.Along("bot", 1000, 1000).Progress, 1e-9, "blue's end is the far end");
        var (px, py) = RiftMap.Red.At("bot", 0.3);
        Assert.AreEqual(0.3, RiftMap.Red.Along("bot", px, py).Progress, 1e-9);
        Assert.IsLessThan(0.5, Progress(RiftMap.Red, "bot", "outer"), "red's own outer turret is in red's half");
        Assert.IsLessThan(Progress(RiftMap.Red, "bot", "outer"), Progress(RiftMap.Red, "bot", "inner"));
    }

    [TestMethod]
    public void From_the_red_side_a_walk_goes_to_reds_turrets_and_enemy_fronts_are_placed_by_them()
    {
        Assert.AreEqual((Progress(RiftMap.Red, "bot", "outer"), "your outer turret"), RiftMap.Red.WalkTo("bot", null, null, null));
        Assert.AreEqual((Progress(RiftMap.Red, "bot", "inner"), "your inner turret"), RiftMap.Red.WalkTo("bot", t => t != "outer", null, null));
        var atOuter = RiftMap.Red.Along("bot", 13866, 4505).Progress;
        Assert.AreEqual("at your outer turret", RiftMap.Red.EnemyFrontPlace("bot", atOuter));
        Assert.IsTrue(RiftMap.Red.AtOurTurret("bot", atOuter));
        Assert.IsFalse(RiftMap.Red.AtOurTurret("bot", RiftMap.Red.Along("bot", 10504, 1029).Progress), "blue's outer is not red's");
        var cover = RiftMap.Red.TheirTurretCovering(10504, 1300)!.Value;
        Assert.AreEqual("blue-bot-outer", cover.Turret.Id);
        Assert.IsNull(RiftMap.Red.TheirTurretCovering(13866, 4600), "red's own turret covers nothing against red");
    }
}
