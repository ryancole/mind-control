using System.Text.Json;
using Jev;
using MindControl.Feed;
using MindControl.Policy;

namespace MindControl.Tests;

/// <summary>
/// The coach asks and the model answers; what is pinned here is the seam.
/// Which moments become a question and which are not questions at all (a
/// dead player, an enemy in fog, a shot nobody saw); what the model is shown,
/// and that nothing from fog is in it; how a yes, a level or an option
/// becomes a key, a step or a cue, stamped with the time of the
/// moment it was asked about; and the plumbing around a model that answers
/// late, fails, or is answered after a resync. Nothing here pins a coaching
/// decision, because none is made here.
/// </summary>
[TestClass]
public sealed class JevPolicyTests
{
    private static readonly Meta Coaching = new()
    {
        Schema = 1, HasAbilities = true, HasThreats = true, HasSkillshots = true,
    };

    private static (JevPolicy Policy, FakeJev Jev) Coach(
        JevOptions? options = null, Meta? meta = null, FrameEnvelope? baseline = null)
    {
        var jev = new FakeJev();
        var policy = new JevPolicy(jev, options ?? new JevOptions { SelfChampion = "Ezreal" });
        policy.Configure(meta ?? Coaching);
        policy.DrainCues();
        if (baseline is not null)
            policy.Resync(baseline);
        return (policy, jev);
    }

    private static ChampionRow Self(double? resource = 0.8, bool? alive = true, double x = 7500, double y = 7500) => new()
    {
        TrackId = 1, Team = "blue", Champion = "Ezreal", IsSelf = true, Visible = true,
        WorldX = x, WorldY = y, Resource = resource, Alive = alive, Level = 6,
    };

    /// <summary>An enemy <paramref name="units"/> due east of a self at (7500, 7500).</summary>
    private static ChampionRow Enemy(double units = 1500, bool visible = true, string champion = "Karma",
        int track = 2, double sinceSeen = 0) => new()
    {
        TrackId = track, Team = "red", Champion = champion, Visible = visible,
        WorldX = 7500 + units, WorldY = 7500, SecondsSinceSeen = sinceSeen,
    };

    private static ChampionRow Ally(int track, double x, double y) => new()
    {
        TrackId = track, Team = "blue", Champion = $"champ{track}", Visible = true, Alive = true,
        WorldX = x, WorldY = y,
    };

    private static FrameEnvelope Frame(double videoTime, params ChampionRow[] champions) =>
        new() { VideoTime = videoTime, Champions = champions };

    private static GameEvent Cast(string slot, double at, int? countdown) => new()
    {
        Kind = EventKind.Ability, VideoTime = at + 0.1, Team = "blue", Champion = "Ezreal",
        Slot = slot, At = at, Countdown = countdown, Confirmed = true,
    };

    private static GameEvent Event(string json) =>
        JsonSerializer.Deserialize<GameEvent>(json, FeedJson.Options)!;

    /// <summary>Feed frames every 100 ms from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static void Run(JevPolicy policy, double from, double to, params ChampionRow[] champions)
    {
        for (var t = from; t <= to + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Frame(t, champions));
    }

    // The fixture's threats (data/coach-full-20260902-222718.jsonl), verbatim.

    /// <summary>From the upper right, 12 damage, the player 0.1px across its line.</summary>
    private const string HitWhileStill = """
        {"kind":"threat","seq":9,"video_time":219.0,"team":"blue",
         "champion":"Ezreal","at":218.1,"arrival":218.431,"closest":53.5,
         "speed":897,"heading":[-0.425,0.905],"outcome":"hit","damage":12,
         "moved_across":0.1,"origin":37.0}
        """;

    private const string WideQ = """
        {"kind":"skillshot","seq":9,"video_time":482.9,"team":"blue",
         "champion":"Ezreal","slot":"Q","at":481.7,"launched":482.033,
         "speed":1054,"heading":[-0.322,-0.947],"miss":314.5,"flight":0.291,
         "outcome":"missed","lead":-314.5}
        """;

