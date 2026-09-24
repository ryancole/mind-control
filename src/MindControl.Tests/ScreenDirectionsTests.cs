using MindControl.Policy;

namespace MindControl.Tests;

[TestClass]
public sealed class ScreenDirectionsTests
{
    [TestMethod]
    public void Every_direction_has_its_name()
    {
        Assert.AreEqual("right", ScreenDirections.Name(1, 0));
        Assert.AreEqual("down-right", ScreenDirections.Name(1, 1));
        Assert.AreEqual("down", ScreenDirections.Name(0, 1));
        Assert.AreEqual("down-left", ScreenDirections.Name(-1, 1));
        Assert.AreEqual("left", ScreenDirections.Name(-1, 0));
        Assert.AreEqual("up-left", ScreenDirections.Name(-1, -1));
        Assert.AreEqual("up", ScreenDirections.Name(0, -1));
        Assert.AreEqual("up-right", ScreenDirections.Name(1, -1));

        // A source is where the thing came from, the opposite of its travel.
        Assert.AreEqual("the left", ScreenDirections.Source(1, 0));
        Assert.AreEqual("above", ScreenDirections.Source(0, 1));
        Assert.AreEqual("the lower right", ScreenDirections.Source(-1, -1));
        Assert.AreEqual("below", ScreenDirections.Source(0, -1));
    }

    [TestMethod]
    public void A_world_offset_is_named_with_north_up_the_screen()
    {
        Assert.AreEqual("up", ScreenDirections.NameOfWorldOffset(0, 1000));
        Assert.AreEqual("down-left", ScreenDirections.NameOfWorldOffset(-700, -700));
    }

    [TestMethod]
    public void Across_gives_both_unit_perpendiculars()
    {
        var (a, b) = ScreenDirections.Across(0, 3);
        Assert.AreEqual((-1.0, 0.0), a);
        Assert.AreEqual((1.0, 0.0), b);
        Assert.AreEqual(1.0, double.Hypot(a.Dx, a.Dy), 1e-9);
    }

    [TestMethod]
    public void Toward_base_is_down_left_on_the_screen()
    {
        Assert.AreEqual(1.0, ScreenDirections.TowardBase(-1, 1), 1e-9);
        Assert.AreEqual(-1.0, ScreenDirections.TowardBase(1, -1), 1e-9);
        Assert.AreEqual(0.0, ScreenDirections.TowardBase(1, 1), 1e-9);
    }
}
