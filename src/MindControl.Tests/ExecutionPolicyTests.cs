using System.Text.Json;
using MindControl.Feed;
using MindControl.Policy;

namespace MindControl.Tests;

/// <summary>
/// The execution readout, and the wire mapping it depends on. What is pinned
/// here is the accounting, not the wording: that each of the three kinds is
/// recognized, that a cast with no bolt and a bolt with no target are said
/// differently from a real miss, that nothing here ever moves the cursor, and
/// that a feed without the coaching stages says so once instead of going
/// silently dead.
/// </summary>
[TestClass]
public sealed class ExecutionPolicyTests
{
    private static readonly Meta Coaching = new()
    {
        Schema = 1, HasAbilities = true, HasThreats = true, HasSkillshots = true,
    };

    private static ExecutionPolicy Policy(Meta? meta = null)
    {
        var policy = new ExecutionPolicy();
        policy.Configure(meta ?? Coaching);
        policy.DrainCues();       // discard any configure-time notice
        return policy;
    }

    private static GameEvent Event(string json) =>
        JsonSerializer.Deserialize<GameEvent>(json, FeedJson.Options)!;

    private static string CueFor(string json)
    {
        var policy = Policy();
        Assert.IsNull(policy.OnEvent(Event(json)), "execution coaching never moves the cursor");
        var cues = policy.DrainCues();
        Assert.HasCount(1, cues);
        return cues[0].Reason;
    }

    [TestMethod]
    public void An_ability_names_the_button_and_the_cooldown()
    {
        var reason = CueFor("""
            {"kind":"ability","seq":9,"video_time":214.1,"game_time":300,
             "team":"blue","champion":"Ezreal","slot":"Q","at":214.1,
             "countdown":5,"confirmed":true}
            """);
        StringAssert.Contains(reason, "Q");
        StringAssert.Contains(reason, "5s");
    }

    [TestMethod]
    public void A_skillshot_that_landed_reports_how_far_off_centre()
    {
        var reason = CueFor("""
            {"kind":"skillshot","seq":9,"video_time":214.1,"team":"blue",
             "champion":"Ezreal","slot":"Q","at":214.1,"launched":214.4,
             "speed":852,"heading":[1.0,-0.027],"miss":6.1,"flight":0.823,
             "outcome":"hit","fall":0.051}
            """);
        StringAssert.Contains(reason, "hit");
        StringAssert.Contains(reason, "6px");
    }

    [TestMethod]
    public void A_skillshot_that_went_wide_reports_the_miss_and_the_side()
    {
        var reason = CueFor("""
            {"kind":"skillshot","seq":9,"video_time":277.4,"team":"blue",
             "champion":"Ezreal","slot":"Q","at":277.4,"launched":277.5,
             "speed":870,"heading":[-0.499,-0.867],"miss":274.3,"flight":0.594,
             "outcome":"missed","lead":-274.3}
            """);
        StringAssert.Contains(reason, "274px");
        StringAssert.Contains(reason, "behind");
    }

    [TestMethod]
    public void A_cast_that_launched_nothing_is_not_reported_as_a_miss()
    {
        // A blink or a self-buff. Calling it a missed skillshot would be the
        // readout inventing a shot the vision layer never saw.
        var reason = CueFor("""
            {"kind":"skillshot","seq":9,"video_time":204.5,"team":"blue",
             "champion":"Ezreal","slot":"W","at":204.5,"outcome":"unknown"}
            """);
        StringAssert.Contains(reason, "no bolt");
        Assert.DoesNotContain("missed", reason);
    }

    [TestMethod]
    public void A_bolt_with_no_enemy_in_front_of_it_says_so_rather_than_scoring_it()
    {
        var reason = CueFor("""
            {"kind":"skillshot","seq":9,"video_time":259.2,"team":"blue",
             "champion":"Ezreal","slot":"Q","at":259.2,"launched":259.2,
             "speed":1008,"heading":[0.991,0.133],"outcome":"unknown"}
            """);
        StringAssert.Contains(reason, "no enemy");
    }

    [TestMethod]
    public void A_threat_that_hit_carries_the_damage_and_the_response()
    {
        var reason = CueFor("""
            {"kind":"threat","seq":9,"video_time":221.0,"team":"blue",
             "champion":"Ezreal","at":221.0,"arrival":221.3,"closest":42.0,
             "speed":1500,"heading":[1.0,0.0],"outcome":"hit","damage":137,
             "moved_across":8.0,"origin":140.0}
            """);
        StringAssert.Contains(reason, "137");
        StringAssert.Contains(reason, "not moving");
    }

    [TestMethod]
    public void A_threat_that_missed_reports_the_movement_across_it()
    {
        var reason = CueFor("""
            {"kind":"threat","seq":9,"video_time":221.0,"team":"blue",
             "champion":"Ezreal","at":221.0,"arrival":221.3,"closest":42.0,
             "speed":1500,"heading":[1.0,0.0],"outcome":"dodged",
             "moved_across":63.0}
            """);
        StringAssert.Contains(reason, "63px");
    }

    [TestMethod]
    public void A_hit_outranks_a_dodge_which_outranks_a_cast()
    {
        var policy = Policy();
        policy.OnEvent(Event("""{"kind":"ability","video_time":1,"slot":"Q","at":1}"""));
        policy.OnEvent(Event("""
            {"kind":"threat","video_time":2,"at":2,"arrival":2.2,"closest":9,
             "speed":1500,"outcome":"dodged"}
            """));
        policy.OnEvent(Event("""
            {"kind":"threat","video_time":3,"at":3,"arrival":3.2,"closest":9,
             "speed":1500,"outcome":"hit","damage":80}
            """));
        var priorities = policy.DrainCues().Select(c => c.Priority).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, priorities);
    }

    [TestMethod]
    public void Kinds_it_does_not_own_are_left_alone()
    {
        var policy = Policy();
        policy.OnEvent(Event("""{"kind":"death","video_time":5,"champion":"Ahri"}"""));
        Assert.IsEmpty(policy.DrainCues());
    }

    [TestMethod]
    public void A_feed_without_the_coaching_stages_says_so_once()
    {
        var policy = new ExecutionPolicy();
        policy.Configure(new Meta { Schema = 1 });
        var cues = policy.DrainCues();
        Assert.HasCount(1, cues);
        StringAssert.Contains(cues[0].Reason, "--coach");
        Assert.IsEmpty(policy.DrainCues(), "the notice is drained, not repeated");
    }

    [TestMethod]
    public void A_resync_drops_cues_the_feed_could_not_vouch_for()
    {
        var policy = Policy();
        policy.OnEvent(Event("""{"kind":"ability","video_time":1,"slot":"Q","at":1}"""));
        policy.Resync(null);
        Assert.IsEmpty(policy.DrainCues());
    }
}