    private const string NearQ = """
        {"kind":"skillshot","seq":9,"video_time":278.4,"team":"blue",
         "champion":"Ezreal","slot":"Q","at":277.4,"launched":277.5,
         "speed":1147,"heading":[-0.015,-1.0],"miss":16.3,"flight":0.279,
         "outcome":"hit","lead":-16.3}
        """;

    // --- The moment itself: buttons ---

    [TestMethod]
    public void Nothing_is_asked_before_the_HUD_has_shown_a_button_come_back()
    {
        // A slot may not be skilled; until a cooldown has been printed for it
        // there is no button to ask about, only a guess.
        var (policy, jev) = Coach();
        Run(policy, 10, 12, Self(), Enemy(900));
        Assert.IsEmpty(jev.Asks);
    }

    [TestMethod]
    public void A_button_that_is_up_with_an_enemy_in_view_is_a_question()
    {
        var (policy, jev) = Coach();
        policy.OnEvent(Cast("Q", 10, 5));
        Run(policy, 14.5, 14.9, Self(), Enemy(900));   // still on the printed cooldown
        Assert.IsEmpty(jev.Asks);

        policy.OnFrame(Frame(15.5, Self(), Enemy(900)));

        var ask = jev.Asks.Single();
        CollectionAssert.AreEqual(new[] { "press_Q" }, ask.Questions.Keys.ToArray(), "W has never been seen cast");
        Assert.AreEqual(15.5, ask.State.VideoTime);
        var q = ask.State.Abilities.Single(a => a.Slot == "Q");
        Assert.AreEqual("up", q.Status);
        Assert.AreEqual(0.5, q.SecondsPastPrintedCooldown);
        Assert.AreEqual(1150, q.Range);
        Assert.AreEqual("unknown", ask.State.Abilities.Single(a => a.Slot == "W").Status);
        var karma = ask.State.VisibleEnemies.Single();
        Assert.AreEqual("Karma", karma.Champion);
        Assert.AreEqual(900, karma.DistanceUnits);
        Assert.AreEqual("right", karma.ScreenDirection);
        CollectionAssert.AreEqual(new[] { "Q", "W" }, karma.InRangeOf.ToArray());
        Assert.AreEqual(0.8, ask.State.Player!.Mana);
        Assert.IsNotNull(ask.Options, "a question about the moment is not retried");
        Assert.AreEqual(0, ask.Options!.Retry!.MaxRetries);
    }

    [TestMethod]
    public void A_yes_presses_the_key_at_the_moment_it_was_asked_about()
    {
        var (policy, jev) = Coach();
        jev.Script = (id, _) => id == "press_Q" ? FakeJev.Yes : null;
        policy.OnEvent(Cast("Q", 10, 5));
        Run(policy, 15.5, 17.5, Self(), Enemy(900));

        var presses = policy.DrainKeys();
        Assert.IsNotEmpty(presses);
        Assert.AreEqual("Q", presses[0].Key);
        Assert.AreEqual(15.5, presses[0].VideoTime);
        Assert.AreEqual(2, presses[0].Priority);
        Assert.AreEqual("coach would have pressed Q here: Karma has been in Q range (900 units) for 0.0s with Q up",
            presses[0].Sentence);
    }

    [TestMethod]
    public void The_coach_is_reminded_of_what_it_pressed()
    {
        var (policy, jev) = Coach();
        jev.Script = (id, _) => id == "press_Q" ? FakeJev.Yes : null;
        policy.OnEvent(Cast("Q", 10, 5));
        policy.OnFrame(Frame(15.5, Self(), Enemy(900)));
        policy.OnFrame(Frame(16.0, Self(), Enemy(900)));

        var reminded = jev.Last.State.Coach.Single();
        Assert.AreEqual("pressed Q", reminded.Did);
        Assert.AreEqual(0.5, reminded.SecondsAgo);
    }

