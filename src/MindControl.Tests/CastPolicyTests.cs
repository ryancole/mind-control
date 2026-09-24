using MindControl.Feed;
using MindControl.Policy;

namespace MindControl.Tests;

/// <summary>
/// When the coach presses a key and, mostly, when it does not. What is pinned
/// is the decision: an ability known to be up with a visible enemy in range for
/// the hold is pressed; before the slot's first cast, on cooldown, after an
/// untimed cast, out of range, in fog, out of mana, dead, and for a champion
/// with no kit on file, nothing is. Plus the hold measured from whichever came
/// later (arrival or cooldown end), the repeat window, the reset on resync,
/// that nothing here moves the cursor, and that a feed without the ability
/// stage says so once.
/// </summary>
[TestClass]
public sealed class CastPolicyTests
{
    private static readonly Meta Coaching = new()
    {
        Schema = 1, HasAbilities = true,
        WorldBounds = new WorldBounds { MinX = 0, MinY = 0, MaxX = 14800, MaxY = 14800 },
    };

    private static CastPolicy Policy(CastOptions? options = null, Meta? meta = null)
    {
        var policy = new CastPolicy(options ?? new CastOptions { SelfChampion = "Ezreal" });
        policy.Configure(meta ?? Coaching);
        policy.DrainCues();
        return policy;
    }

    private static ChampionRow Self(double? resource = 0.8, bool? alive = true, string champion = "Ezreal") => new()
    {
        TrackId = 1, Team = "blue", Champion = champion, IsSelf = true, Visible = true,
        WorldX = 5000, WorldY = 5000, Resource = resource, Alive = alive,
    };

    /// <summary>An enemy <paramref name="units"/> due east of self.</summary>
    private static ChampionRow Enemy(double units, bool visible = true, string champion = "Karma") => new()
    {
        TrackId = 7, Team = "red", Champion = champion, Visible = visible,
        WorldX = 5000 + units, WorldY = 5000,
    };

    private static FrameEnvelope Frame(double videoTime, params ChampionRow[] champions) =>
        new() { VideoTime = videoTime, Champions = champions };

    private static GameEvent Cast(string slot, double at, int? countdown) => new()
    {
        Kind = EventKind.Ability, VideoTime = at + 0.1, Team = "blue", Champion = "Ezreal",
        Slot = slot, At = at, Countdown = countdown, Confirmed = true,
    };

    /// <summary>Q cast at 10 with a 5s countdown: known up from 15.5 (the half-second margin).</summary>
    private static CastPolicy AfterQ(CastPolicy? policy = null)
    {
        policy ??= Policy();
        Assert.IsNull(policy.OnEvent(Cast("Q", 10, 5)), "cast coaching never moves the cursor");
        return policy;
    }

    private static IReadOnlyList<KeyPress> Show(CastPolicy policy, FrameEnvelope frame)
    {
        Assert.IsNull(policy.OnFrame(frame), "cast coaching never moves the cursor");
        return policy.DrainKeys();
    }

    /// <summary>Feed frames every 100 ms from <paramref name="from"/> to <paramref name="to"/>, collecting the presses.</summary>
    private static List<KeyPress> Run(CastPolicy policy, double from, double to, params ChampionRow[] champions)
    {
        var presses = new List<KeyPress>();
        for (var t = from; t <= to + 1e-9; t = Math.Round(t + 0.1, 3))
            presses.AddRange(Show(policy, Frame(t, champions)));
        return presses;
    }

    [TestMethod]
    public void An_enemy_in_range_with_the_ability_up_is_pressed_after_the_hold()
    {
        var policy = AfterQ();
        var presses = Run(policy, 15.5, 18, Self(), Enemy(900));

        Assert.HasCount(1, presses);
        Assert.AreEqual("Q", presses[0].Key);
        Assert.AreEqual(17.5, presses[0].VideoTime, 1e-9);
        Assert.AreEqual(
            "coach would have pressed Q here: Karma has been in Q range (900 units) for 2.0s with Q up",
            presses[0].Sentence);
    }

    [TestMethod]
    public void Nothing_before_the_slot_has_ever_been_seen_cast()
    {
        // Ready is not assumed: the slot may not be skilled, and before the
        // HUD has printed a cooldown nothing here knows either way.
        Assert.IsEmpty(Run(Policy(), 10, 20, Self(), Enemy(900)));
    }

    [TestMethod]
    public void Nothing_while_the_slot_is_on_cooldown()
    {
        // Q cast at 10, 5s printed, half a second of slack: up at 15.5.
        Assert.IsEmpty(Run(AfterQ(), 10, 15.4, Self(), Enemy(900)));
    }

    [TestMethod]
    public void A_cast_whose_countdown_was_not_read_makes_the_slot_unknown_again()
    {
        var policy = AfterQ();
        policy.OnEvent(Cast("Q", 16, countdown: null));
        Assert.IsEmpty(Run(policy, 16, 40, Self(), Enemy(900)));
    }

    [TestMethod]
    public void The_hold_runs_from_the_cooldown_end_when_the_enemy_was_already_in_range()
    {
        // In range from 12, Q up at 15.5: the two seconds count from 15.5,
        // not from 12, so a cast thrown as they arrived is not followed by a
        // press the instant it is back.
        var policy = AfterQ();
        var presses = Run(policy, 12, 18, Self(), Enemy(900));
        Assert.HasCount(1, presses);
        Assert.AreEqual(17.5, presses[0].VideoTime, 1e-9);
    }

