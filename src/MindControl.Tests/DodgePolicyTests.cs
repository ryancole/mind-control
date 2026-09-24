using System.Text.Json;
using MindControl.Feed;
using MindControl.Policy;

namespace MindControl.Tests;

/// <summary>
/// When the coach steps and, mostly, when it does not. What is pinned is the
/// decision: a bolt that hit while the player stood still is a step; a dodge,
/// an unknown outcome, a hit while moving, a hit with no motion measured and
/// a bolt with no heading are not, and two bolts credited with one fall are
/// one step. Plus which way -- across the bolt's line, toward the player's
/// own base, with the tie on the diagonal settled by rule -- the eight
/// names, the stamp at first sighting, the reset on resync, that nothing
/// here moves the cursor, and that a feed without the threat stage says so
/// once. The wording is pinned where a word carries a caveat: it is "a
/// bolt", never an ability, and the warning is stated rather than judged.
/// </summary>
[TestClass]
public sealed class DodgePolicyTests
{
    private static readonly Meta Coaching = new() { Schema = 1, HasThreats = true };

    private static DodgePolicy Policy(Meta? meta = null, DodgeOptions? options = null)
    {
        var policy = new DodgePolicy(options);
        policy.Configure(meta ?? Coaching);
        policy.DrainCues();       // discard any configure-time notice
        return policy;
    }

    private static GameEvent Event(string json) =>
        JsonSerializer.Deserialize<GameEvent>(json, FeedJson.Options)!;

    private static IReadOnlyList<MoveStep> StepsFor(DodgePolicy policy, string json)
    {
        Assert.IsNull(policy.OnEvent(Event(json)), "movement coaching never moves the cursor");
        return policy.DrainMoves();
    }

    private static MoveStep OneStep(DodgePolicy policy, string json)
    {
        var steps = StepsFor(policy, json);
        Assert.HasCount(1, steps);
        return steps[0];
    }

    private static MoveStep OneStep(string json) => OneStep(Policy(), json);

    private static void Silent(DodgePolicy policy, string json) => Assert.IsEmpty(StepsFor(policy, json));

    private static void Silent(string json) => Silent(Policy(), json);

    // The fixture's threats (data/coach-full-20260902-222718.jsonl), verbatim.

    /// <summary>From the upper right, 12 damage, the player 0.1px across its line.</summary>
    private const string HitWhileStill = """
        {"kind":"threat","seq":9,"video_time":219.0,"team":"blue",
         "champion":"Ezreal","at":218.1,"arrival":218.431,"closest":53.5,
         "speed":897,"heading":[-0.425,0.905],"outcome":"hit","damage":12,
         "moved_across":0.1,"origin":37.0}
        """;

    private const string HitWhileMoving = """
        {"kind":"threat","seq":9,"video_time":270.6,"team":"blue",
         "champion":"Ezreal","at":269.3,"arrival":269.553,"closest":3.7,
         "speed":1487,"heading":[-0.252,0.968],"outcome":"hit","damage":85,
         "moved_across":40.5,"origin":55.0}
        """;

    /// <summary>Straight from the right (heading almost exactly -x).</summary>
    private const string HitFromTheRight = """
        {"kind":"threat","video_time":1075.3,"at":1074.533,"arrival":1074.783,"closest":25.6,
         "speed":1185,"heading":[-1.0,0.05],"outcome":"hit","damage":27,"moved_across":15.0}
        """;

    /// <summary>From the upper left, travelling down-right.</summary>
    private const string HitFromTheUpperLeft = """
        {"kind":"threat","video_time":1089.0,"at":1088.333,"arrival":1088.433,"closest":56.3,
         "speed":2726,"heading":[0.85,0.53],"outcome":"hit","damage":200,"moved_across":0.0}
        """;