    [TestMethod]
    public void A_yes_below_the_threshold_is_a_no()
    {
        var (policy, jev) = Coach();
        jev.Script = (id, _) => id == "press_Q" ? new NoulAnswer(0.5) : null;
        policy.OnEvent(Cast("Q", 10, 5));
        Run(policy, 15.5, 17.5, Self(), Enemy(900));
        Assert.IsEmpty(policy.DrainKeys());
        Assert.IsNotEmpty(jev.Asks, "it was asked; the answer just fell short");
    }

    [TestMethod]
    public void An_enemy_in_fog_is_never_shown_to_the_coach()
    {
        // Fair play, the rule every question keeps: a row that is not
        // visible is information the player does not have.
        var (policy, jev) = Coach();
        policy.OnEvent(Cast("Q", 10, 5));
        Run(policy, 15.5, 16, Self(), Enemy(900, visible: false));
        Assert.IsEmpty(jev.Asks, "nobody on the screen means no question");

        policy.OnFrame(Frame(16.5, Self(), Enemy(900, visible: false), Enemy(2000, champion: "Zed", track: 3)));
        var shown = jev.Asks.Single().State;
        Assert.AreEqual("Zed", shown.VisibleEnemies.Single().Champion);
        var json = JsonSerializer.Serialize(shown, Moment.JsonOptions);
        Assert.DoesNotContain("Karma", json);
        Assert.DoesNotContain("seconds_since_seen", json);
        Assert.DoesNotContain("null", json);
        StringAssert.Contains(json, "\"visible_enemies\"");
    }

    [TestMethod]
    public void Nothing_is_asked_while_dead()
    {
        var (policy, jev) = Coach();
        policy.OnEvent(Cast("Q", 10, 5));
        Run(policy, 15.5, 17, Self(alive: false), Enemy(900));
        Assert.IsEmpty(jev.Asks);
    }

    [TestMethod]
    public void One_question_about_the_moment_in_flight_and_no_more_often_than_the_interval()
    {
        var (policy, jev) = Coach();
        policy.OnEvent(Cast("Q", 10, 5));

        jev.Hold = true;
        Run(policy, 15.5, 16.5, Self(), Enemy(900));
        Assert.HasCount(1, jev.Asks, "the first is still unanswered");

        jev.Release();
        jev.Hold = false;
        Run(policy, 16.6, 17.5, Self(), Enemy(900));
        // 16.6 (the answer landed), then 16.9, 17.2, 17.5: a quarter second apart.
        CollectionAssert.AreEqual(new[] { 15.5, 16.6, 16.9, 17.2, 17.5 }, jev.Asks.Select(a => a.State.VideoTime).ToArray());
    }

    [TestMethod]
    public void A_button_the_player_just_pressed_is_not_asked_about()
    {
        var (policy, jev) = Coach();
        policy.OnEvent(Cast("Q", 10, 5));
        policy.OnEvent(Cast("W", 10, 5));
        policy.OnFrame(Frame(15.5, Self(), Enemy(900)));
        CollectionAssert.AreEqual(new[] { "press_Q", "press_W" }, jev.Last.Questions.Keys.Order().ToArray());

        policy.OnEvent(Cast("Q", 15.6, 5));   // they pressed it: up again at 20.6
        policy.OnFrame(Frame(16, Self(), Enemy(900)));
        CollectionAssert.AreEqual(new[] { "press_W" }, jev.Last.Questions.Keys.ToArray());
        Assert.AreEqual("cooldown", jev.Last.State.Abilities.Single(a => a.Slot == "Q").Status);
    }

    [TestMethod]
    public void A_cast_whose_countdown_was_not_read_makes_the_button_unknown_again()
    {
        var (policy, jev) = Coach();
        policy.OnEvent(Cast("Q", 10, 5));
        policy.OnEvent(Cast("Q", 16, countdown: null));
        Run(policy, 16, 30, Self(), Enemy(900));
        Assert.IsEmpty(jev.Asks);
    }