    [TestMethod]
    public void The_hold_restarts_when_the_enemy_leaves_range()
    {
        var policy = AfterQ();
        Assert.IsEmpty(Run(policy, 16, 17, Self(), Enemy(900)));      // 1.1s held
        Assert.IsEmpty(Run(policy, 17.1, 17.5, Self(), Enemy(1300))); // gone
        Assert.IsEmpty(Run(policy, 17.6, 19.4, Self(), Enemy(900)));  // 1.9s held again
        Assert.HasCount(1, Run(policy, 19.5, 19.6, Self(), Enemy(900)));
    }

    [TestMethod]
    public void Nothing_for_an_enemy_out_of_range()
    {
        Assert.IsEmpty(Run(AfterQ(), 15.5, 25, Self(), Enemy(1151)));
    }

    [TestMethod]
    public void W_reaches_further_than_Q()
    {
        var policy = Policy();
        policy.OnEvent(Cast("Q", 10, 5));
        policy.OnEvent(Cast("W", 10, 5));
        var presses = Run(policy, 15.5, 18, Self(), Enemy(1180));
        Assert.HasCount(1, presses);
        Assert.AreEqual("W", presses[0].Key);
    }

    [TestMethod]
    public void Nothing_for_an_enemy_in_fog_even_at_a_known_position()
    {
        // Fair play, the same rule attention keeps: a row that is not visible
        // is information the player does not have.
        Assert.IsEmpty(Run(AfterQ(), 15.5, 25, Self(), Enemy(900, visible: false)));
    }

    [TestMethod]
    public void Nothing_with_an_empty_resource_bar()
    {
        Assert.IsEmpty(Run(AfterQ(), 15.5, 25, Self(resource: 0.05), Enemy(900)));
    }

    [TestMethod]
    public void The_last_resource_reading_stands_while_the_nameplate_does_not_resolve()
    {
        var policy = AfterQ();
        Assert.IsEmpty(Show(policy, Frame(15.5, Self(resource: 0.05), Enemy(900))));
        Assert.IsEmpty(Run(policy, 15.6, 25, Self(resource: null), Enemy(900)));
    }

    [TestMethod]
    public void Nothing_while_dead()
    {
        Assert.IsEmpty(Run(AfterQ(), 15.5, 25, Self(alive: false), Enemy(900)));
    }

    [TestMethod]
    public void Nothing_for_a_champion_with_no_kit_on_file()
    {
        var policy = Policy(new CastOptions { SelfChampion = "Annie" });
        policy.OnEvent(Cast("Q", 10, 5));
        Assert.IsEmpty(Run(policy, 15.5, 25, Self(champion: "Annie"), Enemy(500)));
    }

    [TestMethod]
    public void Said_once_then_not_again_inside_the_repeat_window()
    {
        var policy = AfterQ();
        var presses = Run(policy, 15.5, 30, Self(), Enemy(900));
        // 17.5 and then 25.5: the eight-second window, with the enemy never
        // leaving and the player never casting.
        Assert.HasCount(2, presses);
        Assert.AreEqual(17.5, presses[0].VideoTime, 1e-9);
        Assert.AreEqual(25.5, presses[1].VideoTime, 1e-9);
    }

    [TestMethod]
    public void A_cast_puts_the_slot_back_on_cooldown_and_the_press_is_not_repeated()
    {
        var policy = AfterQ();
        Assert.HasCount(1, Run(policy, 15.5, 18, Self(), Enemy(900)));
        policy.OnEvent(Cast("Q", 18.2, 5));   // they pressed it: up again at 23.7
        Assert.IsEmpty(Run(policy, 18.3, 25.6, Self(), Enemy(900)));
        Assert.HasCount(1, Run(policy, 25.7, 25.8, Self(), Enemy(900)));
    }

    [TestMethod]
    public void Resync_forgets_every_cooldown()
    {
        var policy = AfterQ();
        policy.Resync(Frame(15.5, Self(), Enemy(900)));
        Assert.IsEmpty(Run(policy, 15.5, 30, Self(), Enemy(900)));
    }

    [TestMethod]
    public void Without_self_named_the_is_self_flag_is_used()
    {
        var policy = Policy(new CastOptions());
        policy.OnEvent(Cast("Q", 10, 5));
        Assert.HasCount(1, Run(policy, 15.5, 18, Self(), Enemy(900)));
    }

    [TestMethod]
    public void A_feed_without_the_ability_stage_says_so_once()
    {
        var policy = new CastPolicy(new CastOptions { SelfChampion = "Ezreal" });
        policy.Configure(new Meta { Schema = 1, HasAbilities = false });
        var cues = policy.DrainCues();
        Assert.HasCount(1, cues);
        StringAssert.Contains(cues[0].Reason, "cast coaching is off");
        Assert.IsEmpty(policy.DrainCues(), "said once");
    }

    [TestMethod]
    public void A_coaching_feed_gets_no_notice()
    {
        var policy = new CastPolicy(new CastOptions { SelfChampion = "Ezreal" });
        policy.Configure(Coaching);
        Assert.IsEmpty(policy.DrainCues());
    }
}
