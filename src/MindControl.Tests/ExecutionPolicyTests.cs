using System.Text.Json;
using MindControl.Feed;
using MindControl.Policy;

namespace MindControl.Tests;

/// <summary>
/// What execution coaching says and, mostly, what it does not. What is pinned
/// here is the decision per class of event -- a wide shot and a bolt that
/// found the player still are said, a hit, a cast with no bolt, a bolt with no
/// target, a dodge and a button press are not -- plus the running counts, the
/// double-credited fall, that nothing here ever moves the cursor, and that a
/// feed without the coaching stages says so once instead of going silently
/// dead. The wording is pinned only where a word carries a caveat: a wide
/// shot is where the bolt passed, never "you missed".
/// </summary>
[TestClass]
public sealed class ExecutionPolicyTests
{
    private static readonly Meta Coaching = new()
    {
        Schema = 1, HasAbilities = true, HasThreats = true, HasSkillshots = true,
    };

    private static ExecutionPolicy Policy(Meta? meta = null, ExecutionOptions? options = null)
    {
        var policy = new ExecutionPolicy(options);
        policy.Configure(meta ?? Coaching);
        policy.DrainCues();       // discard any configure-time notice
        return policy;
    }

    private static GameEvent Event(string json) =>
        JsonSerializer.Deserialize<GameEvent>(json, FeedJson.Options)!;

    private static IReadOnlyList<CoachCue> CuesFor(ExecutionPolicy policy, string json)
    {
        Assert.IsNull(policy.OnEvent(Event(json)), "execution coaching never moves the cursor");
        return policy.DrainCues();
    }

    private static string OneCue(string json)
    {
        var cues = CuesFor(Policy(), json);
        Assert.HasCount(1, cues);
        return cues[0].Reason;
    }

    private static void Silent(string json) => Assert.IsEmpty(CuesFor(Policy(), json));

    // The fixture's events, verbatim where a real one exists.

    private const string WideQ = """
        {"kind":"skillshot","seq":9,"video_time":278.6,"team":"blue",
         "champion":"Ezreal","slot":"Q","at":277.4,"launched":277.5,
         "speed":870,"heading":[-0.499,-0.867],"miss":274.3,"flight":0.594,
         "outcome":"missed","lead":-274.3}
        """;

    private const string NearQ = """
        {"kind":"skillshot","seq":9,"video_time":215.8,"team":"blue",
         "champion":"Ezreal","slot":"Q","at":214.1,"launched":214.433,
         "speed":852,"heading":[1.0,-0.027],"miss":6.1,"flight":0.823,
         "outcome":"hit","fall":0.051,"lead":6.1}
        """;

    private const string HitWhileStill = """
        {"kind":"threat","seq":9,"video_time":271.4,"team":"blue",
         "champion":"Ezreal","at":270.433,"arrival":270.787,"closest":9.6,
         "speed":854,"heading":[-0.136,0.991],"outcome":"hit","damage":72,
         "moved_across":15.3,"origin":125.0}
        """;

    private const string HitWhileMoving = """
        {"kind":"threat","seq":9,"video_time":270.6,"team":"blue",
         "champion":"Ezreal","at":269.3,"arrival":269.553,"closest":3.7,
         "speed":1487,"heading":[-0.252,0.968],"outcome":"hit","damage":85,
         "moved_across":40.5,"origin":55.0}
        """;

    [TestMethod]
    public void A_shot_that_went_wide_says_how_far_and_which_side()
    {
        var reason = OneCue(WideQ);
        StringAssert.Contains(reason, "Q passed 274px behind them");
    }

    [TestMethod]
    public void A_wide_shot_is_where_the_bolt_passed_never_a_miss()
    {
        // The verdict upstream is geometric, against a hit radius that is the
        // least settled number in that project. The copy says what was
        // measured and leaves the word "miss" out of it.
        var reason = OneCue(WideQ);
        Assert.DoesNotContain("miss", reason.ToLowerInvariant());
    }

