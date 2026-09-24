using System.Text.Json;
using MindControl.Feed;
using MindControl.Policy;

namespace MindControl.Tests;

/// <summary>
/// What execution coaching says and, mostly, what it does not. What is pinned
/// here is the decision per class of event -- a run of shots going wide and a
/// bolt that found the player still are said; a lone wide shot, a near shot,
/// a cast with no bolt, a bolt with no target, a dodge and a button press are
/// not -- plus the running counts and their thresholds, the double-credited
/// fall, that nothing here ever moves the cursor, and that a feed without the
/// coaching stages says so once instead of going silently dead. The wording is
/// pinned only where a word carries a caveat: a wide shot is where the bolt
/// passed, never "you missed", and an aim count is over the shots that were
/// seen, never over casts.
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

    private static string OneCue(ExecutionPolicy policy, string json)
    {
        var cues = CuesFor(policy, json);
        Assert.HasCount(1, cues);
        return cues[0].Reason;
    }

    private static string OneCue(string json) => OneCue(Policy(), json);

    private static void Silent(ExecutionPolicy policy, string json) => Assert.IsEmpty(CuesFor(policy, json));

    private static void Silent(string json) => Silent(Policy(), json);

    /// <summary>
    /// A policy one wide shot short of speaking: three near shots and a wide
    /// one, four seen with one wide, under both defaults (five seen holding
    /// two wide). The next wide shot is the run.
    /// </summary>
    private static ExecutionPolicy OnTheEdge()
    {
        var policy = Policy();
        Silent(policy, NearQ);
        Silent(policy, NearQ);
        Silent(policy, NearQ);
        Silent(policy, WideQ);
        return policy;
    }

    // The fixture's events (data/coach-full-20260902-222718.jsonl, the gated
    // build), verbatim where a real one exists.

    private const string WideQ = """
        {"kind":"skillshot","seq":9,"video_time":482.9,"team":"blue",
         "champion":"Ezreal","slot":"Q","at":481.7,"launched":482.033,
         "speed":1054,"heading":[-0.322,-0.947],"miss":314.5,"flight":0.291,
         "outcome":"missed","lead":-314.5}
        """;

    private const string SecondWideQ = """
        {"kind":"skillshot","seq":9,"video_time":797.4,"team":"blue",
         "champion":"Ezreal","slot":"Q","at":796.367,"launched":796.867,
         "speed":1075,"heading":[-0.058,0.998],"miss":189.6,"flight":0.013,
         "outcome":"missed","lead":-189.6}
        """;

    private const string WideWNoLead = """
        {"kind":"skillshot","seq":9,"video_time":907.6,"team":"blue",
         "champion":"Ezreal","slot":"W","at":906.567,"launched":906.567,
         "speed":1074,"heading":[0.458,-0.889],"miss":320.5,"flight":0.489,
         "outcome":"missed"}
        """;

    private const string WideQAhead = """
        {"kind":"skillshot","seq":9,"video_time":1071.5,"team":"blue",
         "champion":"Ezreal","slot":"Q","at":1070.467,"launched":1070.4,
         "speed":945,"heading":[-0.669,-0.744],"miss":400.8,"flight":0.217,
         "outcome":"missed","lead":400.8}
        """;

    private const string WideE = """
        {"kind":"skillshot","seq":9,"video_time":905.1,"team":"blue",
         "champion":"Ezreal","slot":"E","at":903.467,"launched":903.933,
         "speed":1467,"heading":[0.993,0.117],"miss":136.8,"flight":0.474,
         "outcome":"missed","lead":-136.8}
        """;

    private const string NearQ = """
        {"kind":"skillshot","seq":9,"video_time":278.4,"team":"blue",
         "champion":"Ezreal","slot":"Q","at":277.4,"launched":277.5,
         "speed":1147,"heading":[-0.015,-1.0],"miss":16.3,"flight":0.279,
         "outcome":"hit","lead":-16.3}
        """;

    /// <summary>A cast the stage never saw a bolt leave: two thirds of them.</summary>
    private const string UnseenQ = """
        {"kind":"skillshot","video_time":190.2,"slot":"Q","at":189.133,"outcome":"unknown"}
        """;

    /// <summary>A bolt seen leaving the model with no enemy in front of it.</summary>
    private const string FarmingQ = """
        {"kind":"skillshot","video_time":159.8,"slot":"Q","at":158.767,"launched":158.9,
         "speed":965,"heading":[1.0,-0.006],"outcome":"unknown"}
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
    public void A_wide_shot_on_its_own_is_silence()
    {
        // About 7% of the bolts the stage credits are still not the player's
        // shot. One wide shot is what a stray looks like; the fixture's lane
        // phase has exactly one in nine minutes, and it is this one.
        Silent(WideQ);
    }

    [TestMethod]
    public void A_lone_wide_shot_among_many_seen_is_still_silence()
    {
        var policy = Policy();
        for (var i = 0; i < 9; i++)
            Silent(policy, NearQ);
        Silent(policy, WideQ);   // 1 of 10: the stray floor, not a tendency
    }

    [TestMethod]
    public void A_run_of_wide_shots_is_said_on_the_shot_that_made_it_one()
    {
        var reason = OneCue(OnTheEdge(), SecondWideQ);
        StringAssert.Contains(reason, "Q passed 190px behind them");
        StringAssert.Contains(reason, "2 of the last 5 shots that were seen went wide");
    }

    [TestMethod]
    public void Wide_shots_alone_are_not_enough_seen_to_speak()
    {
        // Four wide of four is below the minimum count of seen shots. The
        // count is over the fights, where there is enough signal to say
        // anything; a couple of shots is not that.
        var policy = Policy();
        Silent(policy, WideQ);
        Silent(policy, SecondWideQ);
        Silent(policy, WideWNoLead);
        Silent(policy, WideE);
        StringAssert.Contains(OneCue(policy, WideQAhead), "5 of the last 5 shots that were seen went wide");
    }

    [TestMethod]
    public void The_denominator_is_shots_that_were_seen_never_casts()
    {
        var reason = OneCue(OnTheEdge(), SecondWideQ);
        StringAssert.Contains(reason, "shots that were seen");
        Assert.DoesNotContain("cast", reason.ToLowerInvariant());
    }

    [TestMethod]
    public void A_wide_shot_is_where_the_bolt_passed_never_a_miss()
    {
        // The verdict upstream is geometric, against a hit radius whose
        // justification turned out to be strays. The copy says what was
        // measured and leaves the word "miss" out of it.
        var reason = OneCue(OnTheEdge(), SecondWideQ);
        Assert.DoesNotContain("miss", reason.ToLowerInvariant());
    }

    [TestMethod]
    public void The_side_is_the_sign_of_lead_and_absent_when_lead_is()
    {
        StringAssert.Contains(OneCue(OnTheEdge(), WideQAhead), "ahead of them");
        StringAssert.Contains(OneCue(OnTheEdge(), WideWNoLead), "wide of them");
    }

    [TestMethod]
    public void The_count_runs_over_every_slot_together()
    {
        // The habit is the player's, not the button's; per slot there would
        // rarely be enough seen shots to say anything (E was seen at a target
        // three times in the fixture's game, R once).
        var policy = Policy();
        Silent(policy, NearQ);
        Silent(policy, NearQ);
        Silent(policy, NearQ);
        Silent(policy, WideE);
        StringAssert.Contains(OneCue(policy, WideWNoLead), "W passed 321px wide of them; 2 of the last 5");
    }

    [TestMethod]
    public void A_shot_that_landed_is_silence()
    {
        var policy = Policy();
        for (var i = 0; i < 10; i++)
            Silent(policy, NearQ);
    }

    [TestMethod]
    public void A_cast_that_launched_nothing_is_silence_and_not_counted()
    {
        // A blink, a self-buff, or a shot the stage did not see leave the
        // model, which is two thirds of casts. Either way it is not a fact
        // about the player, and it is not a seen shot.
        var policy = OnTheEdge();
        Silent(policy, UnseenQ);
        StringAssert.Contains(OneCue(policy, SecondWideQ), "2 of the last 5 shots");
    }

    [TestMethod]
    public void A_bolt_with_no_enemy_in_front_of_it_is_silence_and_not_counted()
    {
        // Most of a lane phase. A bolt at minions is farming.
        var policy = OnTheEdge();
        Silent(policy, FarmingQ);
        StringAssert.Contains(OneCue(policy, SecondWideQ), "2 of the last 5 shots");
    }

    [TestMethod]
    public void A_button_press_is_silence()
    {
        // The skillshot event a second later says everything this one does.
        Silent("""
            {"kind":"ability","video_time":159.8,"slot":"Q","at":158.767,"countdown":5,"confirmed":true}
            """);
        Silent("""
            {"kind":"ability","video_time":353.4,"slot":"F","at":353.333,"countdown":180,"confirmed":true}
            """);
    }

    [TestMethod]
    public void The_window_forgets_the_oldest_shot()
    {
        var policy = Policy(options: new ExecutionOptions { WindowSize = 3, MinAimedShots = 2, MinWideShots = 2 });
        Silent(policy, WideQ);
        StringAssert.Contains(OneCue(policy, SecondWideQ), "2 of the last 2");
        Silent(policy, NearQ);
        StringAssert.Contains(OneCue(policy, WideWNoLead), "2 of the last 3");
        StringAssert.Contains(OneCue(policy, WideQAhead), "2 of the last 3", "the first wide shot has rolled off");
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
    public void A_bolt_that_found_them_still_outranks_a_run_of_wide_shots()
    {
        var policy = OnTheEdge();
        policy.OnEvent(Event(SecondWideQ));
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
        var policy = OnTheEdge();
        policy.OnEvent(Event(SecondWideQ));
        policy.OnEvent(Event(HitWhileMoving));
        policy.Resync(null);
        Assert.IsEmpty(policy.DrainCues());
        Silent(policy, WideQAhead);   // the run is forgotten with the game
        Assert.DoesNotContain("of the last", CuesFor(policy, HitWhileStill)[0].Reason);
    }
}
