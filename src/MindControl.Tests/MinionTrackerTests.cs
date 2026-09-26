using MindControl.Feed;
using MindControl.Policy;

namespace MindControl.Tests;

[TestClass]
public sealed class MinionTrackerTests
{
    private static Minion Bar(string team, double x, double health) =>
        new() { Team = team, WorldX = x, WorldY = 1400, Health = health };

    [TestMethod]
    public void A_bar_followed_across_frames_has_a_fall_rate_and_a_time_to_empty()
    {
        var tracker = new MinionTracker();
        Minion latest = null!;
        for (var i = 0; i <= 5; i++)
        {
            // Walking 30 units a tenth of a second and losing 0.05 of its bar.
            latest = Bar(MinionTeam.Red, 9300 - 30 * i, 0.6 - 0.05 * i);
            tracker.Update(200 + 0.1 * i, [Bar(MinionTeam.Blue, 9000 + 30 * i, 1.0), latest]);
        }
        var (falling, empty) = tracker.Trend(latest);
        Assert.AreEqual(0.5, falling!.Value, 1e-9);
        Assert.AreEqual(0.7, empty);
    }

    [TestMethod]
    public void A_bar_seen_too_briefly_has_no_rate_and_one_that_holds_is_not_emptying()
    {
        var tracker = new MinionTracker();
        tracker.Update(200, [Bar(MinionTeam.Red, 9300, 0.5)]);
        var fresh = Bar(MinionTeam.Red, 9300, 0.5);
        tracker.Update(200.2, [fresh]);
        Assert.AreEqual((null, null), tracker.Trend(fresh));

        Minion held = null!;
        for (var t = 200.3; t <= 200.8; t += 0.1)
            tracker.Update(t, [held = Bar(MinionTeam.Red, 9300, 0.5)]);
        Assert.AreEqual((0.0, null), tracker.Trend(held));
    }

    [TestMethod]
    public void A_bar_that_jumps_away_or_reads_fuller_is_another_minion()
    {
        var tracker = new MinionTracker();
        for (var i = 0; i < 5; i++)
            tracker.Update(200 + 0.1 * i, [Bar(MinionTeam.Red, 9300, 0.6 - 0.05 * i)]);
        var far = Bar(MinionTeam.Red, 9600, 0.3);          // 300 units off: a new minion
        tracker.Update(200.5, [far]);
        Assert.AreEqual((null, null), tracker.Trend(far));

        var tracker2 = new MinionTracker();
        for (var i = 0; i < 5; i++)
            tracker2.Update(200 + 0.1 * i, [Bar(MinionTeam.Red, 9300, 0.6 - 0.05 * i)]);
        var fuller = Bar(MinionTeam.Red, 9310, 0.9);       // a bar only falls
        tracker2.Update(200.5, [fuller]);
        Assert.AreEqual((null, null), tracker2.Trend(fuller));
    }

    [TestMethod]
    public void A_minion_unseen_for_a_second_is_forgotten_and_sides_never_mix()
    {
        var tracker = new MinionTracker();
        for (var i = 0; i < 5; i++)
            tracker.Update(200 + 0.1 * i, [Bar(MinionTeam.Red, 9300, 0.6 - 0.05 * i)]);
        var back = Bar(MinionTeam.Red, 9300, 0.3);
        tracker.Update(201.6, [back]);
        Assert.AreEqual((null, null), tracker.Trend(back));

        var tracker2 = new MinionTracker();
        for (var i = 0; i < 5; i++)
            tracker2.Update(200 + 0.1 * i, [Bar(MinionTeam.Red, 9300, 0.6 - 0.05 * i)]);
        var ours = Bar(MinionTeam.Blue, 9300, 0.35);
        tracker2.Update(200.5, [ours]);
        Assert.AreEqual((null, null), tracker2.Trend(ours));
    }
}