    [TestMethod]
    public void The_side_is_the_sign_of_lead_and_absent_when_lead_is()
    {
        StringAssert.Contains(OneCue("""
            {"kind":"skillshot","video_time":345.0,"slot":"W","at":343.9,"launched":344.033,
             "miss":331.3,"outcome":"missed","lead":331.3}
            """), "ahead of them");
        StringAssert.Contains(OneCue("""
            {"kind":"skillshot","video_time":345.0,"slot":"W","at":343.9,"launched":344.033,
             "miss":331.3,"outcome":"missed"}
            """), "wide of them");
    }

    [TestMethod]
    public void A_shot_that_landed_is_silence()
    {
        Silent(NearQ);
    }

    [TestMethod]
    public void A_cast_that_launched_nothing_is_silence()
    {
        // A blink, a self-buff, or a bolt the vision layer lost: the fixture's
        // Ezreal, whose Q always launches, had nine such Qs in 270 seconds.
        // Either way it is not a fact about the player.
        Silent("""
            {"kind":"skillshot","video_time":251.1,"slot":"Q","at":250.033,"outcome":"unknown"}
            """);
    }

    [TestMethod]
    public void A_bolt_with_no_enemy_in_front_of_it_is_silence()
    {
        // Two thirds of a lane phase. A bolt at minions is farming.
        Silent("""
            {"kind":"skillshot","video_time":260.2,"slot":"Q","at":259.2,"launched":259.2,
             "speed":1008,"heading":[0.991,0.133],"outcome":"unknown"}
            """);
    }

    [TestMethod]
    public void A_button_press_is_silence()
    {
        // The skillshot event a second later says everything this one does.
        Silent("""
            {"kind":"ability","video_time":214.2,"slot":"Q","at":214.1,"countdown":5,"confirmed":true}
            """);
        Silent("""
            {"kind":"ability","video_time":353.4,"slot":"F","at":353.333,"countdown":180,"confirmed":true}
            """);
    }

    [TestMethod]
    public void The_wide_count_runs_over_scored_shots_of_that_slot_only()
    {
        var policy = Policy();
        CuesFor(policy, NearQ);
        CuesFor(policy, NearQ);
        // A W in between does not enter the Q count.
        CuesFor(policy, """
            {"kind":"skillshot","video_time":345.0,"slot":"W","at":343.9,"launched":344.033,
             "miss":331.3,"outcome":"missed","lead":331.3}
            """);
        // Nor does an unscored Q.
        CuesFor(policy, """
            {"kind":"skillshot","video_time":260.2,"slot":"Q","at":259.2,"launched":259.2,"outcome":"unknown"}
            """);
        var cues = CuesFor(policy, WideQ);
        Assert.HasCount(1, cues);
        StringAssert.Contains(cues[0].Reason, "1 of your last 3 Qs at a target went wide");
    }

    [TestMethod]
    public void The_first_scored_shot_carries_no_count()
    {
        Assert.DoesNotContain("of your last", OneCue(WideQ));
    }

    [TestMethod]
    public void The_window_forgets_the_oldest_shot()
    {
        var policy = Policy(options: new ExecutionOptions { WindowSize = 3 });
        CuesFor(policy, WideQ);
        CuesFor(policy, NearQ);
        CuesFor(policy, NearQ);
        var cues = CuesFor(policy, WideQ);
        StringAssert.Contains(cues[0].Reason, "1 of your last 3 Qs", "the first wide shot has rolled off");
    }

    [TestMethod]
    public void A_bolt_that_landed_while_standing_still_is_said_with_the_damage()
    {
        var reason = OneCue(HitWhileStill);
        StringAssert.Contains(reason, "hit you for 72");
        StringAssert.Contains(reason, "standing still");
    }

    [TestMethod]
    public void The_response_is_never_called_late()
    {
        // ~0.3s of warning is the edge of reaction; the honest number is
        // whether they were already moving across the line.
        var reason = OneCue(HitWhileStill);
        Assert.DoesNotContain("late", reason);
        Assert.DoesNotContain("react", reason);
    }

    [TestMethod]
    public void A_bolt_that_landed_while_moving_is_silence()
    {
        Silent(HitWhileMoving);
    }