    [TestMethod]
    public void Resync_drops_the_answers_still_in_flight()
    {
        var (policy, jev) = Coach();
        jev.Script = (id, _) => id == "press_Q" ? FakeJev.Yes : null;
        policy.OnEvent(Cast("Q", 10, 5));
        jev.Hold = true;
        policy.OnFrame(Frame(15.5, Self(), Enemy(900)));
        Assert.HasCount(1, jev.Asks);

        policy.Resync(Frame(15.6, Self(), Enemy(900)));
        jev.Release();
        policy.OnFrame(Frame(15.7, Self(), Enemy(900)));

        Assert.IsEmpty(policy.DrainKeys(), "an answer about a past we stopped trusting");
        Assert.HasCount(1, jev.Asks, "and the cooldown was forgotten with the gap, so nothing to ask");
    }

    [TestMethod]
    public void A_model_that_does_not_answer_is_said_once_and_coaching_goes_on()
    {
        var (policy, jev) = Coach();
        jev.Fault = new JevConnectionException("POST /v1/systemone failed: refused", "POST /v1/systemone");
        policy.OnEvent(Cast("Q", 10, 5));
        Run(policy, 15.5, 17, Self(), Enemy(900));

        var cues = policy.DrainCues();
        Assert.HasCount(1, cues);
        StringAssert.Contains(cues[0].Reason, "did not answer");
        StringAssert.Contains(cues[0].Reason, "refused");
        Assert.IsGreaterThan(1, jev.Asks.Count, "it kept asking");

        jev.Fault = null;
        jev.Script = (id, _) => id == "press_Q" ? FakeJev.Yes : null;
        Run(policy, 17.1, 17.5, Self(), Enemy(900));
        Assert.IsNotEmpty(policy.DrainKeys(), "and took the next answer");
    }

    [TestMethod]
    public void Every_question_and_answer_reaches_the_audit()
    {
        var jev = new FakeJev();
        var audited = new List<Consultation>();
        var policy = new JevPolicy(jev, new JevOptions { SelfChampion = "Ezreal" }, audited.Add);
        policy.Configure(Coaching);
        policy.OnEvent(Cast("Q", 10, 5));
        policy.OnFrame(Frame(15.5, Self(), Enemy(900)));

        var consultation = audited.Single();
        Assert.AreEqual("now", consultation.Occasion);
        Assert.AreEqual(15.5, consultation.VideoTime);
        Assert.IsNotNull(consultation.Response);
        Assert.IsNull(consultation.Error);
        Assert.IsTrue(consultation.Questions.ContainsKey("press_Q"));
    }

    // --- A bolt at the player ---

    [TestMethod]
    public void A_bolt_is_asked_about_with_both_sides_across_its_line()
    {
        var (policy, jev) = Coach(baseline: Frame(218, Self(), Enemy(900)));
        policy.OnEvent(Event(HitWhileStill));

        var ask = jev.Asks.Single();
        var bolt = (BoltOccasion)ask.State.Occasion!;
        Assert.AreEqual("the upper right", bolt.From);
        Assert.AreEqual("hit", bolt.Outcome);
        Assert.AreEqual(12, bolt.Damage);
        Assert.AreEqual(0.1, bolt.MovedAcrossPx);
        Assert.AreEqual(0.33, bolt.WarningSeconds);
        Assert.IsNull(bolt.PreviousLandingSecondsAgo);
        CollectionAssert.AreEquivalent(new[] { "remark", "step", "side" }, ask.Questions.Keys.ToArray());

        var side = (ChoiceQuestion)ask.Questions["side"];
        CollectionAssert.AreEquivalent(new[] { "up-left", "down-right" }, side.Options.ToArray());
        StringAssert.Contains((string)side.Criteria["up-left"]!, "toward the player's own base");
        StringAssert.Contains((string)side.Criteria["up-left"]!, "away from the nearest visible enemy (Karma)");
        StringAssert.Contains((string)side.Criteria["down-right"]!, "away from the player's own base");
        Assert.IsNotNull(ask.Options?.Retry is null ? "" : null, "a question about an event keeps the client's retries");
    }