    [TestMethod]
    public void A_hit_while_standing_still_is_a_step_across_the_bolt()
    {
        var step = OneStep(HitWhileStill);

        // The bolt travels down-left; its perpendiculars are up-left and
        // down-right, and up-left is the one with the larger component
        // toward the player's base (down-left on the screen).
        Assert.AreEqual("up-left", step.Direction);
        Assert.AreEqual(-0.905, step.Dx, 0.001);
        Assert.AreEqual(-0.425, step.Dy, 0.001);
        Assert.AreEqual(3, step.Priority);
        Assert.AreEqual(
            "coach would have stepped up-left here: a bolt from the upper right hit you for 12 "
            + "while you stood still, 0.33s after it came into view",
            step.Sentence);
    }

    [TestMethod]
    public void The_step_is_stamped_at_the_bolts_first_sighting_not_the_event()
    {
        // The event arrives after the bolt has landed and the health read;
        // the moment the coach would have stepped is the earliest the bolt
        // was on the screen.
        Assert.AreEqual(218.1, OneStep(HitWhileStill).VideoTime, 1e-9);
    }

    [TestMethod]
    public void The_step_is_a_unit_vector()
    {
        var step = OneStep(HitWhileStill);
        Assert.AreEqual(1.0, double.Hypot(step.Dx, step.Dy), 1e-9);
    }

    [TestMethod]
    public void A_bolt_from_the_right_is_stepped_down_from()
    {
        // Perpendiculars up and down; down is toward the base.
        var step = OneStep(HitFromTheRight);
        Assert.AreEqual("down", step.Direction);
        StringAssert.StartsWith(step.Reason, "a bolt from the right hit you for 27");
    }

    [TestMethod]
    public void A_bolt_from_the_upper_left_is_stepped_down_left_from()
    {
        var step = OneStep(HitFromTheUpperLeft);
        Assert.AreEqual("down-left", step.Direction);
        StringAssert.StartsWith(step.Reason, "a bolt from the upper left hit you for 200");
    }

    [TestMethod]
    public void A_bolt_from_above_is_stepped_left_from()
    {
        var step = OneStep("""
            {"kind":"threat","video_time":10,"at":9.5,"arrival":9.8,"closest":5,
             "speed":900,"heading":[0.0,1.0],"outcome":"hit","damage":50,"moved_across":0.0}
            """);
        Assert.AreEqual("left", step.Direction);
        Assert.AreEqual(-1.0, step.Dx, 1e-9);
        Assert.AreEqual(0.0, step.Dy, 1e-9);
        StringAssert.StartsWith(step.Reason, "a bolt from above");
    }

    [TestMethod]
    public void A_bolt_along_the_diagonal_toward_base_is_a_tie_settled_downward()
    {
        // Travelling exactly down-left: both sides are equally toward the
        // base, and the rule is that the one pointing down the screen wins.
        var step = OneStep("""
            {"kind":"threat","video_time":10,"at":9.5,"arrival":9.8,"closest":5,
             "speed":900,"heading":[-0.70710678,0.70710678],"outcome":"hit","damage":50,"moved_across":0.0}
            """);
        Assert.AreEqual("down-right", step.Direction);
    }

    [TestMethod]
    public void A_heading_that_is_not_a_unit_vector_is_normalized()
    {
        var step = OneStep("""
            {"kind":"threat","video_time":10,"at":9.5,"arrival":9.8,"closest":5,
             "speed":900,"heading":[0.0,3.0],"outcome":"hit","damage":50,"moved_across":0.0}
            """);
        Assert.AreEqual("left", step.Direction);
        Assert.AreEqual(1.0, double.Hypot(step.Dx, step.Dy), 1e-9);
    }

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
    public void A_hit_while_moving_is_silence()
    {
        // Being in motion is all a player can bring to 0.3s of warning.
        Silent(HitWhileMoving);
    }

    [TestMethod]
    public void A_dodge_and_an_unread_outcome_are_silence()
    {
        Silent("""
            {"kind":"threat","video_time":220.7,"at":219.7,"arrival":220.134,"closest":102.0,
             "speed":814,"heading":[-0.38,0.92],"outcome":"dodged","moved_across":53.2}
            """);
        Silent("""
            {"kind":"threat","video_time":233.9,"at":232.8,"arrival":233.366,"closest":22.4,
             "speed":853,"heading":[0.0,1.0],"outcome":"unknown","moved_across":0.0}
            """);
    }