    [TestMethod]
    public void A_dodge_and_an_unread_outcome_are_silence()
    {
        Silent("""
            {"kind":"threat","video_time":220.7,"at":219.7,"arrival":220.134,"closest":102.0,
             "speed":814,"outcome":"dodged","moved_across":53.2}
            """);
        Silent("""
            {"kind":"threat","video_time":233.9,"at":232.8,"arrival":233.366,"closest":22.4,
             "speed":853,"outcome":"unknown","moved_across":0.0}
            """);
    }

    [TestMethod]
    public void A_hit_with_no_motion_measured_is_silence_and_not_counted()
    {
        var policy = Policy();
        Assert.IsEmpty(CuesFor(policy, """
            {"kind":"threat","video_time":100,"at":99.6,"arrival":100.0,"closest":9,
             "speed":900,"outcome":"hit","damage":50}
            """));
        Assert.DoesNotContain("of the last", CuesFor(policy, HitWhileStill)[0].Reason);
    }

    [TestMethod]
    public void The_still_count_runs_over_bolts_that_landed()
    {
        var policy = Policy();
        CuesFor(policy, HitWhileMoving);
        var cues = CuesFor(policy, HitWhileStill);
        Assert.HasCount(1, cues);
        StringAssert.Contains(cues[0].Reason, "1 of the last 2 that landed found you still");
    }

    [TestMethod]
    public void One_fall_credited_to_two_bolts_is_said_once()
    {
        // The fixture at 444s: two bolts 0.22s apart, both "hit" for 43.
        var policy = Policy();
        var first = CuesFor(policy, """
            {"kind":"threat","video_time":444.6,"at":443.9,"arrival":444.083,"closest":32.9,
             "speed":1176,"outcome":"hit","damage":43,"moved_across":16.6,"origin":95.0}
            """);
        var second = CuesFor(policy, """
            {"kind":"threat","video_time":444.9,"at":444.1,"arrival":444.306,"closest":108.8,
             "speed":1261,"outcome":"hit","damage":43,"moved_across":1.1,"origin":152.0}
            """);
        Assert.HasCount(1, first);
        Assert.IsEmpty(second);
        // And the second did not enter the count either.
        var third = CuesFor(policy, HitWhileStill);
        StringAssert.Contains(third[0].Reason, "2 of the last 2");
    }

    [TestMethod]
    public void A_bolt_that_found_them_still_outranks_a_shot_that_went_wide()
    {
        var policy = Policy();
        policy.OnEvent(Event(WideQ));
        policy.OnEvent(Event(HitWhileStill));
        var priorities = policy.DrainCues().Select(c => c.Priority).ToArray();
        CollectionAssert.AreEqual(new[] { 2, 3 }, priorities);
    }

    [TestMethod]
    public void Kinds_it_does_not_own_are_left_alone()
    {
        Silent("""{"kind":"death","video_time":5,"champion":"Ahri"}""");
    }

    [TestMethod]
    public void A_feed_without_the_coaching_stages_says_so_once()
    {
        var policy = new ExecutionPolicy();
        policy.Configure(new Meta { Schema = 1, HasAbilities = true });
        var cues = policy.DrainCues();
        Assert.HasCount(1, cues, "abilities alone give this policy nothing to say");
        StringAssert.Contains(cues[0].Reason, "--coach");
        Assert.IsEmpty(policy.DrainCues(), "the notice is drained, not repeated");
    }

    [TestMethod]
    public void A_feed_with_either_stage_gets_no_notice()
    {
        var policy = new ExecutionPolicy();
        policy.Configure(new Meta { Schema = 1, HasThreats = true });
        Assert.IsEmpty(policy.DrainCues());
    }

    [TestMethod]
    public void A_resync_drops_the_counts_and_any_pending_cue()
    {
        var policy = Policy();
        policy.OnEvent(Event(WideQ));
        policy.OnEvent(Event(HitWhileMoving));
        policy.Resync(null);
        Assert.IsEmpty(policy.DrainCues());
        Assert.DoesNotContain("of your last", CuesFor(policy, WideQ)[0].Reason);
        Assert.DoesNotContain("of the last", CuesFor(policy, HitWhileStill)[0].Reason);
    }
}