    [TestMethod]
    public void A_yes_steps_the_chosen_way_at_the_bolts_first_sighting()
    {
        var (policy, jev) = Coach(baseline: Frame(218, Self()));
        jev.Script = (id, q) => id switch
        {
            "remark" => FakeJev.Yes,
            "step" => FakeJev.Yes,
            "side" => FakeJev.Pick(q, "up-left"),
            _ => null,
        };
        policy.OnEvent(Event(HitWhileStill));

        var step = policy.DrainMoves().Single();
        Assert.AreEqual(218.1, step.VideoTime, 1e-9, "stamped at first sighting, not at the event");
        Assert.AreEqual("up-left", step.Direction);
        Assert.AreEqual(-0.905, step.Dx, 0.001);
        Assert.AreEqual(-0.425, step.Dy, 0.001);
        Assert.AreEqual(1.0, double.Hypot(step.Dx, step.Dy), 1e-9);
        Assert.AreEqual(3, step.Priority);
        Assert.AreEqual(
            "coach would have stepped up-left here: a bolt from the upper right hit you for 12 "
            + "while you stood still, 0.33s after it came into view",
            step.Sentence);

        var cue = policy.DrainCues().Single();
        Assert.AreEqual(219.0, cue.VideoTime);
        Assert.AreEqual(3, cue.Priority);
        StringAssert.StartsWith(cue.Reason, "a bolt from the upper right hit you for 12");
        Assert.DoesNotContain("skillshot", cue.Reason);
        Assert.DoesNotContain("dodge", cue.Reason);
    }

    [TestMethod]
    public void A_no_on_the_step_is_no_step_even_with_a_side_chosen()
    {
        var (policy, jev) = Coach(baseline: Frame(218, Self()));
        jev.Script = (id, q) => id == "side" ? FakeJev.Pick(q, "up-left") : null;
        policy.OnEvent(Event(HitWhileStill));
        Assert.IsEmpty(policy.DrainMoves());
        Assert.IsEmpty(policy.DrainCues());
    }

    [TestMethod]
    public void A_bolt_without_a_heading_offers_no_step()
    {
        var (policy, jev) = Coach(baseline: Frame(99, Self()));
        policy.OnEvent(Event("""
            {"kind":"threat","video_time":100,"at":99.6,"arrival":100.0,"closest":9,
             "speed":900,"outcome":"hit","damage":50,"moved_across":0.0}
            """));
        CollectionAssert.AreEqual(new[] { "remark" }, jev.Asks.Single().Questions.Keys.ToArray());
        Assert.IsNull(((BoltOccasion)jev.Last.State.Occasion!).From);
    }

    [TestMethod]
    public void The_previous_landing_is_told_to_the_coach()
    {
        // The fixture's pair at 444s: arrivals 0.22s apart, 43 damage each.
        var (policy, jev) = Coach(baseline: Frame(443, Self()));
        policy.OnEvent(Event("""
            {"kind":"threat","video_time":444.6,"at":443.9,"arrival":444.083,"closest":32.9,
             "speed":1176,"heading":[-0.31,0.95],"outcome":"hit","damage":43,"moved_across":16.6,"origin":95.0}
            """));
        policy.OnEvent(Event("""
            {"kind":"threat","video_time":444.9,"at":444.1,"arrival":444.306,"closest":108.8,
             "speed":1261,"heading":[-0.82,0.57],"outcome":"hit","damage":43,"moved_across":1.1,"origin":152.0}
            """));

        var second = (BoltOccasion)jev.Last.State.Occasion!;
        Assert.AreEqual(0.22, second.PreviousLandingSecondsAgo);
        Assert.AreEqual(43, second.PreviousLandingDamage);
    }

