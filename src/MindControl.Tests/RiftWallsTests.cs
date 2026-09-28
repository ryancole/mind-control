using MindControl.Policy;

namespace MindControl.Tests;

/// <summary>
/// The ground no one can walk on is geometry off the map's navigation grid:
/// walls and structures, and where along a line a click clears them. Nothing
/// here is a coaching decision.
/// </summary>
[TestClass]
public sealed class RiftWallsTests
{
    /// <summary>Where the 21:28 walk-to-lane lines of a live run had the player stand: just behind the blue nexus.</summary>
    private const double StoodX = 1368, StoodY = 1317;

    private static (double Ux, double Uy) Toward(double x, double y)
    {
        var (dx, dy) = (x - StoodX, y - StoodY);
        var length = double.Hypot(dx, dy);
        return (dx / length, dy / length);
    }

    [TestMethod]
    public void Lanes_fountains_and_the_jungle_paths_can_be_walked()
    {
        Assert.IsTrue(RiftWalls.Walkable(450, 450), "blue's fountain");
        Assert.IsTrue(RiftWalls.Walkable(14350, 14350), "red's fountain");
        Assert.IsTrue(RiftWalls.Walkable(StoodX, StoodY));
        Assert.IsTrue(RiftWalls.Walkable(7400, 7400), "the middle of mid lane");
        Assert.IsTrue(RiftWalls.Walkable(12455, 1321), "bot lane's brush is walked through");
    }

    [TestMethod]
    public void Walls_structures_and_off_the_map_cannot()
    {
        Assert.IsFalse(RiftWalls.Walkable(1551, 1660), "blue's nexus");
        Assert.IsFalse(RiftWalls.Walkable(13171, 13220), "red's nexus");
        Assert.IsFalse(RiftWalls.Walkable(0, 7000), "the map's edge");
        Assert.IsFalse(RiftWalls.Walkable(-100, 7000));
        Assert.IsFalse(RiftWalls.Walkable(7000, 20000));
    }

    [TestMethod]
    public void Every_turret_stands_on_ground_that_cannot_be_walked_fallen_or_not()
    {
        foreach (var turret in RiftMap.Turrets)
            Assert.IsFalse(RiftWalls.Walkable(turret.X, turret.Y), turret.Id);
    }

    [TestMethod]
    public void A_click_on_the_nexus_moves_past_it()
    {
        // From behind the nexus, a walk's 400 units toward mid or top land on
        // it; the click moves out the far side, where the game paths round it.
        var (ux, uy) = Toward(5846, 6396);
        Assert.AreEqual(750, RiftWalls.ClearOfWalls(StoodX, StoodY, ux, uy, 400, 800));
        (ux, uy) = Toward(981, 10441);
        Assert.AreEqual(625, RiftWalls.ClearOfWalls(StoodX, StoodY, ux, uy, 400, 800));
    }

    [TestMethod]
    public void A_click_on_open_ground_stays_where_it_was()
    {
        var (ux, uy) = Toward(6919, 1483);
        Assert.AreEqual(400, RiftWalls.ClearOfWalls(StoodX, StoodY, ux, uy, 400, 800), "toward bot, clear of the nexus");
    }

    [TestMethod]
    public void A_click_with_no_walkable_ground_in_reach_is_left_alone()
    {
        // Only 500 units allowed, and the nexus runs from right in front of
        // the player to past that: nothing to move the click to.
        var (ux, uy) = Toward(5846, 6396);
        Assert.AreEqual(400, RiftWalls.ClearOfWalls(StoodX, StoodY, ux, uy, 400, 500));
    }

    [TestMethod]
    public void A_click_on_a_walks_end_looks_nearer_first()
    {
        // The last leg to the mid inhibitor turret is on the turret itself:
        // it lands short of it, not past it toward the enemy.
        var turret = RiftMap.Blue.OurTurret("mid", "inhibitor");
        var (fx, fy) = (turret.X - 600, turret.Y - 600);
        var length = double.Hypot(600, 600);
        var (ux, uy) = (600 / length, 600 / length);
        var nearer = RiftWalls.ClearOfWalls(fx, fy, ux, uy, length, 800, nearerFirst: true);
        var farther = RiftWalls.ClearOfWalls(fx, fy, ux, uy, length, 1600);
        Assert.IsLessThan(length, nearer);
        Assert.IsGreaterThan(length, farther);
        Assert.IsTrue(RiftWalls.Walkable(fx + ux * nearer, fy + uy * nearer));
    }
}