    [TestMethod]
    public void A_hit_with_no_motion_measured_is_silence()
    {
        // Nothing known about whether they were already stepping.
        Silent("""
            {"kind":"threat","video_time":100,"at":99.6,"arrival":100.0,"closest":9,
             "speed":900,"heading":[0.0,1.0],"outcome":"hit","damage":50}
            """);
    }

    [TestMethod]
    public void A_hit_with_no_heading_is_silence()
    {
        // No line to step across.
        Silent("""
            {"kind":"threat","video_time":100,"at":99.6,"arrival":100.0,"closest":9,
             "speed":900,"outcome":"hit","damage":50,"moved_across":0.0}
            """);
        Silent("""
            {"kind":"threat","video_time":100,"at":99.6,"arrival":100.0,"closest":9,
             "speed":900,"heading":[0.0,0.0],"outcome":"hit","damage":50,"moved_across":0.0}
            """);
    }

    [TestMethod]
    public void Two_bolts_credited_with_one_fall_are_one_step()
    {
        // The fixture's pair at 444s: arrivals 0.22s apart, 43 damage each.
        var policy = Policy();
        Assert.HasCount(1, StepsFor(policy, """
            {"kind":"threat","video_time":444.6,"at":443.9,"arrival":444.083,"closest":32.9,
             "speed":1176,"heading":[-0.31,0.95],"outcome":"hit","damage":43,"moved_across":16.6,"origin":95.0}
            """));
        Silent(policy, """
            {"kind":"threat","video_time":444.9,"at":444.1,"arrival":444.306,"closest":108.8,
             "speed":1261,"heading":[-0.82,0.57],"outcome":"hit","damage":43,"moved_across":1.1,"origin":152.0}
            """);
    }

    [TestMethod]
    public void Two_landings_far_enough_apart_are_two_steps()
    {
        var policy = Policy();
        Assert.HasCount(1, StepsFor(policy, HitWhileStill));
        Assert.HasCount(1, StepsFor(policy, HitFromTheRight));
    }

    [TestMethod]
    public void Resync_forgets_the_last_landing()
    {
        var policy = Policy();
        Assert.HasCount(1, StepsFor(policy, HitWhileStill));
        policy.Resync(null);
        // The same fall again would have been folded; after a resync it is
        // a fresh moment, because the memory may be from another game.
        Assert.HasCount(1, StepsFor(policy, HitWhileStill));
    }

    [TestMethod]
    public void Other_events_and_frames_are_ignored()
    {
        var policy = Policy();
        Assert.IsNull(policy.OnFrame(new FrameEnvelope { VideoTime = 1 }));
        Silent(policy, """{"kind":"skillshot","video_time":10,"slot":"Q","at":9.5,"outcome":"missed","miss":300}""");
        Silent(policy, """{"kind":"death","video_time":10,"team":"blue","champion":"Ezreal"}""");
    }

    [TestMethod]
    public void The_copy_says_bolt_and_never_names_an_ability()
    {
        var reason = OneStep(HitWhileStill).Reason;
        StringAssert.Contains(reason, "a bolt");
        Assert.DoesNotContain("skillshot", reason);
        Assert.DoesNotContain("dodge", reason);
    }

    [TestMethod]
    public void A_feed_without_the_threat_stage_says_so_once()
    {
        var policy = new DodgePolicy();
        policy.Configure(new Meta { Schema = 1, HasThreats = false });
        var cues = policy.DrainCues();
        Assert.HasCount(1, cues);
        StringAssert.Contains(cues[0].Reason, "movement coaching is off");
        Assert.IsEmpty(policy.DrainCues(), "said once");
    }

    [TestMethod]
    public void A_coaching_feed_gets_no_notice()
    {
        var policy = new DodgePolicy();
        policy.Configure(Coaching);
        Assert.IsEmpty(policy.DrainCues());
    }
}
