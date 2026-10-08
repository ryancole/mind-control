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
        Schema = 2, HasNameplates = true, HasAbilities = true, HasThreats = true, HasSkillshots = true,
        HasMinions = true, HasMinionDots = true, HasLastHits = true, HasTurrets = true,
        WorldBounds = new() { MaxX = 14870, MaxY = 14980 },
    };

    private static (JevPolicy Policy, FakeJev Jev) Coach(
        JevOptions? options = null, Meta? meta = null, FrameEnvelope? baseline = null)
    {
        var jev = new FakeJev();
        var policy = new JevPolicy(jev, options ?? new JevOptions());
        policy.Configure(meta ?? Coaching);
        policy.DrainCues();
        if (baseline is not null)
            policy.Resync(baseline);
        return (policy, jev);
    }

    private static ChampionRow Self(double? resource = 0.8, bool? alive = true, double x = 7500, double y = 7500,
        int? level = 6, string[]? learnable = null) => new()
    {
        TrackId = 1, Team = "blue", Champion = "Ezreal", IsSelf = true, Visible = true,
        WorldX = x, WorldY = y, Resource = resource, Alive = alive, Level = level, Learnable = learnable,
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

    /// <summary>
    /// Casts of <paramref name="slots"/> long ago whose countdowns were not
    /// read: seen cast, so not up, and not offered as never seen cast either.
    /// Leaves the one button a test sets up as the only one to press.
    /// </summary>
    private static void Unread(JevPolicy policy, params string[] slots)
    {
        foreach (var slot in slots)
            policy.OnEvent(Cast(slot, 1, countdown: null));
    }

    private static GameEvent Event(string json) =>
        JsonSerializer.Deserialize<GameEvent>(json, FeedJson.Options)!;

    /// <summary>Feed frames every 100 ms from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static void Run(JevPolicy policy, double from, double to, params ChampionRow[] champions)
    {
        for (var t = from; t <= to + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Frame(t, champions));
    }

    // --- The root question and its options ---

    /// <summary>The options the root question offered, carry_on first; none when it was not asked.</summary>
    private static string[] Offered(FakeJev.Ask ask) =>
        ask.Questions.TryGetValue("decide", out var q) ? ((ChoiceQuestion)q).Options.ToArray() : [];

    /// <summary>The asks whose root offered <paramref name="option"/>.</summary>
    private static FakeJev.Ask[] Offering(FakeJev jev, string option) =>
        jev.Asks.Where(a => Offered(a).Contains(option)).ToArray();

    /// <summary>What the root said of one of its options.</summary>
    private static string Criterion(FakeJev.Ask ask, string option) =>
        (string)((ChoiceQuestion)ask.Questions["decide"]).Criteria[option]!;

    /// <summary>
    /// A model that picks <paramref name="option"/> at the root whenever it is
    /// offered (and carry_on, the no-order option, when it is not), and each
    /// follow-up named its pick; every other question its silent default.
    /// </summary>
    private static Func<string, Question, Answer?> Choose(string option, params (string Question, string Pick)[] picks) =>
        (id, q) => id == "decide"
            ? FakeJev.Pick(q, ((ChoiceQuestion)q).Options.Contains(option) ? option : "carry_on")
            : picks.Where(p => p.Question == id).Select(p => (Answer?)FakeJev.Pick(q, p.Pick)).FirstOrDefault();

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

    // --- The moment itself: the root question ---

    [TestMethod]
    public void The_root_offers_only_what_the_moment_makes_possible_and_carry_on_first()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Clocked(100, 50, Idle()));

        var ask = jev.Asks.Single();
        CollectionAssert.AreEqual(new[] { "carry_on", "buy", "walk_to_lane" }, Offered(ask),
            "in the fountain with the clock running: no button up, nobody on the screen, nothing in reach");
        CollectionAssert.AreEquivalent(new[] { "decide", "lane" }, ask.Questions.Keys.ToArray(),
            "the walk's follow-up goes in the same request");
        StringAssert.StartsWith(Criterion(ask, "walk_to_lane"), "walk_to_lane: a step toward a lane's minion wave");
        Assert.AreEqual(0, ask.Options!.Retry!.MaxRetries, "a question about the moment is not retried");
    }

    [TestMethod]
    public void Carry_on_or_a_pick_below_the_threshold_is_no_order()
    {
        var (policy, jev) = Coach();
        policy.OnEvent(Cast("Q", 10, 5));
        Run(policy, 15.5, 16.5, Self(), Enemy(900));
        Assert.IsEmpty(policy.DrainKeys(), "the silent model picks carry_on");

        jev.Script = (id, _) => id == "decide"
            ? new ChoiceAnswer("use_ability", new Dictionary<string, double>
            {
                ["carry_on"] = 0.3, ["use_ability"] = 0.35, ["run_away"] = 0.35,
            }, 0.5)
            : null;
        Run(policy, 16.6, 17.5, Self(), Enemy(900));
        Assert.IsEmpty(policy.DrainKeys(), "0.35 is under DecideAt");
        Assert.IsNotEmpty(Offering(jev, "use_ability"), "it was asked; the pick just fell short");
    }

    [TestMethod]
    public void One_request_carries_the_root_and_every_offered_branchs_follow_up()
    {
        var (policy, jev) = Coach();
        policy.OnEvent(Cast("Q", 10, 5));
        policy.OnEvent(Cast("W", 10, 5));
        jev.Hold = true;
        for (var t = 100.0; t <= 103.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 50, Idle(), Enemy(900)));

        var ask = jev.Asks.Single();
        CollectionAssert.IsSubsetOf(new[] { "carry_on", "use_ability", "walk_to_lane", "run_away" }, Offered(ask));
        CollectionAssert.AreEquivalent(new[] { "decide", "ability", "lane" }, ask.Questions.Keys.ToArray());
    }

    // --- Buttons ---

    [TestMethod]
    public void A_button_never_seen_cast_is_offered_as_up_if_it_holds_a_point()
    {
        // No cooldown was ever seen running on it, so it is up if it is
        // skilled; whether it is, at the player's level, is the model's call.
        var (policy, jev) = Coach();
        policy.OnFrame(Frame(10, Self(level: 5), Enemy(900)));

        var ask = Offering(jev, "use_ability").Single();
        StringAssert.StartsWith(Criterion(ask, "use_ability"), "use_ability: throw one of the buttons that is up (E, Q, W)",
            "the ultimate only from level six");
        var q = (string)((ChoiceQuestion)ask.Questions["ability"]).Criteria["Q"]!;
        StringAssert.Contains(q, "never seen cast this game, so up if it holds a point");
        StringAssert.Contains(ask.State.Abilities.Single(a => a.Slot == "Q").Note!, "up if it holds a point");
    }

    [TestMethod]
    public void A_yes_on_a_button_never_seen_cast_says_so()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("use_ability", ("ability", "Q"));
        policy.OnFrame(Frame(10, Self(), Enemy(900)));

        Assert.AreEqual("coach would have pressed Q here: Karma has been in Q range (900 units) for 0.0s with Q never seen on cooldown",
            policy.DrainKeys().Single().Sentence);
    }

    [TestMethod]
    public void Enemy_minions_in_a_buttons_reach_are_something_to_throw_it_at()
    {
        // Farming with abilities: no enemy champion on the screen, but the
        // wave inside Q's reach.
        var (policy, jev) = Coach();
        jev.Script = Choose("use_ability", ("ability", "Q"));
        policy.OnFrame(Clocked(200, 300, InLane(Bar(MinionTeam.Red, 9300, 1400, health: 0.3), Bar(MinionTeam.Red, 9700, 1400))));

        var ask = Offering(jev, "use_ability").Single();
        var q = ask.State.Abilities.Single(a => a.Slot == "Q");
        Assert.AreEqual((2, 0.3), (q.EnemyMinionsInRange, q.LowestEnemyMinionInRange));
        StringAssert.Contains((string)((ChoiceQuestion)ask.Questions["ability"]).Criteria["Q"]!,
            "enemy minions inside its range: 2, the lowest at 30% health");
        Assert.AreEqual("coach would have pressed Q here: 2 enemy minions in Q range, the lowest at 30% health, with Q never seen on cooldown",
            policy.DrainKeys().Single().Sentence);
    }

    [TestMethod]
    public void A_button_that_is_up_with_an_enemy_in_view_is_a_question()
    {
        var (policy, jev) = Coach();
        Unread(policy, "W", "E", "R");
        policy.OnEvent(Cast("Q", 10, 5));
        Run(policy, 14.5, 14.9, Self(), Enemy(900));   // still on the printed cooldown
        Assert.IsEmpty(Offering(jev, "use_ability"));

        policy.OnFrame(Frame(15.5, Self(), Enemy(900)));

        var ask = Offering(jev, "use_ability").Single();
        Assert.IsFalse(ask.Questions.ContainsKey("ability"), "one button up, since W was last cast with its countdown unread: no choice to make");
        StringAssert.StartsWith(Criterion(ask, "use_ability"), "use_ability: throw one of the buttons that is up (Q)");
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
        jev.Script = Choose("use_ability");
        Unread(policy, "W", "E", "R");
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
        jev.Script = Choose("use_ability");
        Unread(policy, "W", "E", "R");
        policy.OnEvent(Cast("Q", 10, 5));
        policy.OnFrame(Frame(15.5, Self(), Enemy(900)));
        policy.OnFrame(Frame(16.0, Self(), Enemy(900)));

        var reminded = jev.Last.State.Coach.Single();
        Assert.AreEqual("pressed Q", reminded.Did);
        Assert.AreEqual(0.5, reminded.SecondsAgo);
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
    public void A_button_the_player_just_pressed_is_not_offered()
    {
        var (policy, jev) = Coach();
        Unread(policy, "E", "R");
        policy.OnEvent(Cast("Q", 10, 5));
        policy.OnEvent(Cast("W", 10, 5));
        policy.OnFrame(Frame(15.5, Self(), Enemy(900)));
        var ability = (ChoiceQuestion)jev.Last.Questions["ability"];
        CollectionAssert.AreEqual(new[] { "Q", "W" }, ability.Options.ToArray());
        StringAssert.Contains((string)ability.Criteria["Q"]!, "Q: Mystic Shot");
        StringAssert.Contains((string)ability.Criteria["Q"]!, "reaching 1150 units; inside its range: Karma");

        policy.OnEvent(Cast("Q", 15.6, 5));   // they pressed it: up again at 20.6
        policy.OnFrame(Frame(16, Self(), Enemy(900)));
        Assert.IsFalse(jev.Last.Questions.ContainsKey("ability"), "only W is up");
        StringAssert.StartsWith(Criterion(jev.Last, "use_ability"), "use_ability: throw one of the buttons that is up (W)");
        Assert.AreEqual("cooldown", jev.Last.State.Abilities.Single(a => a.Slot == "Q").Status);
    }

    [TestMethod]
    public void A_cast_whose_countdown_was_not_read_makes_the_button_unknown_again()
    {
        var (policy, jev) = Coach();
        Unread(policy, "W", "E", "R");
        policy.OnEvent(Cast("Q", 10, 5));
        policy.OnEvent(Cast("Q", 16, countdown: null));
        Run(policy, 16, 30, Self(), Enemy(900));
        Assert.IsEmpty(Offering(jev, "use_ability"), "just thrown, and not a button never seen cast");
    }

    [TestMethod]
    public void Resync_drops_the_answers_still_in_flight()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("use_ability");
        Unread(policy, "W", "E", "R");
        policy.OnEvent(Cast("Q", 10, 5));
        jev.Hold = true;
        policy.OnFrame(Frame(15.5, Self(), Enemy(900)));
        Assert.HasCount(1, jev.Asks);

        policy.Resync(Frame(15.6, Self(), Enemy(900)));
        jev.Release();
        policy.OnFrame(Frame(15.7, Self(), Enemy(900)));

        Assert.IsEmpty(policy.DrainKeys(), "an answer about a past we stopped trusting");
        Assert.IsEmpty(policy.DrainKeys());
        CollectionAssert.AreEqual(new[] { "E", "Q", "R", "W" }, ((ChoiceQuestion)jev.Last.Questions["ability"]).Options.ToArray(),
            "the casts were forgotten with the gap, so every button is one never seen cast");
    }

    [TestMethod]
    public void The_questions_on_the_wire_are_announced_as_they_go_and_come_back()
    {
        var (policy, jev) = Coach();
        List<string[]> heard = [];
        policy.AskingChanged += occasions => heard.Add(occasions.ToArray());
        policy.OnEvent(Cast("Q", 10, 5));

        jev.Hold = true;
        policy.OnFrame(Frame(15.5, Self(), Enemy(900)));
        CollectionAssert.AreEqual(new[] { "decide" }, heard[^1], "sent and not yet back");

        jev.Release();
        CollectionAssert.AreEqual(Array.Empty<string>(), heard[^1],
            "back as soon as the answer is, not when the next frame settles it");
        Assert.HasCount(2, heard);
    }

    [TestMethod]
    public void A_question_whose_answer_a_resync_will_drop_is_on_the_wire_until_it_is_back()
    {
        var (policy, jev) = Coach();
        List<string[]> heard = [];
        policy.AskingChanged += occasions => heard.Add(occasions.ToArray());
        policy.OnEvent(Cast("Q", 10, 5));
        jev.Hold = true;
        policy.OnFrame(Frame(15.5, Self(), Enemy(900)));

        policy.Resync(null);
        CollectionAssert.AreEqual(new[] { "decide" }, heard[^1]);
        jev.Release();
        CollectionAssert.AreEqual(Array.Empty<string>(), heard[^1]);
    }

    [TestMethod]
    public void A_question_that_fails_comes_off_the_wire_too()
    {
        var (policy, jev) = Coach();
        List<string[]> heard = [];
        policy.AskingChanged += occasions => heard.Add(occasions.ToArray());
        jev.Fault = new JevConnectionException("POST /v1/systemone failed: refused", "POST /v1/systemone");
        policy.OnEvent(Cast("Q", 10, 5));
        policy.OnFrame(Frame(15.5, Self(), Enemy(900)));

        CollectionAssert.AreEqual(new[] { "decide" }, heard[0]);
        CollectionAssert.AreEqual(Array.Empty<string>(), heard[^1]);
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
        Assert.IsTrue(cues[0].Failure, "a failure is marked as one, not as coaching");
        Assert.IsGreaterThan(1, jev.Asks.Count, "it kept asking");

        jev.Fault = null;
        jev.Script = Choose("use_ability");
        Run(policy, 17.1, 17.5, Self(), Enemy(900));
        Assert.IsNotEmpty(policy.DrainKeys(), "and took the next answer");
    }

    [TestMethod]
    public void Every_question_and_answer_reaches_the_audit()
    {
        var jev = new FakeJev();
        var audited = new List<Consultation>();
        var policy = new JevPolicy(jev, new JevOptions(), audited.Add);
        policy.Configure(Coaching);
        policy.OnEvent(Cast("Q", 10, 5));
        policy.OnFrame(Frame(15.5, Self(), Enemy(900)));

        var consultation = audited.Single();
        Assert.AreEqual("decide", consultation.Occasion);
        Assert.AreEqual(15.5, consultation.VideoTime);
        Assert.IsNotNull(consultation.Response);
        Assert.IsNull(consultation.Error);
        CollectionAssert.Contains(((ChoiceQuestion)consultation.Questions["decide"]).Options.ToArray(), "use_ability");
    }

    // --- Away from the action: heading to lane ---

    /// <summary>The player in their fountain, in the fixture's map frame.</summary>
    private static ChampionRow Idle(double x = 400, double y = 460) => Self(x: x, y: y);

    /// <summary>A frame with the game clock running, which the lane question needs.</summary>
    private static FrameEnvelope Clocked(double videoTime, int gameTime, params ChampionRow[] champions) =>
        new() { VideoTime = videoTime, GameTime = gameTime, Champions = champions };

    /// <summary>The fixture's bot lane, where the player laned for minutes.</summary>
    private static ChampionRow InBotLane(int track = 3) => Ally(track, 13064, 2051);

    [TestMethod]
    public void A_player_is_offered_the_walk_to_lane_whether_they_stand_or_walk()
    {
        var (policy, jev) = Coach();
        // Jittering by 40 units is standing still; the minimap read wobbles that much.
        var i = 0;
        for (var t = 100.0; t < 103.0; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 50, Idle(x: 400 + i++ % 2 * 40), InBotLane()));
        Assert.IsTrue(jev.Asks.All(a => Offered(a).Contains("walk_to_lane")), "no wait for them to stand still first");
        Assert.AreEqual(0.0, jev.Asks[0].State.Whereabouts!.StoodStillForSeconds);

        policy.OnFrame(Clocked(103.0, 53, Idle(), InBotLane()));

        var ask = jev.Last;
        Assert.AreEqual(103.0, ask.State.VideoTime);
        Assert.AreEqual("0:53", ask.State.GameClock);
        var where = ask.State.Whereabouts!;
        Assert.AreEqual("your fountain", where.Place);
        Assert.AreEqual(3.0, where.StoodStillForSeconds);
        CollectionAssert.AreEqual(new[] { "top", "mid", "bot" }, where.Lanes.Select(l => l.Lane).ToArray());
        var bot = where.Lanes.Single(l => l.Lane == "bot");
        Assert.AreEqual(2244, bot.DistanceUnits);
        Assert.AreEqual("up-right", bot.ScreenDirection);
        CollectionAssert.AreEqual(new[] { "champ3" }, bot.AlliesThere.ToArray());
        Assert.IsEmpty(where.Lanes.Single(l => l.Lane == "mid").AlliesThere);
        Assert.AreEqual("bot lane, their half, short of their outer turret", ask.State.Allies.Single().Place, "an ally is placed on the map too");
        var lane = (ChoiceQuestion)ask.Questions["lane"];
        CollectionAssert.AreEquivalent(new[] { "top", "mid", "bot" }, lane.Options.ToArray());
        StringAssert.Contains((string)lane.Criteria["bot"]!, "2244 units away, up-right on the screen; allies there: champ3");
        StringAssert.Contains((string)lane.Criteria["top"]!, "allies there: none");
    }

    [TestMethod]
    public void A_pick_is_a_step_toward_the_chosen_lane_and_the_next_comes_a_second_on()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("walk_to_lane", ("lane", "bot"));
        for (var t = 103.0; t <= 105.5 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 50, Idle()));

        // Decided every 0.3s of these frames; for a second after each step the
        // walk is not offered, and then it is again: the step still being
        // walked is no reason to hold the next.
        var steps = policy.DrainMoves();
        CollectionAssert.AreEqual(new[] { 103.0, 104.2, 105.4 }, steps.Select(s => s.VideoTime).ToArray());
        CollectionAssert.DoesNotContain(Offered(jev.Asks[1]), "walk_to_lane", "0.3s after a step");
        Assert.AreEqual("right", steps[0].Direction);
        Assert.IsGreaterThan(0, steps[0].Dx);
        Assert.IsLessThan(0, steps[0].Dy, "screen y grows down");
        Assert.AreEqual(1, Math.Round(double.Hypot(steps[0].Dx, steps[0].Dy), 6));
        Assert.AreEqual(2, steps[0].Priority);
        var (x, y) = RiftMap.Blue.At("bot", RiftMapTests.Progress(RiftMap.Blue, "bot", "outer"));
        var length = double.Hypot(x - 400, y - 460);
        Assert.IsTrue(steps[0].AttackMove, "a walk toward the lane stops to attack on the way");
        Assert.AreEqual(
            "coach would have attack-moved right toward bot lane, at your outer turret here: you are in your fountain at 0:50; "
            + $"a good player would be on the way up bot lane (bot lane, at your outer turret, {length:0} units right)",
            steps[0].Sentence);
        StringAssert.StartsWith(steps[2].Reason, "you have stood still for 2.4s in your fountain");
        // Aimed at the farthest turret standing, not the lane's nearest point
        // (its mouth just outside the base); the recording clicks a step that
        // way on the ground.
        Assert.AreEqual(new Destination("bot lane, at your outer turret", x, y), steps[0].Destination);
        Assert.AreEqual(length, steps[0].DistanceUnits!.Value, 1);
        var reminded = Offering(jev, "walk_to_lane")[^1].State.Coach;
        Assert.AreEqual("stepped toward bot lane", reminded[^1].Did);
        Assert.AreEqual(1.2, reminded[^1].SecondsAgo, "the step before, still being walked");
        Assert.IsEmpty(policy.DrainKeys());
        CollectionAssert.AreEqual(new[] { "you play from the blue side" }, policy.DrainCues().Select(c => c.Reason).ToArray());
    }

    [TestMethod]
    public void A_player_at_the_lanes_safe_spot_is_not_offered_the_walk()
    {
        var (policy, jev, heard) = Watched();
        jev.Script = Choose("walk_to_lane", ("lane", "bot"));
        for (var t = 200.0; t <= 203.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 150, Self(x: 12400, y: 1900)));

        var where = jev.Last.State.Whereabouts!;
        Assert.AreEqual("bot lane, your half, past your outer turret", where.Place);
        Assert.AreEqual("past it", where.Lanes.Single(l => l.Lane == "bot").YouAre);
        Assert.IsFalse(jev.Asks.Any(a => Offered(a).Contains("walk_to_lane")), "a pick would be no step, asked again and again");
        Assert.AreEqual("already past your outer turret in bot lane",
            heard[^1].Branches!.Single(b => b.Option == "walk_to_lane").Gate);
        Assert.IsEmpty(policy.DrainMoves());
    }

    [TestMethod]
    public void A_player_on_the_move_is_offered_the_walk_but_not_without_a_clock()
    {
        var (policy, jev) = Coach();
        // Walking out of base at 335 units a second: never on one spot, and
        // still offered the walk, since a walk is a click a second too.
        for (var t = 100.0; t <= 110.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 50, Idle(x: 400 + (t - 100) * 335)));
        Assert.HasCount(34, Offering(jev, "walk_to_lane"), "every 0.3s of these frames");
        Assert.IsTrue(jev.Asks.All(a => a.State.Whereabouts!.StoodStillForSeconds < 0.5), "the jitter radius holds a walker a fraction of a second at most");
        jev.Asks.Clear();

        // On one spot, but no game clock: the game has not begun.
        for (var t = 110.1; t <= 120.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Frame(t, Idle()));
        Assert.IsEmpty(Offering(jev, "walk_to_lane"));

        // The clock lands: the spot has been held since 110.1.
        policy.OnFrame(Clocked(120.3, 60, Idle()));
        Assert.AreEqual(10.2, Offering(jev, "walk_to_lane").Single().State.Whereabouts!.StoodStillForSeconds);
    }

    [TestMethod]
    public void The_spot_is_forgotten_at_a_resync()
    {
        var (policy, jev) = Coach();
        for (var t = 100.0; t <= 102.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 50, Idle()));
        var before = jev.Asks.Count;
        policy.Resync(Clocked(102.1, 52, Idle()));
        policy.OnFrame(Clocked(102.2, 52, Idle()));
        Assert.AreEqual(0.1, jev.Asks[before].State.Whereabouts!.StoodStillForSeconds,
            "still since the baseline, not since before the gap");
    }

    [TestMethod]
    public void Whereabouts_are_on_every_question()
    {
        var (policy, jev) = Coach(baseline: Clocked(218, 79, Self(x: 12400, y: 1900), Enemy(900)));
        policy.OnEvent(Event(HitWhileStill));
        var where = jev.Asks.Single().State.Whereabouts!;
        Assert.AreEqual("bot lane, your half, past your outer turret", where.Place);
        Assert.AreEqual(1.0, where.StoodStillForSeconds, "since the baseline at 218, asked about at 219");
    }

    // --- Minions: where the waves are, and standing in the enemy's ---

    private static MinionDot Dot(string team, double x, double y) => new() { Team = team, WorldX = x, WorldY = y };

    private static Minion Bar(string team, double x, double y, double? health = 0.8) =>
        new() { Team = team, WorldX = x, WorldY = y, Health = health };

    /// <summary>The waves meeting in bot lane beyond our outer turret, well out of its range.</summary>
    private static readonly MinionDot[] BotWavesMeeting =
    [
        Dot(MinionTeam.Blue, 10900, 1300), Dot(MinionTeam.Blue, 11100, 1350),
        Dot(MinionTeam.Red, 11650, 1480), Dot(MinionTeam.Red, 11800, 1560), Dot(MinionTeam.Red, 11750, 1500),
    ];

    /// <summary>An enemy wave crashing into our bot outer turret, with nothing of ours left in front of it.</summary>
    private static readonly MinionDot[] BotWaveAtOurTurret =
    [
        Dot(MinionTeam.Red, 10300, 1260), Dot(MinionTeam.Red, 10500, 1250), Dot(MinionTeam.Red, 10450, 1300),
    ];

    [TestMethod]
    public void The_enemy_front_is_placed_by_our_turrets()
    {
        Assert.AreEqual("in their half of the lane", RiftMap.Blue.EnemyFrontPlace("bot", 0.8));
        Assert.AreEqual("in your half of the lane, short of your outer turret", RiftMap.Blue.EnemyFrontPlace("bot", 0.45),
            "bot's outer turret stands far up the lane");
        Assert.AreEqual("at your outer turret", RiftMap.Blue.EnemyFrontPlace("bot", RiftMap.Blue.Along("bot", 10500, 1250).Progress));
        Assert.AreEqual("at your inner turret", RiftMap.Blue.EnemyFrontPlace("bot", RiftMap.Blue.Along("bot", 7000, 1480).Progress));
        Assert.AreEqual("at or past your inhibitor turret", RiftMap.Blue.EnemyFrontPlace("top", RiftMap.Blue.Along("top", 1253, 4400).Progress));
        Assert.IsFalse(RiftMap.Blue.AtOurTurret("mid", 0.5));
        Assert.IsTrue(RiftMap.Blue.AtOurTurret("mid", RiftMap.Blue.Along("mid", 5846, 6396).Progress));
    }

    [TestMethod]
    public void A_wave_at_a_fallen_turret_is_placed_on_the_way_to_the_next_one_in()
    {
        var atOuter = RiftMap.Blue.Along("bot", 10500, 1250).Progress;
        Assert.AreEqual("at your outer turret", RiftMap.Blue.EnemyFrontPlace("bot", atOuter, _ => true));
        Assert.AreEqual("past your fallen outer turret, on the way to your inner turret",
            RiftMap.Blue.EnemyFrontPlace("bot", atOuter, tier => tier != "outer"));
        Assert.AreEqual("past your fallen outer and inner turrets, on the way to your inhibitor turret",
            RiftMap.Blue.EnemyFrontPlace("bot", atOuter, tier => tier == "inhibitor"));
        Assert.AreEqual("past your fallen outer, inner and inhibitor turrets, on the way to your nexus",
            RiftMap.Blue.EnemyFrontPlace("bot", atOuter, _ => false));
        Assert.AreEqual("past your fallen outer turret, on the way to your inner turret (the minimap has not shown whether it stands)",
            RiftMap.Blue.EnemyFrontPlace("bot", atOuter, tier => tier == "outer" ? false : null));
        Assert.AreEqual("at your outer turret (the minimap has not shown whether it stands)",
            RiftMap.Blue.EnemyFrontPlace("bot", atOuter, _ => null), "not called is not fallen");
        Assert.AreEqual("at your inner turret", RiftMap.Blue.EnemyFrontPlace("bot", RiftMap.Blue.Along("bot", 7000, 1480).Progress,
            tier => tier != "outer"), "a turret the wave is already past is not named");
    }

    [TestMethod]
    public void A_player_away_from_a_wave_at_their_turret_is_offered_to_catch_it()
    {
        var (policy, jev) = Coach();
        // Walking through the river, not standing: a roaming player is who leaves a wave.
        var roaming = Self(x: 7000, y: 5000) with { MinionDots = BotWaveAtOurTurret };
        policy.OnFrame(Clocked(300, 400, roaming));

        var ask = Offering(jev, "walk_to_lane").Single();
        var bot = ask.State.Whereabouts!.Lanes.Single(l => l.Lane == "bot").Wave!;
        Assert.AreEqual("at your outer turret", bot.TheirFrontPlace);
        Assert.IsNull(bot.OurFront);
        Assert.AreEqual("down-right", bot.TheirFrontScreenDirection);
        Assert.IsGreaterThan(1500, bot.TheirFrontUnitsAway!.Value);
        var criteria = ((ChoiceQuestion)ask.Questions["lane"]).Criteria;
        StringAssert.Contains((string)criteria["bot"]!, "its enemy wave is at a turret of the player's");
        Assert.DoesNotContain("its enemy wave", (string)criteria["mid"]!);
    }

    [TestMethod]
    public void A_walk_to_the_crashing_lane_says_it_goes_to_catch_the_wave()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("walk_to_lane", ("lane", "bot"));
        policy.OnFrame(Clocked(300, 400, Self(x: 7000, y: 5000) with { MinionDots = BotWaveAtOurTurret }));

        var step = policy.DrainMoves().Single();
        // Short of the enemy's front, its dot nearest our base, as the facts round it.
        var front = Math.Round(RiftMap.Blue.Along("bot", 10300, 1260).Progress, 2);
        var (x, y) = RiftMap.Blue.At("bot", front - RiftMap.WalkBehindUnits / RiftMap.Length("bot"));
        Assert.AreEqual("bot lane, at your outer turret", step.Destination!.Name);
        Assert.IsTrue(step.AttackMove, "a walk to catch a wave stops to attack on the way");
        Assert.AreEqual(x, step.Destination.X, 1e-6);
        Assert.AreEqual(y, step.Destination.Y, 1e-6);
        StringAssert.StartsWith(step.Reason,
            "the enemy wave is at your outer turret in bot lane with none of your team there; a good player would be on the way to catch it (");
        policy.OnFrame(Clocked(303, 403, Self(x: 7300, y: 4800) with { MinionDots = BotWaveAtOurTurret }));
        Assert.AreEqual("stepped toward bot lane's wave at your turret", jev.Last.State.Coach.Single().Did);
    }

    [TestMethod]
    public void A_player_at_their_lane_is_still_offered_the_walk_while_another_lane_crashes()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("walk_to_lane", ("lane", "mid"));
        var dots = BotWaveAtOurTurret.Append(Dot(MinionTeam.Red, 5846, 6396)).ToArray();
        policy.OnFrame(Clocked(300, 400, Self(x: 1300, y: 12000) with { MinionDots = dots }, InBotLane()));

        var choice = (ChoiceQuestion)Offering(jev, "walk_to_lane").Single().Questions["lane"];
        StringAssert.Contains((string)choice.Criteria["bot"]!, "theirs at your outer turret");
        StringAssert.Contains((string)choice.Criteria["bot"]!, "its enemy wave is at a turret of the player's");
        StringAssert.Contains((string)choice.Criteria["bot"]!, "allies there: champ3");
        StringAssert.Contains((string)choice.Criteria["mid"]!, "its enemy wave is at a turret of the player's");
        Assert.DoesNotContain("its enemy wave", (string)choice.Criteria["top"]!);
        var step = policy.DrainMoves().Single();
        StringAssert.StartsWith(step.Reason, "the enemy wave is at your outer turret in mid lane with none of your team there; a good player would be on the way to catch it (");
    }

    [TestMethod]
    public void A_walk_to_lane_stops_short_of_an_enemy_wave_alone_at_their_turret()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("walk_to_lane", ("lane", "bot"));
        var self = Idle() with { MinionDots = BotWaveAtOurTurret };
        for (var t = 100.0; t <= 103.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 400, self));

        var front = Math.Round(RiftMap.Blue.Along("bot", 10300, 1260).Progress, 2);
        var (x, y) = RiftMap.Blue.At("bot", front - RiftMap.WalkBehindUnits / RiftMap.Length("bot"));
        var walk = policy.DrainMoves().First(m => m.Destination is not null);
        Assert.AreEqual(new Destination("bot lane, at your outer turret", x, y), walk.Destination, "not into it, not past it to the turret");
        StringAssert.Contains(walk.Reason, "on the way to catch it (short of the enemy minions in bot lane, at your outer turret, ");
    }

    /// <summary>All 22 turrets standing, but for the ones named: (team, lane, tier) to their reading.</summary>
    private static Turret[] Turrets(params (string Team, string Lane, string Tier, bool? Standing)[] except) =>
    [
        .. new[] { MinionTeam.Blue, MinionTeam.Red }.SelectMany(team =>
            RiftMap.Lanes.SelectMany(lane => RiftMap.Tiers.Select(tier => new Turret
            {
                Team = team, Lane = lane, Tier = tier,
                Standing = except.Where(e => e.Team == team && e.Lane == lane && e.Tier == tier)
                    .Select(e => e.Standing).DefaultIfEmpty(true).First(),
            }))
            .Concat(new[] { "top", "bot" }.Select(side =>
                new Turret { Team = team, Lane = "base", Tier = TurretTier.Nexus, Standing = true, Side = side }))),
    ];

    [TestMethod]
    public void A_wave_at_a_fallen_outer_turret_is_told_as_on_the_way_to_the_inner_one()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("walk_to_lane", ("lane", "bot"));
        var turrets = Turrets((MinionTeam.Blue, "bot", TurretTier.Outer, false), (MinionTeam.Red, "mid", TurretTier.Inner, null));
        policy.OnFrame(Clocked(300, 400, Self(x: 7000, y: 5000) with { MinionDots = BotWaveAtOurTurret, Turrets = turrets }));

        var lanes = jev.Last.State.Whereabouts!.Lanes;
        var bot = lanes.Single(l => l.Lane == "bot");
        Assert.AreEqual("past your fallen outer turret, on the way to your inner turret", bot.Wave!.TheirFrontPlace);
        Assert.AreEqual(new LaneTurretFacts("fallen", "standing", "standing"), bot.YourTurrets);
        Assert.AreEqual(new LaneTurretFacts("standing", "not seen", "standing"), lanes.Single(l => l.Lane == "mid").TheirTurrets);
        StringAssert.StartsWith(policy.DrainMoves().Single().Reason,
            "the enemy wave is past your fallen outer turret, on the way to your inner turret in bot lane");
    }

    [TestMethod]
    public void Turrets_are_remembered_across_frames_that_do_not_carry_them_and_forgotten_on_resync()
    {
        var (policy, jev) = Coach();
        var turrets = Turrets((MinionTeam.Blue, "bot", TurretTier.Outer, false));
        policy.OnFrame(Clocked(300, 400, Self(x: 7000, y: 5000) with { MinionDots = BotWaveAtOurTurret, Turrets = turrets }));
        policy.OnFrame(Clocked(303, 403, Self(x: 7000, y: 5000) with { MinionDots = BotWaveAtOurTurret }));
        Assert.AreEqual("fallen", jev.Last.State.Whereabouts!.Lanes.Single(l => l.Lane == "bot").YourTurrets!.Outer);

        policy.Resync(Clocked(310, 410, Self(x: 7000, y: 5000)));
        policy.OnFrame(Clocked(313, 413, Self(x: 7000, y: 5000) with { MinionDots = BotWaveAtOurTurret }));
        var bot = jev.Last.State.Whereabouts!.Lanes.Single(l => l.Lane == "bot");
        Assert.IsNull(bot.YourTurrets, "nothing read since the gap");
        Assert.AreEqual("at your outer turret", bot.Wave!.TheirFrontPlace, "placed by the spot alone");
    }

    [TestMethod]
    public void A_turret_falling_or_rebuilt_is_said_in_the_log()
    {
        var (policy, _) = Coach();
        policy.OnEvent(new GameEvent { Kind = EventKind.TurretDestroyed, Team = "blue", Lane = "bot", Tier = "outer", VideoTime = 512 });
        policy.OnEvent(new GameEvent { Kind = EventKind.TurretRebuilt, Team = "red", Lane = "base", Tier = "nexus", Side = "top", VideoTime = 800 });
        CollectionAssert.AreEqual(new[] { "your bot outer turret fell", "their top nexus turret stands again" },
            policy.DrainCues().Select(c => c.Reason).ToArray());
    }

    [TestMethod]
    public void No_wave_at_a_turret_or_a_player_already_at_it_is_no_wave_to_catch()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Clocked(300, 400, Self(x: 7000, y: 5000) with { MinionDots = BotWavesMeeting }));
        policy.OnFrame(Clocked(303, 403, Self(x: 10000, y: 1300) with { MinionDots = BotWaveAtOurTurret }));
        policy.OnFrame(Clocked(306, 406, Self(x: 7000, y: 5000, alive: false) with { MinionDots = BotWaveAtOurTurret }));
        policy.OnFrame(Clocked(309, 409, Self(x: 7000, y: 5000)));
        var walks = Offering(jev, "walk_to_lane");
        Assert.IsTrue(walks.All(a => a.State.VideoTime != 306), "not while dead");
        Assert.IsTrue(walks.SelectMany(a => ((ChoiceQuestion)a.Questions["lane"]).Criteria.Values)
            .All(c => !((string)c!).Contains("its enemy wave")));
    }

    [TestMethod]
    public void The_minimap_minions_are_placed_in_their_lanes_and_told_to_the_lane_question()
    {
        var (policy, jev) = Coach();
        var self = Idle() with { MinionDots = BotWavesMeeting };
        for (var t = 100.0; t <= 103.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 180, self));

        var lanes = jev.Last.State.Whereabouts!.Lanes;
        var bot = lanes.Single(l => l.Lane == "bot").Wave!;
        Assert.AreEqual((2, 3), (bot.OurMinions, bot.TheirMinions));
        Assert.AreEqual(Math.Round(RiftMap.Blue.Along("bot", 11100, 1350).Progress, 2), bot.OurFront, "our front is our foremost dot");
        Assert.AreEqual(Math.Round(RiftMap.Blue.Along("bot", 11650, 1480).Progress, 2), bot.TheirFront, "theirs is their foremost toward us");
        Assert.AreEqual("in your half of the lane, short of your outer turret", bot.TheirFrontPlace);
        Assert.AreEqual(Math.Round((bot.OurFront!.Value + bot.TheirFront!.Value) / 2, 2), bot.MeetAt);
        Assert.AreEqual("right", bot.MeetScreenDirection);
        var mid = lanes.Single(l => l.Lane == "mid").Wave!;
        Assert.AreEqual((0, 0), (mid.OurMinions, mid.TheirMinions), "looked and saw none");
        Assert.IsNull(mid.MeetAt);

        var criteria = ((ChoiceQuestion)jev.Last.Questions["lane"]).Criteria;
        StringAssert.Contains((string)criteria["bot"]!,
            $"minions on the minimap: 2 ours, 3 theirs, theirs in your half of the lane, short of your outer turret, "
            + $"meeting in bot lane, your half, past your outer turret ({bot.MeetAt:0.00} of the way to the enemy nexus)");
        StringAssert.Contains((string)criteria["mid"]!, "the minimap shows no minions in it");
    }

    [TestMethod]
    public void A_minimap_that_was_not_read_for_minions_is_no_wave_at_all()
    {
        var (policy, jev) = Coach();
        for (var t = 100.0; t <= 103.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 180, Idle()));
        Assert.IsTrue(jev.Last.State.Whereabouts!.Lanes.All(l => l.Wave is null));
        Assert.DoesNotContain("minion", (string)((ChoiceQuestion)jev.Last.Questions["lane"]).Criteria["bot"]!);

        // Dots the map calibration could not place are no reading either.
        var unplaced = Idle() with { MinionDots = [new MinionDot { Team = MinionTeam.Red, X = 200, Y = 190 }] };
        for (var t = 103.1; t <= 106.1 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 183, unplaced));
        Assert.IsTrue(jev.Last.State.Whereabouts!.Lanes.All(l => l.Wave is null));
    }

    [TestMethod]
    public void A_walk_goes_behind_our_minions_when_they_fight_beyond_the_turret()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("walk_to_lane", ("lane", "bot"));
        var self = Idle() with { MinionDots = BotWavesMeeting };
        for (var t = 100.0; t <= 103.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 180, self));

        var bot = jev.Asks[0].State.Whereabouts!.Lanes.Single(l => l.Lane == "bot");
        Assert.AreEqual("behind your minions", bot.WalkTo);
        Assert.AreEqual("short of it", bot.YouAre, "from the fountain");
        var (x, y) = RiftMap.Blue.At("bot", bot.Wave!.OurFront!.Value - RiftMap.WalkBehindUnits / RiftMap.Length("bot"));
        var step = policy.DrainMoves().First();
        Assert.AreEqual(new Destination("bot lane, at your outer turret", x, y), step.Destination);
        StringAssert.Contains(step.Reason, "a good player would be on the way up bot lane (behind your minions in bot lane, at your outer turret, ");
        StringAssert.Contains((string)((ChoiceQuestion)jev.Asks[0].Questions["lane"]).Criteria["bot"]!,
            "; the farthest it is safe to walk: behind your minions in bot lane, at your outer turret, ");
    }

    [TestMethod]
    public void A_player_left_at_the_turret_behind_a_pushed_wave_walks_up_behind_it()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("walk_to_lane", ("lane", "bot"));
        // Standing at their bot outer turret, their wave well up the lane beyond it.
        var outer = RiftMapTests.Progress(RiftMap.Blue, "bot", "outer");
        var (tx, ty) = RiftMap.Blue.At("bot", outer);
        var ours = RiftMap.Blue.At("bot", outer + 0.15);
        var theirs = RiftMap.Blue.At("bot", outer + 0.20);
        var self = Idle(tx, ty) with
        {
            MinionDots = [Dot(MinionTeam.Blue, ours.X, ours.Y), Dot(MinionTeam.Red, theirs.X, theirs.Y)],
        };
        for (var t = 100.0; t <= 103.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 180, self));

        var bot = jev.Asks[0].State.Whereabouts!.Lanes.Single(l => l.Lane == "bot");
        Assert.AreEqual("behind your minions", bot.WalkTo);
        Assert.AreEqual("short of it", bot.YouAre, "at the turret, with the wave pushed beyond it");
        CollectionAssert.Contains(Offered(jev.Asks[0]), "walk_to_lane");
        var step = policy.DrainMoves().First();
        Assert.AreEqual("bot lane, their half, short of their outer turret", step.Destination!.Name);
        Assert.IsTrue(step.AttackMove, "walking up to the wave stops to farm it on the way");
        StringAssert.Contains(step.Reason, "a good player would be on the way up bot lane (behind your minions in bot lane, their half, short of their outer turret, ");
    }

    [TestMethod]
    public void A_walk_goes_to_the_farthest_turret_standing_when_our_minions_are_short_of_it()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("walk_to_lane", ("lane", "bot"));
        var self = Idle() with { MinionDots = [Dot(MinionTeam.Blue, 8800, 1400)] };
        for (var t = 100.0; t <= 103.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 70, self));

        var (x, y) = RiftMap.Blue.At("bot", RiftMapTests.Progress(RiftMap.Blue, "bot", "outer"));
        Assert.AreEqual(new Destination("bot lane, at your outer turret", x, y), policy.DrainMoves().First().Destination);
        StringAssert.Contains(
            (string)((ChoiceQuestion)Offering(jev, "walk_to_lane")[^1].Questions["lane"]).Criteria["bot"]!, "1 ours, 0 theirs, ours pushed");
    }

    [TestMethod]
    public void A_player_in_lane_at_their_inhibitor_turret_is_still_short_of_it_and_walked_on()
    {
        // The VOD case: out of the base and into bot lane at the inhibitor
        // turret, the outer turret still standing far up the lane.
        var (policy, jev) = Coach();
        jev.Script = Choose("walk_to_lane", ("lane", "bot"));
        for (var t = 100.0; t <= 103.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 356, Self(x: 4400, y: 1300) with { Turrets = Turrets() }));

        var where = jev.Asks[0].State.Whereabouts!;
        Assert.AreEqual("bot lane, at your inhibitor turret", where.Place);
        var bot = where.Lanes.Single(l => l.Lane == "bot");
        Assert.AreEqual(("your outer turret", "short of it"), (bot.WalkTo, bot.YouAre));
        Assert.IsNull(where.Lanes.Single(l => l.Lane == "top").YouAre, "not in top lane, not in base");
        Assert.AreEqual("right", policy.DrainMoves().First().Direction);

        // With the outer turret fallen, the inner is as far as it is safe.
        var (policy2, jev2) = Coach();
        var fallen = Turrets((MinionTeam.Blue, "bot", TurretTier.Outer, false));
        policy2.OnFrame(Clocked(100, 356, Self(x: 4400, y: 1300) with { Turrets = fallen }));
        Assert.AreEqual("your inner turret", jev2.Last.State.Whereabouts!.Lanes.Single(l => l.Lane == "bot").WalkTo);
    }

    [TestMethod]
    public void A_player_at_or_past_the_safe_spot_is_not_walked()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("walk_to_lane", ("lane", "bot"));
        for (var t = 100.0; t <= 103.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 356, Self(x: 10504, y: 1300)));
        Assert.AreEqual("at it", jev.Last.State.Whereabouts!.Lanes.Single(l => l.Lane == "bot").YouAre);
        Assert.IsEmpty(policy.DrainMoves());
    }

    [TestMethod]
    public void A_player_seen_in_the_red_fountain_is_coached_from_the_red_side()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("walk_to_lane", ("lane", "bot"));
        for (var t = 100.0; t <= 103.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 50, Idle(x: 14300, y: 14400)));

        CollectionAssert.AreEqual(new[] { "you play from the red side" }, policy.DrainCues().Select(c => c.Reason).ToArray());
        var state = jev.Asks[0].State;
        StringAssert.Contains(state.Setting, "their own base is at the upper right");
        Assert.AreEqual("your fountain", state.Whereabouts!.Place);
        var bot = state.Whereabouts.Lanes.Single(l => l.Lane == "bot");
        Assert.AreEqual(("your outer turret", "short of it"), (bot.WalkTo, bot.YouAre));
        var step = policy.DrainMoves().First();
        var (x, y) = RiftMap.Red.At("bot", RiftMapTests.Progress(RiftMap.Red, "bot", "outer"));
        Assert.AreEqual(new Destination("bot lane, at your outer turret", x, y), step.Destination, "red's own outer turret, not blue's");
        Assert.AreEqual("down", step.Direction);
    }

    [TestMethod]
    public void The_red_side_is_kept_across_a_resync_and_our_turrets_are_reds()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("walk_to_lane", ("lane", "bot"));
        // An ally in the red fountain says the side while the player is out.
        policy.OnFrame(Clocked(90, 300, Self(x: 7000, y: 5000), Ally(3, 14300, 14400)));
        policy.Resync(Clocked(95, 305, Self(x: 13500, y: 10300)));
        policy.DrainMoves();

        // The feed's "blue" turrets are the player's own: here, red's.
        var fallen = Turrets((MinionTeam.Blue, "bot", TurretTier.Outer, false));
        for (var t = 100.0; t <= 103.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 310, Self(x: 13500, y: 10300) with { Turrets = fallen }));

        var where = jev.Asks[^1].State.Whereabouts!;
        Assert.AreEqual("bot lane, at your inhibitor turret", where.Place, "at red's bot inhibitor turret");
        var bot = where.Lanes.Single(l => l.Lane == "bot");
        Assert.AreEqual(("your inner turret", "short of it"), (bot.WalkTo, bot.YouAre));
        Assert.AreEqual(new LaneTurretFacts("fallen", "standing", "standing"), bot.YourTurrets);
        var step = policy.DrainMoves().First();
        var (x, y) = RiftMap.Red.At("bot", RiftMapTests.Progress(RiftMap.Red, "bot", "inner"));
        Assert.AreEqual(new Destination("bot lane, at your inner turret", x, y), step.Destination);
        Assert.AreEqual("down", step.Direction);
    }

    /// <summary>The player on bot lane's straight, east of our outer turret.</summary>
    private static ChampionRow InLane(params Minion[] minions) => Self(x: 9000, y: 1400) with { Minions = minions };

    [TestMethod]
    public void A_player_in_lane_with_enemy_minions_on_the_screen_is_offered_a_step_back()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Clocked(200, 300, InLane(
            Bar(MinionTeam.Blue, 8600, 1420), Bar(MinionTeam.Blue, 8700, 1400, health: null),
            Bar(MinionTeam.Red, 9300, 1400), Bar(MinionTeam.Red, 9400, 1380), Bar(MinionTeam.Red, 9700, 1400),
            new Minion { Team = MinionTeam.Red, X = 1200, Y = 640 })));

        var ask = Offering(jev, "step_back").Single();
        StringAssert.StartsWith(Criterion(ask, "step_back"), "step_back: one step back down the lane");
        var minions = ask.State.Minions!;
        Assert.AreEqual((2, 4), (minions.Ours, minions.Theirs), "an unplaced bar still counts");
        Assert.AreEqual(300, minions.NearestTheirsUnits);
        Assert.AreEqual(2, minions.TheirsWithinCasterRange, "the one 700 units off is out of a caster's reach");
        var length = RiftMap.Length("bot");
        var ahead = Math.Round((RiftMap.Blue.Along("bot", 9000, 1400).Progress - RiftMap.Blue.Along("bot", 8700, 1400).Progress) * length);
        Assert.AreEqual(ahead, minions.AheadOfOurFrontUnits);
        Assert.AreEqual(300, ahead, 5);
        Assert.AreEqual(0, ask.Options!.Retry!.MaxRetries, "a question about the moment is not retried");
    }

    [TestMethod]
    public void A_pick_steps_back_down_the_lane_toward_home()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("step_back");
        policy.OnFrame(Clocked(200, 300, InLane(Bar(MinionTeam.Blue, 8700, 1400), Bar(MinionTeam.Red, 9300, 1400))));
        policy.OnFrame(Clocked(200.1, 300, InLane(Bar(MinionTeam.Blue, 8700, 1400), Bar(MinionTeam.Red, 9300, 1400))));

        var step = policy.DrainMoves().Single();
        Assert.AreEqual(200.0, step.VideoTime);
        Assert.AreEqual("left", step.Direction, "bot's straight runs east from our base");
        Assert.IsNull(step.Destination, "a sidestep on the ground, not a walk");
        Assert.AreEqual(
            "coach would have stepped left here: an enemy minion is within a caster minion's reach of you "
            + "and you stand 299 units in front of your own minions; a good player stands behind their own minions' front",
            step.Sentence);

        policy.OnFrame(Clocked(202.0, 302, InLane(Bar(MinionTeam.Blue, 8700, 1400), Bar(MinionTeam.Red, 9300, 1400))));
        var reminded = jev.Last.State.Coach.Single();
        Assert.AreEqual(202.0, jev.Last.State.VideoTime);
        Assert.AreEqual("stepped back left, out of the enemy minions", reminded.Did);
        Assert.AreEqual(2.0, reminded.SecondsAgo);
    }

    [TestMethod]
    public void Among_enemy_minions_with_none_of_their_own_the_copy_says_so()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("step_back");
        policy.OnFrame(Clocked(200, 300, InLane(Bar(MinionTeam.Red, 9700, 1400))));
        policy.OnFrame(Clocked(200.1, 300, InLane(Bar(MinionTeam.Red, 9700, 1400))));

        Assert.IsNull(jev.Asks[0].State.Minions!.AheadOfOurFrontUnits);
        Assert.AreEqual(
            "the nearest enemy minion is 700 units away and none of your own minions is on the screen to take the hits; "
            + "a good player stands behind their own minions' front",
            policy.DrainMoves().Single().Reason);
    }

    [TestMethod]
    public void No_enemy_minion_off_lane_dead_or_bars_unread_offers_no_step_back()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Clocked(200, 300, InLane(Bar(MinionTeam.Blue, 8700, 1400))));
        policy.OnFrame(Clocked(203, 303, InLane()));
        policy.OnFrame(Clocked(206, 306, Self(x: 7000, y: 3000) with { Minions = [Bar(MinionTeam.Red, 7200, 3000)] }));
        policy.OnFrame(Clocked(209, 309, InLane(Bar(MinionTeam.Red, 9300, 1400)) with { Alive = false }));
        policy.OnFrame(Clocked(212, 312, Self(x: 9000, y: 1400)));
        Assert.IsEmpty(Offering(jev, "step_back"));
    }

    [TestMethod]
    public void A_movement_click_is_not_offered_again_for_a_second()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("step_back");
        var near = InLane(Bar(MinionTeam.Red, 9300, 1400));
        for (var t = 200.0; t <= 202.5 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 300, near));

        CollectionAssert.AreEqual(new[] { 200.0, 201.2, 202.4 }, Offering(jev, "step_back").Select(a => a.State.VideoTime).ToArray());
        CollectionAssert.AreEqual(new[] { 200.0, 201.2, 202.4 }, policy.DrainMoves().Select(m => m.VideoTime).ToArray());
        Assert.IsTrue(jev.Asks.Where(a => !Offered(a).Contains("step_back")).All(a => !Offered(a).Contains("walk_to_lane")),
            "no other step either");
    }

    [TestMethod]
    public void Minions_carried_on_an_allys_flapped_row_are_still_measured_from_the_player()
    {
        var (policy, jev) = Coach();
        // The player is established by the vote first; then the flag flaps.
        policy.OnFrame(Frame(190, Self(x: 9000, y: 1400)));
        var ally = Ally(3, 5000, 1300) with { IsSelf = true, Minions = [Bar(MinionTeam.Red, 9300, 1400)] };
        policy.OnFrame(Clocked(200, 300, Self(x: 9000, y: 1400) with { IsSelf = false }, ally));
        Assert.AreEqual(300, Offering(jev, "step_back").Single().State.Minions!.NearestTheirsUnits);
    }

    // --- Basic attacks: a last hit, or a trade ---

    /// <summary>The roots that offered either attack, on a minion or on a champion.</summary>
    private static FakeJev.Ask[] Attacks(FakeJev jev) =>
        jev.Asks.Where(a => Offered(a).Contains("attack_minion") || Offered(a).Contains("attack_champion")).ToArray();

    [TestMethod]
    public void Enemy_minions_in_reach_are_offered_as_targets_lowest_bar_first()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Clocked(200, 300, InLane(
            Bar(MinionTeam.Red, 9400, 1400, health: 0.7),
            Bar(MinionTeam.Red, 9300, 1400, health: 0.25),
            Bar(MinionTeam.Red, 9650, 1400, health: 0.1),      // 650: a step past Ezreal's 550
            Bar(MinionTeam.Red, 9800, 1400, health: 0.05),     // 800: out of reach
            Bar(MinionTeam.Red, 9200, 1400, health: null),     // a covered bar: nothing to judge
            Bar(MinionTeam.Blue, 8900, 1400, health: 0.1))));  // ours

        var ask = Attacks(jev).Single();
        var attack = ask.State.Attack!;
        Assert.AreEqual(550, attack.AttackRangeUnits);
        CollectionAssert.AreEqual(
            new[]
            {
                new MinionTarget("minion 1", 0.1, 650, "right", false),
                new MinionTarget("minion 2", 0.25, 300, "right", true),
                new MinionTarget("minion 3", 0.7, 400, "right", true),
            },
            attack.EnemyMinionsNear.ToArray());
        var criteria = ((ChoiceQuestion)ask.Questions["minion"]).Criteria;
        CollectionAssert.AreEqual(new[] { "minion 1", "minion 2", "minion 3" }, criteria.Keys.ToArray());
        Assert.AreEqual("minion 2: an enemy minion with 25% of its health bar left, 300 units right, inside your attack range",
            criteria["minion 2"]);
        StringAssert.Contains((string)criteria["minion 1"]!, "a step outside your attack range");
        Assert.AreEqual(0, ask.Options!.Retry!.MaxRetries, "a question about the moment is not retried");
    }

    [TestMethod]
    public void A_pick_right_clicks_the_chosen_target()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("attack_minion", ("minion", "minion 1"));
        var lane = InLane(Bar(MinionTeam.Red, 9300, 1700, health: 0.2), Bar(MinionTeam.Red, 9400, 1400, health: 0.9));
        policy.OnFrame(Clocked(200, 300, lane));
        policy.OnFrame(Clocked(200.1, 300, lane));

        var attack = policy.DrainMoves().Single(m => m.Target is not null);
        Assert.AreEqual(200.0, attack.VideoTime);
        Assert.AreEqual(new AttackTarget("the enemy minion at 20% health", 424), attack.Target);
        Assert.AreEqual("up-right", attack.Direction);
        Assert.AreEqual(Math.Sqrt(0.5), attack.Dx, 1e-3);
        Assert.AreEqual(-Math.Sqrt(0.5), attack.Dy, 1e-3, "world y grows north, screen y down");
        Assert.IsNull(attack.Destination);
        Assert.AreEqual(
            "coach would have attacked the enemy minion at 20% health up-right here: it is 424 units up-right with 20% "
            + "of its bar left, inside your attack range; a good player would attack it now",
            attack.Sentence);

        policy.OnFrame(Clocked(201.1, 301, lane));
        Assert.AreEqual("attacked the enemy minion at 20% health", Attacks(jev)[^1].State.Coach.Single().Did);
    }

    [TestMethod]
    public void A_picked_minion_is_clicked_where_its_bar_was_seen()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("attack_minion");
        var lane = InLane(Bar(MinionTeam.Red, 9300, 1700, health: 0.2) with { X = 1180, Y = 410 });
        policy.OnFrame(Clocked(200, 300, lane));
        policy.OnFrame(Clocked(200.1, 300, lane));

        var attack = policy.DrainMoves().Single(m => m.Target is not null);
        Assert.AreEqual((1180.0, 410.0), attack.ViewPx);
    }

    [TestMethod]
    public void A_lone_enemy_champion_in_reach_is_offered_without_a_choice()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("attack_champion");
        var karma = Enemy(500) with { Health = 0.4 };
        policy.OnFrame(Frame(10, Self(), karma));
        policy.OnFrame(Frame(10.1, Self(), karma));

        var ask = Attacks(jev)[0];
        Assert.IsFalse(ask.Questions.ContainsKey("champion"));
        CollectionAssert.DoesNotContain(Offered(ask), "attack_minion", "no minion bar read");
        Assert.IsTrue(ask.State.VisibleEnemies.Single().InAttackRange);
        Assert.IsEmpty(ask.State.Attack!.EnemyMinionsNear, "the bars were not read: no minion to offer");
        var attack = policy.DrainMoves().Single();
        Assert.AreEqual(new AttackTarget("Karma", 500), attack.Target);
        Assert.IsNull(attack.ViewPx, "a champion is placed only on the minimap");
        Assert.AreEqual("right", attack.Direction);
        Assert.AreEqual("Karma is 500 units right with 40% health, inside your attack range; a good player would attack them now",
            attack.Reason);
    }

    [TestMethod]
    public void A_minion_and_a_champion_in_reach_are_two_choices_each_with_its_own_rubric()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("attack_champion");
        var karma = Enemy() with { WorldX = 9400, WorldY = 1400, Health = 0.2 };
        policy.OnFrame(Clocked(200, 300, InLane(Bar(MinionTeam.Red, 9300, 1400, health: 0.3)), karma));

        var ask = Attacks(jev).Single();
        CollectionAssert.IsSubsetOf(new[] { "attack_minion", "attack_champion" }, Offered(ask));
        Assert.IsFalse(ask.Questions.ContainsKey("minion") || ask.Questions.ContainsKey("champion"), "one of each: no follow-up");
        StringAssert.StartsWith(Criterion(ask, "attack_minion"), "attack_minion: right-click an enemy minion");
        StringAssert.StartsWith(Criterion(ask, "attack_champion"), "attack_champion: right-click an enemy champion");
        Assert.AreEqual(new AttackTarget("Karma", 400), policy.DrainMoves().Single().Target);
    }

    [TestMethod]
    public void Nothing_in_reach_dead_or_unplaced_offers_no_attack()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Frame(10, Self(), Enemy(800)));                              // past range and a step
        policy.OnFrame(Frame(12, Self(), Enemy(500, visible: false)));              // in fog
        policy.OnFrame(Frame(14, Self(alive: false), Enemy(500)));
        policy.OnFrame(Frame(16, Self() with { WorldX = null }, Enemy(500)));
        policy.OnFrame(Clocked(18, 300, InLane(Bar(MinionTeam.Blue, 9100, 1400, health: 0.1))));
        Assert.IsEmpty(Attacks(jev));
    }

    [TestMethod]
    public void A_champion_whose_attack_range_is_not_on_file_is_still_asked_about_and_told_so()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Frame(10, Self() with { Champion = "Nidalee" }, Enemy(600)));
        var ask = Attacks(jev).Single();
        Assert.IsNull(ask.State.Attack!.AttackRangeUnits);
        Assert.IsNull(ask.State.VisibleEnemies.Single().InAttackRange);
    }

    [TestMethod]
    public void An_attack_is_not_offered_again_for_a_second()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("attack_minion");
        var near = InLane(Bar(MinionTeam.Red, 9300, 1400, health: 0.3));
        for (var t = 200.0; t <= 202.5 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 300, near));
        CollectionAssert.AreEqual(new[] { 200.0, 201.2, 202.4 }, Attacks(jev).Select(a => a.State.VideoTime).ToArray());
        CollectionAssert.AreEqual(new[] { 200.0, 201.2, 202.4 }, policy.DrainMoves().Select(m => m.VideoTime).ToArray());
    }

    [TestMethod]
    public void A_bar_followed_across_frames_tells_its_fall_to_the_attack_question()
    {
        var (policy, jev) = Coach(new JevOptions { AskEverySeconds = 0.5 });
        for (var i = 0; i <= 5; i++)
            policy.OnFrame(Clocked(200 + 0.1 * i, 300, InLane(Bar(MinionTeam.Red, 9300, 1400, health: 0.6 - 0.05 * i))));

        var ask = Attacks(jev)[^1];
        Assert.AreEqual(200.5, ask.State.VideoTime);
        var minion = ask.State.Attack!.EnemyMinionsNear.Single();
        Assert.AreEqual((0.5, 0.7), (minion.FallingPerSecond!.Value, minion.SecondsToEmpty!.Value), $"{minion}");
        Assert.IsNull(Attacks(jev)[0].State.Attack!.EnemyMinionsNear.Single().FallingPerSecond, "one reading is no rate");
    }

    // --- The farm: last hits taken and missed ---

    /// <summary>An enemy minion's death on the screen, its bar last seen at <paramref name="at"/>, reported 1.5 s later.</summary>
    private static GameEvent Died(string kind, double at) => new()
    {
        Kind = kind, VideoTime = at + 1.5, Team = "blue", Champion = "Ezreal", IsSelf = true, At = at, Health = 0.1,
    };

    [TestMethod]
    public void The_state_tells_the_farm_off_the_score_and_the_last_minutes_last_hits()
    {
        var (policy, jev) = Coach();
        policy.OnEvent(Died(EventKind.LastHit, 130));   // older than the minute counted
        policy.OnEvent(Died(EventKind.LastHit, 195));
        policy.OnEvent(Died(EventKind.MissedCs, 197));
        policy.OnFrame(Clocked(200, 300, Idle() with { Cs = 30 }));

        Assert.AreEqual(new FarmingFacts(30, 6.0, 1, 1, 3.0), jev.Last.State.Farming);
    }

    [TestMethod]
    public void The_farm_is_not_told_before_the_score_is_read_or_a_minion_dies_and_no_rate_before_one_thirty()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Clocked(100, 50, Idle()));
        Assert.IsNull(jev.Last.State.Farming);

        policy.OnFrame(Clocked(101, 80, Idle() with { Cs = 0 }));
        Assert.AreEqual(new FarmingFacts(0, null, 0, 0, null), jev.Last.State.Farming);
    }

    [TestMethod]
    public void A_missed_last_hit_is_put_down_to_what_the_coach_did_before_it()
    {
        var (policy, jev) = Coach();
        List<FarmOutcome> farmed = [];
        policy.Farmed += farmed.Add;
        var near = InLane(Bar(MinionTeam.Red, 9300, 1400, health: 0.3));
        void Frames(double from, ChampionRow self)
        {
            for (var t = from; t <= from + 1 + 1e-9; t = Math.Round(t + 0.1, 3))
                policy.OnFrame(Clocked(t, 300, self));
        }

        jev.Script = Choose("carry_on");
        Frames(200, near);
        policy.OnEvent(Died(EventKind.MissedCs, 201));
        jev.Script = Choose("attack_minion");
        Frames(210, near);
        policy.OnEvent(Died(EventKind.MissedCs, 211));
        Frames(220, InLane());
        policy.OnEvent(Died(EventKind.MissedCs, 221));
        policy.OnEvent(Died(EventKind.LastHit, 221));
        policy.OnEvent(Died(EventKind.MissedCs, 300));   // no root in its window

        CollectionAssert.AreEqual(
            new[]
            {
                "attack offered, picked carry_on", "coach attacked", "attack closed: no enemy minion in reach of a basic attack",
                "attack closed: no enemy minion in reach of a basic attack", "not asked",
            },
            farmed.Select(f => f.Why).ToArray());
        CollectionAssert.AreEqual(
            new[] { EventKind.MissedCs, EventKind.MissedCs, EventKind.MissedCs, EventKind.LastHit, EventKind.MissedCs },
            farmed.Select(f => f.Outcome).ToArray());
        StringAssert.EndsWith(policy.DrainActivity(),
            "; farm: 1 taken, 4 missed (attack closed: no enemy minion in reach of a basic attack 1, attack offered, picked carry_on 1, "
            + "coach attacked 1, not asked 1); last hits 1 of 5 this game");
        policy.OnFrame(Clocked(400, 300, Idle()));
        StringAssert.EndsWith(policy.DrainActivity(), "; last hits 1 of 5 this game", "the totals outlive a drain");
    }

    /// <summary>Near their bot outer turret (13866, 4505): the player outside its range, Karma inside it.</summary>
    private static ChampionRow ByTheirTurret() => Self(x: 13300, y: 3800);

    private static ChampionRow KarmaUnderTurret() => Enemy() with { WorldX = 13600, WorldY = 4200, Health = 0.3 };

    [TestMethod]
    public void An_enemy_under_their_turret_is_said_to_be()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Frame(10, ByTheirTurret(), KarmaUnderTurret()));
        var ask = Attacks(jev).Single();
        Assert.AreEqual("their bot outer turret, 405 units from it", ask.State.VisibleEnemies.Single().UnderTheirTurret);
        Assert.AreEqual("bot lane, at their outer turret", ask.State.VisibleEnemies.Single().Place);
        Assert.IsNull(ask.State.Attack!.YouUnderTheirTurret);
        Assert.IsNull(ask.State.Attack.YourMinionsUnderThatTurret);
    }

    [TestMethod]
    public void A_player_under_their_turret_is_told_so_with_their_own_minions_there()
    {
        var (policy, jev) = Coach();
        var self = Self(x: 13500, y: 4000) with
        {
            Minions = [Bar(MinionTeam.Blue, 13700, 4300), Bar(MinionTeam.Blue, 12000, 3000), Bar(MinionTeam.Red, 13600, 4200, health: 0.2)],
        };
        policy.OnFrame(Frame(10, self));
        var attack = Attacks(jev).Single().State.Attack!;
        Assert.AreEqual("their bot outer turret, 624 units from it", attack.YouUnderTheirTurret);
        Assert.AreEqual(1, attack.YourMinionsUnderThatTurret);
    }

    [TestMethod]
    public void A_turret_the_minimap_shows_fallen_covers_nothing()
    {
        var (policy, jev) = Coach();
        Turret[] turrets = RiftMap.Blue.TheirTurrets
            .Select(t => new Turret { Team = MinionTeam.Red, Lane = t.Lane, Tier = t.Tier, Side = t.NexusSide, Standing = !(t.Lane == "bot" && t.Tier == "outer") })
            .ToArray();
        policy.OnFrame(Frame(10, ByTheirTurret() with { Turrets = turrets }, KarmaUnderTurret()));
        Assert.IsNull(Attacks(jev).Single().State.VisibleEnemies.Single().UnderTheirTurret);
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
        Assert.IsNull(step.Destination, "a dodge is a sidestep on the ground, not a walk");
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

    // --- A skill point waiting ---

    private static GameEvent SkillPoint(double videoTime = 452.1, params string[] slots) => new()
    {
        Kind = EventKind.SkillPoint, VideoTime = videoTime, Team = "blue", Champion = "Ezreal", TrackId = 1,
        IsSelf = true, Slots = slots.Length == 0 ? ["Q", "W", "E"] : slots,
    };

    private static GameEvent SkillSpent(double videoTime, double? heldFor) => new()
    {
        Kind = EventKind.SkillSpent, VideoTime = videoTime, Team = "blue", Champion = "Ezreal", TrackId = 1,
        IsSelf = true, HeldFor = heldFor,
    };

    private static string[] Lit(FakeJev.Ask ask) => ((ChoiceQuestion)ask.Questions["slot"]).Options.ToArray();

    private static FakeJev.Ask[] Levels(FakeJev jev) => Offering(jev, "level_up");

    [TestMethod]
    public void A_waiting_point_is_offered_with_the_lit_buttons_to_choose_from()
    {
        var (policy, jev) = Coach(baseline: Clocked(450, 312, Self(level: 7), Enemy(900)));
        policy.OnEvent(Cast("Q", 440, 5));
        policy.OnEvent(SkillPoint(452.1, "Q", "W", "E"));
        Assert.IsEmpty(jev.Asks, "noted, and offered at the next moment decided");
        policy.OnFrame(Clocked(452.1, 312, Self(level: 7), Enemy(900)));

        var ask = Levels(jev).Single();
        StringAssert.StartsWith(Criterion(ask, "level_up"), "level_up: put the ability point waiting");
        var point = ask.State.SkillPoint!;
        Assert.AreEqual(7, point.Level, "the level is the self row's");
        CollectionAssert.AreEqual(new[] { "Q", "W", "E" }, point.Lit.ToArray());
        Assert.IsFalse(point.UltimateTakesAPoint);
        Assert.AreEqual(7, point.CoachWatchingSinceLevel);
        Assert.AreEqual(0, point.HeldForSeconds);
        Assert.AreEqual("5:12", ask.State.GameClock);
        var slot = (ChoiceQuestion)ask.Questions["slot"];
        CollectionAssert.AreEqual(new[] { "Q", "W", "E" }, slot.Options.ToArray());
        StringAssert.Contains((string)slot.Criteria["Q"]!, "Mystic Shot");
        StringAssert.Contains((string)slot.Criteria["Q"]!, "the ability this champion usually maxes first");
        StringAssert.Contains((string)slot.Criteria["Q"]!, "seen cast this game, so it holds a point already");
        StringAssert.Contains((string)slot.Criteria["Q"]!, "the coach has put no point in it since it began watching");
        StringAssert.Contains((string)slot.Criteria["W"]!, "usually maxes second");
        StringAssert.Contains((string)slot.Criteria["W"]!, "never seen cast this game, so it may hold no point yet");
        StringAssert.Contains((string)slot.Criteria["E"]!, "usually maxes last");
    }

    [TestMethod]
    public void Only_the_buttons_the_HUD_lights_are_offered()
    {
        // The HUD knows which abilities can take a point -- a full one does
        // not light, and the ultimate lights only at 6, 11 and 16 -- so the
        // options are the lit set and nothing is reconstructed in code.
        var (policy, jev) = Coach(baseline: Frame(385, Self(level: 6)));
        policy.OnEvent(SkillPoint(385.7, "W", "E", "R"));
        policy.OnFrame(Frame(385.7, Self(level: 6)));

        var ask = Levels(jev).Single();
        CollectionAssert.AreEqual(new[] { "W", "E", "R" }, Lit(ask));
        Assert.IsTrue(ask.State.SkillPoint!.UltimateTakesAPoint);
        var r = (string)((ChoiceQuestion)ask.Questions["slot"]).Criteria["R"]!;
        StringAssert.Contains(r, "Trueshot Barrage");
        StringAssert.Contains(r, "takes a point at levels 6, 11 and 16, and the HUD offers it now");

        // The feed's order is not trusted, and a string that is no slot is left out.
        policy.OnEvent(SkillPoint(400.0, "E", "Q", "X"));
        policy.OnFrame(Frame(400.0, Self(level: 6)));
        CollectionAssert.AreEqual(new[] { "Q", "E" }, Lit(jev.Last));
        Assert.IsFalse(jev.Last.State.SkillPoint!.UltimateTakesAPoint);
    }

    [TestMethod]
    public void A_pick_puts_the_point_in_the_chosen_ability_with_Ctrl_held()
    {
        var (policy, jev) = Coach(baseline: Clocked(450, 312, Self(level: 7)));
        jev.Script = Choose("level_up", ("slot", "Q"));
        policy.OnEvent(SkillPoint(452.1));
        policy.OnFrame(Clocked(452.1, 312, Self(level: 7)));

        var press = policy.DrainKeys().Single();
        Assert.AreEqual(452.1, press.VideoTime);
        Assert.AreEqual("Q", press.Key);
        Assert.IsTrue(press.WithControl);
        Assert.AreEqual("Ctrl+Q", press.Chord);
        Assert.AreEqual(2, press.Priority);
        Assert.AreEqual(
            "coach would have pressed Ctrl+Q here: you reached level 7 at 5:12; "
            + "a good player would put the point in Q (Mystic Shot: a skillshot poke)",
            press.Sentence);
        Assert.IsEmpty(policy.DrainMoves());
        Assert.IsEmpty(policy.DrainCues());

        policy.OnFrame(Clocked(452.6, 314, Self(level: 7)));
        Assert.HasCount(1, Levels(jev), "the chord is pressed: the point is not offered again");
        policy.OnEvent(SkillSpent(452.8, 0.7));
        policy.OnEvent(SkillPoint(473.2));
        policy.OnFrame(Frame(473.2, Self(level: 8)));
        var reminded = Levels(jev)[^1].State.Coach.Single();
        Assert.AreEqual("put the point in Q", reminded.Did);
        Assert.AreEqual(21.1, reminded.SecondsAgo);
    }

    [TestMethod]
    public void The_coachs_placement_is_counted_when_the_HUD_shows_the_point_gone_in()
    {
        var (policy, jev) = Coach(baseline: Frame(450, Self(level: 7)));
        jev.Script = Choose("level_up", ("slot", "Q"));
        void Point(double at)
        {
            policy.OnEvent(SkillPoint(at));
            policy.OnFrame(Frame(at, Self(level: 7)));
        }

        Point(452.1);
        Assert.HasCount(1, policy.DrainKeys());
        Point(473.2);
        StringAssert.Contains((string)((ChoiceQuestion)Levels(jev)[^1].Questions["slot"]).Criteria["Q"]!,
            "put no point in it", "pressed, but the HUD has not shown it go in");
        policy.OnEvent(SkillSpent(473.8, 0.6));
        var cue = policy.DrainCues().Single();
        Assert.AreEqual(473.8, cue.VideoTime);
        Assert.AreEqual(1, cue.Priority);
        Assert.AreEqual("the point went in after 0.6s; the coach's Ctrl+Q counted as its placement", cue.Reason);

        Point(500.0);
        Assert.AreEqual(7, Levels(jev)[^1].State.SkillPoint!.CoachWatchingSinceLevel);
        var slot = (ChoiceQuestion)Levels(jev)[^1].Questions["slot"];
        StringAssert.Contains((string)slot.Criteria["Q"]!, "the coach has put 1 point in it since it began watching");
        StringAssert.Contains((string)slot.Criteria["W"]!, "the coach has put no point in it since it began watching");
        policy.OnEvent(SkillSpent(500.5, 0.5));
        Point(520.0);
        StringAssert.Contains((string)((ChoiceQuestion)Levels(jev)[^1].Questions["slot"]).Criteria["Q"]!, "put 2 points in it");

        // A gap may be a new game: the count starts over, and says so.
        policy.Resync(Frame(600, Self(level: 10)));
        policy.OnEvent(SkillPoint(600.5));
        policy.OnFrame(Frame(600.5, Self(level: 10)));
        Assert.AreEqual(10, Levels(jev)[^1].State.SkillPoint!.CoachWatchingSinceLevel);
        StringAssert.Contains((string)((ChoiceQuestion)Levels(jev)[^1].Questions["slot"]).Criteria["Q"]!, "put no point in it");
    }

    [TestMethod]
    public void A_point_the_player_spends_themselves_is_in_nobodys_count()
    {
        var (policy, jev) = Coach(baseline: Frame(450, Self(level: 7)));
        jev.Script = (id, q) => id == "slot" ? FakeJev.Pick(q, "Q") : null;
        policy.OnEvent(SkillPoint(452.1));
        policy.OnFrame(Frame(452.1, Self(level: 7)));
        Assert.HasCount(1, Levels(jev));
        Assert.IsEmpty(policy.DrainKeys(), "carry_on at the root is no press even with an ability chosen");

        policy.OnEvent(SkillSpent(455.5, 3.4));
        var cue = policy.DrainCues().Single();
        Assert.AreEqual("the player put the point in themselves after 3.4s; it is in nobody's count", cue.Reason);
        Assert.IsEmpty(policy.DrainKeys());

        // Nothing is offered once it is gone.
        Run(policy, 455.6, 465, Self(level: 7, learnable: []));
        Assert.HasCount(1, Levels(jev));

        jev.Script = Choose("level_up", ("slot", "Q"));
        policy.OnEvent(SkillPoint(473.2));
        policy.OnFrame(Frame(473.2, Self(level: 8)));
        StringAssert.Contains((string)((ChoiceQuestion)Levels(jev)[^1].Questions["slot"]).Criteria["Q"]!, "put no point in it");

        // A spend whose arrival the feed never saw has no held time to say.
        policy.OnEvent(SkillSpent(474.0, null));
        Assert.AreEqual("the point went in; the coach's Ctrl+Q counted as its placement", policy.DrainCues().Single().Reason);
    }

    [TestMethod]
    public void A_point_spent_before_the_answer_lands_is_no_press()
    {
        var (policy, jev) = Coach(baseline: Frame(450, Self(level: 7)));
        jev.Script = Choose("level_up", ("slot", "Q"));
        jev.Hold = true;
        policy.OnEvent(SkillPoint(452.1));
        policy.OnFrame(Frame(452.1, Self(level: 7)));
        policy.OnEvent(SkillSpent(452.3, 0.2));
        jev.Release();
        policy.OnFrame(Frame(452.4, Self(level: 7)));

        Assert.IsEmpty(policy.DrainKeys(), "the player beat the coach to it");
        StringAssert.Contains(policy.DrainCues().Single().Reason, "the player put the point in themselves");
    }

    [TestMethod]
    public void A_new_set_for_a_held_point_is_a_new_point_and_the_same_set_again_is_not()
    {
        var (policy, jev) = Coach(baseline: Frame(300, Self(level: 5)));
        jev.Script = (id, q) => id switch
        {
            "decide" => FakeJev.Pick(q, "level_up"),
            "slot" => FakeJev.Pick(q, ((ChoiceQuestion)q).Options.Contains("R") ? "R" : "Q"),
            _ => null,
        };
        jev.Hold = true;
        policy.OnEvent(SkillPoint(300.5, "Q", "W", "E"));
        policy.OnFrame(Frame(300.5, Self(level: 5)));
        policy.OnEvent(SkillPoint(300.9, "Q", "W", "E"));
        Assert.HasCount(1, jev.Asks, "the set the coach already knows, announced again, and one in flight");

        // The ultimate lights at 6 under the point still held, before the answer lands.
        policy.OnFrame(Frame(385.6, Self(level: 6)));
        policy.OnEvent(SkillPoint(385.7, "Q", "W", "E", "R"));
        jev.Hold = false;
        jev.Release();
        policy.OnFrame(Frame(385.8, Self(level: 6)));

        var press = policy.DrainKeys().Single();
        Assert.AreEqual("R", press.Key, "the answer about the old set is dropped; the new set's stands");
        Assert.AreEqual(385.8, press.VideoTime);
        CollectionAssert.AreEqual(new[] { "Q", "W", "E", "R" }, Lit(jev.Last));
        var point = jev.Last.State.SkillPoint!;
        Assert.AreEqual(6, point.Level);
        Assert.IsTrue(point.UltimateTakesAPoint);
        Assert.AreEqual(85.3, point.HeldForSeconds, "held since the point first showed");
    }

    [TestMethod]
    public void A_held_point_is_offered_again_until_the_coach_spends_it()
    {
        // The first point of a game, at level one: the model says carry on,
        // and is asked again at every moment with how long the point has
        // waited, until it picks level_up. Which moment that is is its call;
        // here, half a minute in.
        var (policy, jev) = Coach(baseline: Frame(10, Self(level: 1)));
        jev.Script = (id, q) => id switch
        {
            "decide" => FakeJev.Pick(q, jev.Last.State.SkillPoint!.HeldForSeconds >= 30 ? "level_up" : "carry_on"),
            "slot" => FakeJev.Pick(q, "Q"),
            _ => null,
        };
        policy.OnEvent(SkillPoint(10.5, "Q", "W", "E"));
        Run(policy, 10.5, 45, Self(level: 1, learnable: ["Q", "W", "E"]));

        // Offered at 10.5 and every 0.3s of these frames; the pick came at 40.5, and nothing after.
        Assert.HasCount(101, Levels(jev));
        Assert.AreEqual(1, Levels(jev)[0].State.SkillPoint!.Level);
        Assert.AreEqual(0.3, Levels(jev)[1].State.SkillPoint!.HeldForSeconds);
        Assert.AreEqual(1, Levels(jev)[1].State.SkillPoint!.CoachWatchingSinceLevel);
        Assert.AreEqual(30, Levels(jev)[^1].State.SkillPoint!.HeldForSeconds);

        var press = policy.DrainKeys().Single();
        Assert.AreEqual(40.5, press.VideoTime);
        Assert.AreEqual("Ctrl+Q", press.Chord);
        Assert.AreEqual(
            "coach would have pressed Ctrl+Q here: you have held the point from level 1 for 30.0s; "
            + "a good player would put it in Q (Mystic Shot: a skillshot poke) by now",
            press.Sentence);

        policy.OnEvent(SkillSpent(41.2, 31.0));
        Assert.AreEqual("the point went in after 31.0s; the coach's Ctrl+Q counted as its placement", policy.DrainCues().Single().Reason);
        policy.OnEvent(SkillPoint(120.0, "Q", "W", "E"));
        policy.OnFrame(Frame(120.0, Self(level: 2)));
        StringAssert.Contains((string)((ChoiceQuestion)Levels(jev)[^1].Questions["slot"]).Criteria["Q"]!, "put 1 point in it");
    }

    [TestMethod]
    public void A_point_the_player_leaves_lit_is_shown_again_until_they_spend_it()
    {
        // The coach's chord never reaches the game: the VOD's chevrons stay
        // lit, so the point is still there to spend, and shown again every
        // PointAgainEverySeconds, not every moment.
        var (policy, jev) = Coach(baseline: Frame(450, Self(level: 7)));
        jev.Script = Choose("level_up", ("slot", "Q"));
        policy.OnEvent(SkillPoint(452.1));
        Run(policy, 452.1, 463, Self(level: 7, learnable: ["Q", "W", "E"]));

        var presses = policy.DrainKeys();
        CollectionAssert.AreEqual(new[] { 452.1, 457.1, 462.1 }, presses.Select(p => p.VideoTime).ToArray());
        Assert.IsTrue(presses.All(p => p.Chord == "Ctrl+Q"));
        StringAssert.StartsWith(presses[1].Sentence, "coach would have pressed Ctrl+Q here: you have held the point from level 7 for 5.0s");

        // Shown three times, but one point: one placement when it goes in.
        policy.OnEvent(SkillSpent(463.2, 11.1));
        Assert.AreEqual("the point went in after 11.1s; the coach's Ctrl+Q counted as its placement", policy.DrainCues().Single().Reason);
    }

    [TestMethod]
    public void A_level_gained_under_a_lit_point_is_another_point_to_put_in()
    {
        // The chevrons stay lit and the lit set is the same, so the feed says
        // nothing; the level rising under them is a second point.
        var (policy, jev) = Coach(baseline: Frame(100, Self(level: 2)));
        jev.Script = Choose("level_up", ("slot", "Q"));
        policy.OnEvent(SkillPoint(100.5, "Q", "W", "E"));
        policy.OnFrame(Frame(100.5, Self(level: 2, learnable: ["Q", "W", "E"])));
        Assert.HasCount(1, policy.DrainKeys());
        Assert.AreEqual(1, jev.Last.State.SkillPoint!.Waiting);

        policy.OnEvent(SkillPoint(101.0, "Q", "W", "E"));
        Run(policy, 101.0, 102.0, Self(level: 3, learnable: ["Q", "W", "E"]));
        var press = policy.DrainKeys().Single();
        Assert.AreEqual(101.0, press.VideoTime, "offered at once, not after the reminder's wait");
        Assert.AreEqual(2, Levels(jev)[^1].State.SkillPoint!.Waiting);
        StringAssert.Contains(press.Sentence, "you have 2 points waiting at level 3");

        // A row flapping a level down and back is not a third.
        Run(policy, 102.0, 103.0, Self(level: 2, learnable: ["Q", "W", "E"]));
        Run(policy, 103.0, 104.0, Self(level: 3, learnable: ["Q", "W", "E"]));
        Assert.IsEmpty(policy.DrainKeys());

        policy.OnEvent(SkillSpent(104.2, 3.7));
        Assert.AreEqual("the points went in after 3.7s; the coach's Ctrl+Q, Ctrl+Q counted as its placement", policy.DrainCues().Single().Reason);
    }

    [TestMethod]
    public void Lit_chevrons_the_feed_never_announced_are_offered_from_the_row()
    {
        var (policy, jev) = Coach(baseline: Frame(200, Self(level: 4)));
        Run(policy, 200.1, 201, Self(level: 4, learnable: ["Q", "W", "E"]));
        Assert.IsNotEmpty(Levels(jev));
        CollectionAssert.AreEqual(new[] { "Q", "W", "E" }, Lit(jev.Last));
    }

    [TestMethod]
    public void A_point_already_waiting_at_the_baseline_is_offered_from_the_frames()
    {
        // After a gap the feed does not announce a point it already showed;
        // the row still carries it.
        var (policy, jev) = Coach(baseline: Frame(100, Self(level: 1, learnable: ["Q", "W", "E"])));
        policy.OnFrame(Frame(100.1, Self(level: 1, learnable: ["Q", "W", "E"])));
        Assert.HasCount(1, Levels(jev), "first sight, off the row");
        CollectionAssert.AreEqual(new[] { "Q", "W", "E" }, Lit(jev.Last));

        policy.OnEvent(SkillPoint(100.3, "Q", "W", "E"));
        Run(policy, 100.4, 103.5, Self(level: 1, learnable: ["Q", "W", "E"]));
        Assert.AreEqual(3.4, Levels(jev)[^1].State.SkillPoint!.HeldForSeconds,
            "the same set, announced: the same point, held since the baseline");
    }

    [TestMethod]
    public void A_row_without_a_reading_neither_asks_nor_forgets()
    {
        // Dead, the reader is off and the key is absent; the point is still
        // waiting, and a point can be spent while dead.
        var (policy, jev) = Coach(baseline: Frame(200, Self(level: 4)));
        policy.OnEvent(SkillPoint(200.5, "Q", "W", "E"));
        Run(policy, 200.5, 203.5, Self(level: 4, alive: false));
        Assert.HasCount(11, Levels(jev));
        Assert.IsFalse(jev.Last.State.Player!.Alive);
        CollectionAssert.AreEqual(new[] { "carry_on", "level_up" }, Offered(jev.Last), "nothing else while dead");
        CollectionAssert.AreEqual(new[] { "Q", "W", "E" }, Lit(jev.Last), "the last set announced");
    }

    // --- Late answers ---

    [TestMethod]
    public void An_answer_that_arrives_later_lands_on_the_next_call_stamped_when_it_was_asked()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("use_ability");
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
    public void Self_is_the_flagged_row_when_the_name_it_was_known_by_is_lost()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Clocked(100, 180, Idle()));
        var asked = jev.Asks.Count;

        // The reader loses the name: the flagged row carries none from here on.
        var unnamed = Idle() with { Champion = null };
        for (var t = 100.5; t <= 103.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 180, unnamed));
        Assert.IsGreaterThan(asked, jev.Asks.Count, "the coach went silent when the player's name was lost");
        Assert.AreEqual("?", jev.Last.State.Player!.Champion);
    }

    [TestMethod]
    public void The_activity_line_tells_no_player_from_answers_of_no_order()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Clocked(100, 180));
        Assert.AreEqual("no player row in 1 frames; asked 0", policy.DrainActivity());

        jev.Script = Choose("carry_on");
        for (var t = 101.0; t <= 102.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 180, Idle()));
        Assert.AreEqual("player Ezreal; asked 4 (carry_on 4)", policy.DrainActivity());
        Assert.IsNull(policy.DrainActivity(), "drained");
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
        policy.Configure(new Meta { Schema = 2 });
        var cues = policy.DrainCues();
        Assert.HasCount(1, cues);
        StringAssert.Contains(cues[0].Reason, "--coach");
        StringAssert.Contains(cues[0].Reason, "no button will be pressed");
        Assert.IsEmpty(policy.DrainCues(), "said once");
        policy.Resync(null);
        Assert.IsEmpty(policy.DrainCues());
    }

    [TestMethod]
    public void A_feed_without_world_calibration_says_so_once()
    {
        var policy = new JevPolicy(new FakeJev());
        policy.Configure(Coaching with { WorldBounds = null });
        var cues = policy.DrainCues();
        Assert.HasCount(1, cues);
        StringAssert.Contains(cues[0].Reason, "world calibration (nobody will be walked to lane or into brush)");
        Assert.DoesNotContain("no button will be pressed", cues[0].Reason);
    }

    [TestMethod]
    public void A_feed_without_the_minion_readers_says_what_goes_unasked()
    {
        var policy = new JevPolicy(new FakeJev());
        policy.Configure(Coaching with { HasMinions = false, HasMinionDots = false });
        var reason = policy.DrainCues().Single().Reason;
        StringAssert.Contains(reason, "minion bars (nobody will be stepped back out of an enemy wave or shown a last hit)");
        StringAssert.Contains(reason, "minimap minions (walks go to where a lane is played, not to its wave");
        StringAssert.Contains(reason, "400px");
    }

    [TestMethod]
    public void A_feed_without_the_turret_reader_says_what_it_costs()
    {
        var policy = new JevPolicy(new FakeJev());
        policy.Configure(Coaching with { HasTurrets = false });
        StringAssert.Contains(policy.DrainCues().Single().Reason, "minimap turrets (a wave is placed by the turret spots");
    }

    [TestMethod]
    public void A_coaching_feed_gets_no_notice()
    {
        var policy = new JevPolicy(new FakeJev());
        policy.Configure(Coaching);
        Assert.IsEmpty(policy.DrainCues());
    }

    // --- Brush: out of sight ---

    private static FakeJev.Ask[] Hides(FakeJev jev) => Offering(jev, "hide_in_brush");

    /// <summary>
    /// In bot lane between our turrets, about 550 units above the lane's
    /// outer-edge patch at (7807, 804), the only one within reach.
    /// </summary>
    private static ChampionRow ByTheLaneBrush(params Minion[] minions) => Self(x: 7807, y: 1400) with { Minions = minions };

    [TestMethod]
    public void A_player_outside_the_brush_with_a_patch_near_is_offered_to_walk_in()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Clocked(200, 300, ByTheLaneBrush()));

        var ask = Hides(jev).Single();
        Assert.IsFalse(ask.Questions.ContainsKey("brush"), "one patch near: no choice to make");
        var brush = ask.State.Brush!;
        Assert.IsNull(brush.YouStandIn);
        var near = brush.Near.Single();
        Assert.AreEqual("brush 1", near.Name);
        Assert.AreEqual("the bot lane brush, between your inner and outer turrets", near.Kind);
        Assert.AreEqual("bot lane, between your inner and outer turrets", near.Place);
        Assert.AreEqual("down", near.ScreenDirection);
        Assert.IsLessThan(600, near.DistanceUnits);
        Assert.IsTrue(near.TowardYourBase);
        Assert.IsNull(near.AheadOfYourMinionsUnits, "no minion of theirs on the screen");
        Assert.AreEqual(0, ask.Options!.Retry!.MaxRetries, "a question about the moment is not retried");
    }

    [TestMethod]
    public void A_pick_steps_toward_the_patch_and_the_next_question_is_told()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("hide_in_brush");
        policy.OnFrame(Clocked(200, 300, ByTheLaneBrush(), Enemy(1500) with { WorldX = 8600, WorldY = 1500 }));
        policy.OnFrame(Clocked(200.1, 300, ByTheLaneBrush()));

        var walk = policy.DrainMoves().Single(m => m.Destination is not null);
        Assert.AreEqual(200.0, walk.VideoTime);
        Assert.AreEqual("the bot lane brush, between your inner and outer turrets", walk.Destination!.Name);
        Assert.AreEqual("the bot lane brush, between your inner and outer turrets", RiftBrush.At(walk.Destination.X, walk.Destination.Y)!.NameFrom(MapSide.Blue), "the click lands in the grass");
        Assert.AreEqual("down", walk.Direction);
        StringAssert.StartsWith(walk.Sentence, "coach would have stepped down toward the bot lane brush, between your inner and outer turrets here: the bot lane brush, between your inner and outer turrets is ");
        StringAssert.EndsWith(walk.Reason,
            " units down and Karma can see you out here; a good player would stand in the brush, where no enemy outside it can see them");

        policy.OnFrame(Clocked(203.1, 303, ByTheLaneBrush()));
        Assert.AreEqual("stepped toward the bot lane brush, between your inner and outer turrets", Hides(jev)[^1].State.Coach.Single().Did);
    }

    [TestMethod]
    public void Several_patches_near_are_a_choice_between_them()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("hide_in_brush", ("brush", "brush 2"));
        policy.OnFrame(Clocked(200, 300, Self(x: 12400, y: 2400)));

        var ask = Hides(jev).Single();
        var near = ask.State.Brush!.Near;
        Assert.IsGreaterThan(1, near.Count);
        var criteria = ((ChoiceQuestion)ask.Questions["brush"]).Criteria;
        CollectionAssert.AreEqual(near.Select(n => n.Name).ToArray(), criteria.Keys.ToArray());
        StringAssert.StartsWith((string)criteria["brush 1"]!, $"brush 1: {near[0].Kind}, {near[0].DistanceUnits:0} units ");
        var walk = policy.DrainMoves().Single();
        var second = RiftBrush.Near(12400, 2400, 1200).ElementAt(1).Patch;
        Assert.AreSame(second, RiftBrush.At(walk.Destination!.X, walk.Destination.Y));
    }

    [TestMethod]
    public void A_patch_in_front_of_their_own_minions_is_told_as_such()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Clocked(200, 300, ByTheLaneBrush(Bar(MinionTeam.Blue, 7000, 1450), Bar(MinionTeam.Red, 8000, 1400))));

        var near = Hides(jev).Single().State.Brush!.Near.Single();
        Assert.IsGreaterThan(0, near.AheadOfYourMinionsUnits!.Value, "the patch lies up the lane from their foremost minion");
        Assert.IsNotNull(near.NearestEnemyMinionUnits);
        Assert.AreEqual($"{near.AheadOfYourMinionsUnits:0} units in front of your minions", near.FaceCheck);
    }

    [TestMethod]
    public void Standing_in_brush_dead_unplaced_without_a_clock_or_far_from_any_offers_no_brush()
    {
        var (policy, jev) = Coach();
        var inside = RiftBrush.All.Single(p => p.NameFrom(MapSide.Blue) == "the bot lane brush, between your inner and outer turrets" && p.Y < 1000).Inside(7807, 1400);
        policy.OnFrame(Clocked(200, 300, Self(x: inside.X, y: inside.Y)));
        policy.OnFrame(Clocked(204, 304, Self(alive: false, x: 7807, y: 1400)));
        policy.OnFrame(Clocked(208, 308, Self(x: 7807, y: 1400) with { WorldX = null }));
        policy.OnFrame(Frame(212, Self(x: 7807, y: 1400)));
        policy.OnFrame(Clocked(216, 316, Self(x: 400, y: 460)));
        Assert.IsEmpty(Hides(jev));
    }

    [TestMethod]
    public void The_patch_they_stand_in_is_on_every_question()
    {
        var (policy, jev) = Coach();
        var inside = RiftBrush.All.Single(p => p.NameFrom(MapSide.Blue) == "the bot lane brush, between your inner and outer turrets" && p.Y < 1000).Inside(7807, 1400);
        policy.OnFrame(Clocked(200, 300, Self(x: inside.X, y: inside.Y), Enemy(500) with { WorldX = inside.X + 400, WorldY = inside.Y }));
        var ask = jev.Asks.First();
        Assert.AreEqual("the bot lane brush, between your inner and outer turrets", ask.State.Brush!.YouStandIn);
        CollectionAssert.DoesNotContain(ask.State.Brush.Near.Select(n => n.Kind).ToArray(), "the bot lane brush, between your inner and outer turrets",
            "the patch they stand in is not somewhere to walk to");
    }


    // --- Running back to cover ---

    /// <summary>The player 1000 units in front of their minion on bot's straight, Karma 1000 units further on.</summary>
    private static FrameEnvelope OutAhead(double videoTime, params ChampionRow[] others) =>
        Clocked(videoTime, 300, [
            InLane(Bar(MinionTeam.Blue, 8000, 1400)),
            Enemy() with { WorldX = 10000, WorldY = 1400 },
            .. others,
        ]);

    [TestMethod]
    public void Out_ahead_of_the_wave_with_an_enemy_near_the_refuges_are_offered_nearest_first()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(OutAhead(200, Ally(3, 6000, 1400)));

        var ask = Offering(jev, "run_away").Single();
        StringAssert.StartsWith(Criterion(ask, "run_away"), "run_away: a step back to cover");
        var retreat = ask.State.Cover!;
        Assert.AreEqual(0, retreat.AlliesNearerTheEnemy, "the ally is behind them");
        CollectionAssert.AreEqual(new[] { "your bot outer turret", "behind your minions", "champ3" },
            retreat.Refuges.Select(r => r.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "turret", "minions", "ally" }, retreat.Refuges.Select(r => r.Kind).ToArray());
        Assert.IsFalse(retreat.Refuges[0].TowardYourBase, "the outer turret lies up the lane from them");
        Assert.IsLessThan(1000, retreat.Refuges[0].NearestEnemyUnits!.Value, "and Karma stands nearer it than they do");
        Assert.IsTrue(retreat.Refuges[1].TowardYourBase);
        CollectionAssert.AreEqual(retreat.Refuges.Select(r => r.Name).ToArray(),
            ((ChoiceQuestion)ask.Questions["refuge"]).Options.ToArray(), "the follow-up offers the same refuges");
    }

    [TestMethod]
    public void A_pick_steps_toward_the_chosen_refuge()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("run_away", ("refuge", "behind your minions"));
        policy.OnFrame(OutAhead(200));
        policy.OnFrame(OutAhead(200.3));

        var step = policy.DrainMoves().Single();
        Assert.AreEqual(200.0, step.VideoTime);
        Assert.AreEqual("left", step.Direction, "back down bot's straight toward home");
        Assert.AreEqual("behind your minions in bot lane, at your inner turret", step.Destination!.Name);
        Assert.IsLessThan(8000, step.Destination.X, "behind the foremost minion, not on it");
        StringAssert.Contains(step.Reason, "Karma is 1000 units right and you stand");
        StringAssert.Contains(step.Reason, "a good player would run back to behind your minions");
        Assert.AreEqual("ran back toward behind your minions", jev.Last.State.Coach[^1].Did);
        CollectionAssert.DoesNotContain(Offered(jev.Last), "run_away", "a step 0.3s ago");
    }

    [TestMethod]
    public void With_no_enemy_on_the_screen_there_is_nothing_to_run_from()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Clocked(200, 300, InLane(Bar(MinionTeam.Blue, 8000, 1400))));
        Assert.IsEmpty(Offering(jev, "run_away"));
        var cover = jev.Last.State.Cover!;
        Assert.IsNull(cover.AlliesNearerTheEnemy, "nobody to be nearer to");
        Assert.AreEqual("behind your minions", cover.Refuges.Single(r => r.Kind == "minions").Name,
            "the cover is told either way; only the run needs an enemy");
        Assert.IsNull(cover.Refuges[0].NearestEnemyUnits);
    }

    // --- Recall ---

    [TestMethod]
    public void A_recall_pick_presses_B_and_holds_still_through_the_channel()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("recall");
        var hurt = Self(x: 9000, y: 1400) with { Health = 0.2 };
        for (var t = 200.0; t <= 209.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 300, hurt));

        var keys = policy.DrainKeys();
        CollectionAssert.AreEqual(new[] { 200.0, 208.0 }, keys.Select(k => k.VideoTime).ToArray(),
            "again only once the channel is done, and the player still stands there");
        Assert.AreEqual("B", keys[0].Key);
        Assert.AreEqual(
            "you are in bot lane, between your inner and outer turrets at 5:00 with 20% health, 80% mana, no turret or minions of yours by you; a good player would recall",
            keys[0].Reason);
        Assert.IsEmpty(policy.DrainMoves());
        Assert.IsTrue(jev.Asks.Skip(1).All(a => a.State.VideoTime >= 208.0),
            "nothing asked through the channel: every option would move them");
        Assert.AreEqual("pressed B to recall", jev.Asks[1].State.Coach[^1].Did);
    }

    [TestMethod]
    public void An_enemy_showing_up_ends_the_hold_on_the_channel()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("recall");
        var hurt = Self(x: 9000, y: 1400) with { Health = 0.2 };
        policy.OnFrame(Clocked(200, 300, hurt));
        policy.OnFrame(Clocked(203, 300, hurt, Enemy() with { WorldX = 10000, WorldY = 1400 }));

        Assert.HasCount(2, jev.Asks);
        CollectionAssert.Contains(Offered(jev.Last), "run_away");
        CollectionAssert.DoesNotContain(Offered(jev.Last), "recall", "the one pressed is still channelling, as far as the coach knows");
    }

    [TestMethod]
    public void The_cover_says_what_the_player_is_safe_by()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Clocked(200, 300, Self(x: 10300, y: 1300) with { Minions = [Bar(MinionTeam.Blue, 10800, 1300)] }));

        CollectionAssert.AreEqual(new[] { "your bot outer turret", "your minions" }, jev.Last.State.Cover!.YouAreBy.ToArray());
    }

    [TestMethod]
    public void Anywhere_in_their_turrets_range_is_under_it_and_nothing_to_run_back_from()
    {
        var (policy, jev, heard) = Watched();
        jev.Script = Choose("run_away");
        // 650 units out in front of the bot outer turret: inside its range,
        // though well past the spot a run back to it would aim at.
        var turret = RiftMap.Turrets.Single(t => t.Owner == MapSide.Blue && t.Lane == "bot" && t.Tier == TurretTier.Outer);
        for (var t = 200.0; t <= 203.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 300, Self(x: turret.X + 650, y: turret.Y) with { Health = 0.3 },
                Enemy() with { WorldX = turret.X + 1500, WorldY = turret.Y }));

        CollectionAssert.Contains(jev.Last.State.Cover!.YouAreBy.ToArray(), "your bot outer turret");
        Assert.IsFalse(jev.Asks.Any(a => Offered(a).Contains("run_away")), "a step back from under it is no retreat");
        Assert.AreEqual("already under your bot outer turret", heard[^1].Branches!.Single(b => b.Option == "run_away").Gate);
        Assert.IsEmpty(policy.DrainMoves());
    }

    // --- Said, not yet done ---

    [TestMethod]
    public void A_pick_the_hands_cannot_do_yet_is_said_and_not_said_again_for_a_while()
    {
        var (policy, jev) = Coach();
        jev.Script = Choose("buy");
        for (var t = 200.0; t <= 206.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 300, Idle()));

        var cues = policy.DrainCues().Where(c => c.Reason.StartsWith("coach would")).ToArray();   // not the side found in the fountain
        CollectionAssert.AreEqual(new[] { 200.0 }, cues.Select(c => c.VideoTime).ToArray(), "once a visit");
        Assert.AreEqual("coach would have bought here: you are in your fountain at 5:00; no enemy on the screen", cues[0].Reason);
        Assert.AreEqual(2, cues[0].Priority);
        Assert.IsFalse(cues[0].Failure);
        Assert.IsEmpty(policy.DrainKeys(), "no key for it yet");
        Assert.IsEmpty(policy.DrainMoves());
        CollectionAssert.DoesNotContain(Offered(jev.Asks[1]), "buy", "just said");
        Assert.AreEqual("said buy", jev.Last.State.Coach[^1].Did);
    }

    [TestMethod]
    public void Buying_is_offered_in_the_fountain_and_recalling_out_of_the_base()
    {
        var (policy, jev) = Coach();
        policy.OnFrame(Frame(10, Idle()));
        CollectionAssert.AreEqual(new[] { "carry_on", "buy" }, Offered(jev.Last), "shopping before the clock starts");
        policy.OnFrame(Clocked(20, 60, Self(x: 9000, y: 1400), Enemy(1500) with { WorldX = 10000, WorldY = 1400 }));
        CollectionAssert.IsSubsetOf(new[] { "run_away", "recall" }, Offered(jev.Last));
        CollectionAssert.DoesNotContain(Offered(jev.Last), "go_to_turret", "folded into run_away");
        CollectionAssert.DoesNotContain(Offered(jev.Last), "buy");
    }

    [TestMethod]
    public void Without_gold_in_the_feed_buying_is_said_once_a_visit_to_the_fountain()
    {
        // No has_gold: after one trip to the shop, asking again only tells a
        // player to buy what they cannot afford.
        var (policy, jev, heard) = Watched();
        jev.Script = Choose("buy");
        for (var t = 200.0; t <= 220.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 300, Idle()));
        Assert.AreEqual(1, policy.DrainCues().Count(c => c.Reason.StartsWith("coach would have bought")));
        Assert.AreEqual("already said this visit; no gold to tell what else is affordable",
            heard[^1].Branches!.Single(b => b.Option == "buy").Gate);

        policy.OnFrame(Clocked(221, 321, Self(x: 9000, y: 1400)));   // out to lane
        for (var t = 240.0; t <= 241.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 340, Idle()));                   // and home again
        Assert.AreEqual(1, policy.DrainCues().Count(c => c.Reason.StartsWith("coach would have bought")), "a new visit");
    }

    /// <summary>A coach on a feed whose gold reader runs, told its thoughts.</summary>
    private static (JevPolicy Policy, FakeJev Jev, List<Thought> Heard) WatchedWithGold()
    {
        var (policy, jev) = Coach(meta: Coaching with { HasGold = true });
        List<Thought> heard = [];
        policy.Thinking += heard.Add;
        return (policy, jev, heard);
    }

    private static string BuyGate(List<Thought> heard) => heard[^1].Branches!.Single(b => b.Option == "buy").Gate!;

    private static int Bought(JevPolicy policy) => policy.DrainCues().Count(c => c.Reason.StartsWith("coach would have bought"));

    /// <summary>The player standing in the fountain from <paramref name="from"/> to <paramref name="to"/>, with this gold on every frame (null: none read).</summary>
    private static void InFountain(JevPolicy policy, double from, double to, int? gold)
    {
        for (var t = from; t <= to + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 300, Idle() with { Gold = gold }));
    }

    [TestMethod]
    public void With_gold_buying_goes_on_being_offered_until_a_purchase_leaves_too_little()
    {
        var (policy, jev, heard) = WatchedWithGold();
        jev.Script = Choose("buy");
        InFountain(policy, 200, 206, gold: 1300);
        Assert.AreEqual(2, Bought(policy), "again after SayEverySeconds, not once a visit");

        InFountain(policy, 206.1, 220, gold: 250);   // spent: a drop is taken on its frame
        Assert.AreEqual(0, Bought(policy));
        Assert.AreEqual("250 gold buys nothing; the floor is 300", BuyGate(heard));
        Assert.IsFalse(Offered(jev.Last).Contains("buy"));
    }

    [TestMethod]
    public void With_gold_buying_is_offered_again_once_the_gold_climbs_over_the_floor()
    {
        var (policy, jev, _) = WatchedWithGold();
        jev.Script = Choose("buy");
        InFountain(policy, 200, 203, gold: 120);
        Assert.AreEqual(0, Bought(policy));

        InFountain(policy, 203.1, 204, gold: 300);
        var cues = policy.DrainCues().Where(c => c.Reason.StartsWith("coach would have bought")).ToArray();
        Assert.HasCount(1, cues);
        Assert.IsGreaterThanOrEqualTo(203.1, cues[0].VideoTime, "on the reading that covers the floor");
    }

    [TestMethod]
    public void A_frame_without_gold_holds_the_last_decision_either_way()
    {
        var (policy, jev, heard) = WatchedWithGold();
        jev.Script = Choose("buy");
        InFountain(policy, 200, 201, gold: null);
        Assert.AreEqual(0, Bought(policy), "nothing read yet is not enough gold");
        Assert.AreEqual("gold not read yet", BuyGate(heard));

        InFountain(policy, 201.1, 201.1, gold: 250);
        InFountain(policy, 201.2, 210, gold: null);
        Assert.AreEqual(0, Bought(policy), "an unread box does not open it");
        Assert.AreEqual("250 gold buys nothing; the floor is 300", BuyGate(heard));

        InFountain(policy, 210.1, 210.1, gold: 900);
        InFountain(policy, 210.2, 216, gold: null);
        Assert.AreEqual(2, Bought(policy), "and does not close it: read as zero it would stop after one");
    }

    [TestMethod]
    public void With_gold_a_new_game_forgets_the_last_reading()
    {
        var (policy, jev, heard) = WatchedWithGold();
        jev.Script = Choose("buy");
        InFountain(policy, 200, 200, gold: 900);
        Assert.AreEqual(1, Bought(policy));
        policy.NewGame(1);
        InFountain(policy, 300, 301, gold: null);
        Assert.AreEqual(0, Bought(policy));
        Assert.AreEqual("gold not read yet", BuyGate(heard));
    }

    // --- The brain view: each question told as a thought ---

    private static (JevPolicy Policy, FakeJev Jev, List<Thought> Heard) Watched(FrameEnvelope? baseline = null)
    {
        var (policy, jev) = Coach(baseline: baseline);
        List<Thought> heard = [];
        policy.Thinking += heard.Add;
        return (policy, jev, heard);
    }

    [TestMethod]
    public void A_root_is_told_as_asked_with_every_branch_and_what_closed_the_rest()
    {
        var (policy, jev, heard) = Watched();
        jev.Hold = true;
        policy.OnFrame(Clocked(100, 50, Idle()));

        var asked = heard.Single();
        Assert.AreEqual("asked", asked.Phase);
        Assert.AreEqual("decide", asked.Occasion);
        Assert.AreEqual(100, asked.VideoTime);
        CollectionAssert.AreEqual(
            new[]
            {
                "carry_on", "level_up", "run_away", "use_ability", "attack_minion", "attack_champion", "step_back",
                "hide_in_brush", "recall", "buy", "walk_to_lane",
            },
            asked.Branches!.Select(b => b.Option).ToArray(), "every branch, in the rubric's order");
        CollectionAssert.AreEqual(Offered(jev.Last), asked.Branches!.Where(b => b.Gate is null).Select(b => b.Option).ToArray(),
            "the ungated branches are exactly the root's options");
        Assert.AreEqual("no enemy on the screen", asked.Branches!.Single(b => b.Option == "run_away").Gate);
        Assert.AreEqual("already in your fountain", asked.Branches!.Single(b => b.Option == "recall").Gate);
        Assert.AreEqual("lane", asked.Branches!.Single(b => b.Option == "walk_to_lane").FollowUp);
        CollectionAssert.AreEqual(jev.Last.Questions.Keys.ToArray(), asked.Questions!.Select(q => q.Id).ToArray());
        Assert.AreSame(jev.Last.State, asked.State, "the state the model was shown");
        Assert.AreEqual(0.4, asked.DecideAt);
    }

    [TestMethod]
    public void An_answer_is_told_under_the_same_id_with_what_the_hands_did()
    {
        var (policy, jev, heard) = Watched();
        jev.Script = Choose("walk_to_lane", ("lane", "bot"));
        policy.OnFrame(Clocked(103, 50, Idle()));

        Assert.HasCount(2, heard);
        var answered = heard[1];
        Assert.AreEqual("answered", answered.Phase);
        Assert.AreEqual(heard[0].Id, answered.Id);
        Assert.AreEqual("jev-test", answered.Model);
        Assert.AreEqual("acted", answered.Verdict);
        Assert.AreEqual("walk_to_lane", answered.Answers!["decide"].Choice);
        Assert.AreEqual(1.0, answered.Answers["decide"].Probabilities!["walk_to_lane"]);
        Assert.AreEqual("bot", answered.Answers["lane"].Choice);
        var deed = answered.Did!.Single();
        Assert.AreEqual("attack_move", deed.Hand);
        Assert.AreEqual("bot lane, at your outer turret", deed.Toward);
        Assert.AreEqual(policy.DrainMoves().Single().Sentence, deed.Said);

        policy.OnFrame(Clocked(103.3, 50, Idle()));
        Assert.AreEqual("stepped 0.3s ago; a step a second",
            heard[2].Branches!.Single(b => b.Option == "walk_to_lane").Gate, "the pace of steps is a gate, and says so");
    }

    [TestMethod]
    public void A_pick_that_is_no_order_is_told_as_carry_on_or_weak()
    {
        var (policy, jev, heard) = Watched();
        policy.OnFrame(Clocked(100, 50, Idle()));
        Assert.AreEqual("carry_on", heard.Last().Verdict, "the silent model picks carry_on");
        Assert.IsNull(heard.Last().Did);

        jev.Script = (id, _) => id == "decide"
            ? new ChoiceAnswer("buy", new Dictionary<string, double> { ["carry_on"] = 0.3, ["buy"] = 0.35, ["walk_to_lane"] = 0.35 }, 0.5)
            : null;
        policy.OnFrame(Clocked(100.3, 50, Idle()));
        Assert.AreEqual("weak", heard.Last().Verdict, "0.35 is under DecideAt");
        Assert.IsNull(heard.Last().Did);
    }

    [TestMethod]
    public void An_answer_after_a_resync_is_told_as_stale()
    {
        var (policy, jev, heard) = Watched();
        jev.Script = Choose("walk_to_lane", ("lane", "bot"));
        jev.Hold = true;
        policy.OnFrame(Clocked(100, 50, Idle()));
        policy.Resync(Clocked(100.1, 50, Idle()));
        jev.Release();
        policy.OnFrame(Clocked(100.2, 50, Idle()));

        Assert.AreEqual("stale", heard.Single(t => t.Phase == "answered").Verdict);
        Assert.IsEmpty(policy.DrainMoves());
    }

    [TestMethod]
    public void A_root_with_nothing_to_offer_is_told_as_idle_no_faster_than_a_root_is_asked()
    {
        var (policy, jev, heard) = Watched();
        Run(policy, 100, 101, Self(alive: false, x: 400, y: 460));

        Assert.IsEmpty(jev.Asks, "nothing to offer, so nothing asked");
        CollectionAssert.AreEqual(new[] { 100, 100.3, 100.6, 100.9 }, heard.Select(t => t.VideoTime).ToArray());
        Assert.IsTrue(heard.All(t => t.Phase == "idle"));
        Assert.AreEqual("dead", heard[0].Branches!.Single(b => b.Option == "walk_to_lane").Gate);
        Assert.AreEqual("no skill point waiting", heard[0].Branches!.Single(b => b.Option == "level_up").Gate);
    }

    [TestMethod]
    public void A_bolt_is_told_with_its_yes_and_no_answers_and_the_step_it_took()
    {
        var (policy, jev, heard) = Watched(baseline: Clocked(218, 79, Self(x: 12400, y: 1900), Enemy(900)));
        jev.Script = (id, _) => id is "remark" or "step" ? FakeJev.Yes : null;
        policy.OnEvent(Event(HitWhileStill));

        var answered = heard.Single(t => t.Occasion == "bolt" && t.Phase == "answered");
        Assert.AreEqual(0.6, answered.YesAt);
        Assert.AreEqual(0.95, answered.Answers!["step"].Yes);
        Assert.IsNotNull(answered.Answers["side"].Choice);
        Assert.AreEqual("acted", answered.Verdict);
        CollectionAssert.AreEquivalent(new[] { "voice", "move" }, answered.Did!.Select(d => d.Hand).ToArray());
        Assert.IsNull(answered.Branches, "an event has no root branches");
    }

    // --- One run, several games ---

    [TestMethod]
    public void A_new_game_forgets_the_player_the_side_the_turrets_and_the_farm()
    {
        var (policy, jev) = Coach();
        // Game 0: Ezreal on the red side, an outer turret down, a minute of farm.
        var fallen = Turrets((MinionTeam.Blue, "bot", TurretTier.Outer, false));
        for (var t = 100.0; t <= 103.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 900, Self(x: 7000, y: 5000) with { Cs = 40, Turrets = fallen },
                Ally(3, 14300, 14400)));
        policy.OnEvent(Died(EventKind.LastHit, 101));
        policy.OnEvent(Died(EventKind.MissedCs, 102));
        Assert.AreEqual(Moment.SettingFrom(MapSide.Red), jev.Last.State.Setting);
        Assert.AreEqual("Ezreal", jev.Last.State.Player!.Champion);
        StringAssert.EndsWith(policy.DrainActivity(), "last hits 1 of 2 this game");
        policy.DrainCues();

        // Between games the clock is gone; the player's row lingers a moment.
        policy.OnFrame(new FrameEnvelope { VideoTime = 110, Champions = [Self(x: 7000, y: 5000)] });
        policy.OnFrame(new FrameEnvelope { VideoTime = 115 });

        // Game 1: another champion on another track, on the blue side, the
        // clock back near zero. Twenty-odd seconds on, so game 0's last hits
        // would still be inside the farm's minute had they been kept.
        policy.NewGame(1);
        StringAssert.StartsWith(policy.DrainCues().Single().Reason, "game 1 began");
        var ahri = Idle() with { TrackId = 11, Champion = "Ahri", Cs = 0 };
        policy.Resync(Clocked(125, 5, ahri));
        var before = jev.Asks.Count;
        for (var t = 125.1; t <= 127.0 + 1e-9; t = Math.Round(t + 0.1, 3))
            policy.OnFrame(Clocked(t, 5 + (int)(t - 125), ahri));

        Assert.IsGreaterThan(before, jev.Asks.Count, "the new game's player is coached at once, not after out-voting the last one's");
        var state = jev.Last.State;
        Assert.AreEqual("Ahri", state.Player!.Champion);
        Assert.AreEqual(Moment.SettingFrom(MapSide.Blue), state.Setting);
        StringAssert.StartsWith(state.GameClock, "0:0", "the clock back near zero");
        Assert.AreEqual(new FarmingFacts(0, null, 0, 0, null), state.Farming, "no score rate before 1:30, no last game's last hits");
        var bot = state.Whereabouts!.Lanes.Single(l => l.Lane == "bot");
        Assert.AreNotEqual("fallen", bot.YourTurrets?.Outer, "last game's fallen turret is not this one's");
        CollectionAssert.AreEqual(new[] { "you play from the blue side" }, policy.DrainCues().Select(c => c.Reason).ToArray(),
            "the side is said again for the new game");
        Assert.DoesNotContain("last hits", policy.DrainActivity()!, "the farm's totals are this game's");
    }

    [TestMethod]
    public void A_new_game_drops_the_answers_still_in_flight()
    {
        var (policy, jev) = Coach();
        jev.Hold = true;
        jev.Script = Choose("walk_to_lane", ("lane", "bot"));
        policy.OnFrame(Clocked(100, 50, Idle()));
        Assert.AreEqual(1, jev.Pending);

        policy.NewGame(1);
        jev.Release();
        policy.OnFrame(Clocked(101, 3, Idle()));

        Assert.IsEmpty(policy.DrainMoves(), "an answer about the last game is not acted on in this one");
    }
}