    [TestMethod]
    public void The_copy_states_what_the_bolt_did_and_what_the_player_was_doing()
    {
        var (policy, jev) = Coach(baseline: Frame(219, Self()));
        jev.Script = (id, _) => id == "remark" ? FakeJev.Yes : null;
        policy.OnEvent(Event("""
            {"kind":"threat","video_time":220.7,"at":219.7,"arrival":220.134,"closest":102.0,
             "speed":814,"heading":[-0.38,0.92],"outcome":"dodged","moved_across":53.2}
            """));
        StringAssert.Contains(policy.DrainCues().Single().Reason,
            "a bolt from above missed you while you moved 53px across its line");
    }

    // --- A shot of the player's ---

    [TestMethod]
    public void A_shot_nobody_saw_at_a_target_is_not_a_question()
    {
        var (policy, jev) = Coach(baseline: Frame(150, Self()));
        // A cast the stage never saw a bolt leave, and a bolt with no enemy in front of it.
        policy.OnEvent(Event("""{"kind":"skillshot","video_time":190.2,"slot":"Q","at":189.133,"outcome":"unknown"}"""));
        policy.OnEvent(Event("""
            {"kind":"skillshot","video_time":159.8,"slot":"Q","at":158.767,"launched":158.9,
             "speed":965,"heading":[1.0,-0.006],"outcome":"unknown"}
            """));
        Assert.IsEmpty(jev.Asks);
    }

    [TestMethod]
    public void A_seen_shot_is_asked_about_with_the_recent_run_and_a_yes_is_a_cue()
    {
        var (policy, jev) = Coach(baseline: Frame(270, Self()));
        for (var i = 0; i < 3; i++)
            policy.OnEvent(Event(NearQ));
        jev.Script = (id, _) => id == "remark" ? FakeJev.Yes : null;
        policy.OnEvent(Event(WideQ));

        Assert.HasCount(4, jev.Asks, "every seen shot is a question; a near one usually a no");
        var shot = (ShotOccasion)jev.Last.State.Occasion!;
        Assert.AreEqual("Q", shot.Slot);
        Assert.AreEqual(315, shot.PassedPx);
        Assert.AreEqual("behind them", shot.Side);
        Assert.IsTrue(shot.Wide);
        CollectionAssert.AreEqual(new[] { false, false, false, true }, shot.RecentShotsSeenAtATarget.Select(s => s.Wide).ToArray());

        var cue = policy.DrainCues().Single();
        Assert.AreEqual(482.9, cue.VideoTime);
        Assert.AreEqual(2, cue.Priority);
        Assert.AreEqual("Q passed 315px behind them; 1 of the last 4 shots that were seen went wide", cue.Reason);
        Assert.DoesNotContain("miss", cue.Reason.ToLowerInvariant());
        Assert.DoesNotContain("cast", cue.Reason.ToLowerInvariant());
    }

    // --- Late answers ---

    [TestMethod]
    public void An_answer_that_arrives_later_lands_on_the_next_call_stamped_when_it_was_asked()
    {
        var (policy, jev) = Coach();
        jev.Script = (id, _) => id == "press_Q" ? FakeJev.Yes : null;
        policy.OnEvent(Cast("Q", 10, 5));
        jev.Hold = true;

        policy.OnFrame(Frame(15.5, Self(), Enemy(900)));
        Assert.IsEmpty(policy.DrainKeys(), "nothing has been answered yet");

        jev.Release();
        policy.OnFrame(Frame(15.6, Self(), Enemy(900)));
        var press = policy.DrainKeys().Single();
        Assert.AreEqual(15.5, press.VideoTime, "stamped when it was asked, not when it was answered");
    }

    // --- Identity ---

    [TestMethod]
    public void Self_latches_to_the_majority_when_the_camera_flag_flaps()
    {
        var (policy, jev) = Coach(new JevOptions(), baseline: Frame(0, Self(), Ally(3, 3000, 3000)));
        for (var i = 0; i < 10; i++)
            policy.OnFrame(Frame(i * 0.1, Self(), Ally(3, 3000, 3000)));
        policy.OnEvent(Cast("Q", 1, 1));

        // The flag flaps onto the ally; the question is still about Ezreal's seat.
        policy.OnFrame(Frame(2.0, Self() with { IsSelf = false }, Ally(3, 3000, 3000) with { IsSelf = true }, Enemy(900)));
        Assert.AreEqual("Ezreal", jev.Asks.Single().State.Player!.Champion, "the question should be about the majority self");
    }

    [TestMethod]
    public void An_identity_correction_migrates_the_self_majority()
    {
        var (policy, jev) = Coach(new JevOptions(), baseline: Frame(0, Self(), Ally(3, 3000, 3000)));
        for (var i = 0; i < 5; i++)
            policy.OnFrame(Frame(i * 0.1, Self(), Ally(3, 3000, 3000)));

        policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Identified, Team = "blue", TrackId = 1, Champion = "Annie", Replaces = "Ezreal", VideoTime = 1.0,
        });

        // Self renamed and no longer flagged; the buttons are still asked about from Annie's seat.
        var renamed = Self() with { Champion = "Annie", IsSelf = false };
        policy.OnEvent(Cast("Q", 1.0, 0));
        policy.OnFrame(Frame(1.1, renamed, Ally(3, 3000, 3000), Enemy()));
        Assert.AreEqual("Annie", jev.Asks.Single().State.Player!.Champion, "self survives the rename");
    }

    [TestMethod]
    public void A_mutual_identity_exchange_swaps_the_self_votes()
    {
        var mislabeledSelf = Self() with { Champion = "Leona" };
        var mislabeledAlly = Ally(3, 3000, 3000) with { Champion = "Ezreal" };
        var (policy, jev) = Coach(new JevOptions(), baseline: Frame(0, mislabeledSelf, mislabeledAlly));
        for (var i = 0; i < 5; i++)
            policy.OnFrame(Frame(i * 0.1, mislabeledSelf, mislabeledAlly));

        policy.OnEvent(new GameEvent { Kind = EventKind.Identified, Team = "blue", TrackId = 1, Champion = "Ezreal", Replaces = "Leona", VideoTime = 1.0 });
        policy.OnEvent(new GameEvent { Kind = EventKind.Identified, Team = "blue", TrackId = 3, Champion = "Leona", Replaces = "Ezreal", VideoTime = 1.0 });

        // Labels now honest: the player is Ezreal, and the question must be
        // from Ezreal's seat -- Leona's here means self is still on the old name.
        policy.OnEvent(Cast("Q", 1.0, 0));
        policy.OnFrame(Frame(1.1, mislabeledSelf with { Champion = "Ezreal" }, mislabeledAlly with { Champion = "Leona" }, Enemy()));
        Assert.AreEqual("Ezreal", jev.Asks.Single().State.Player!.Champion, "self did not survive the exchange");
    }

    // --- The feed's capabilities ---

    [TestMethod]
    public void A_feed_without_the_coaching_stages_says_so_once()
    {
        var policy = new JevPolicy(new FakeJev());
        policy.Configure(new Meta { Schema = 1 });
        var cues = policy.DrainCues();
        Assert.HasCount(1, cues);
        StringAssert.Contains(cues[0].Reason, "--coach");
        StringAssert.Contains(cues[0].Reason, "no button will be pressed");
        Assert.IsEmpty(policy.DrainCues(), "said once");
        policy.Resync(null);
        Assert.IsEmpty(policy.DrainCues());
    }

    [TestMethod]
    public void A_coaching_feed_gets_no_notice()
    {
        var policy = new JevPolicy(new FakeJev());
        policy.Configure(Coaching);
        Assert.IsEmpty(policy.DrainCues());
    }
}
