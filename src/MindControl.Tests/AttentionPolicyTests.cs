using MindControl.Device;
using MindControl.Feed;
using MindControl.Policy;

namespace MindControl.Tests;

[TestClass]
public sealed class AttentionPolicyTests
{
    // A 15000x15000 world mapped onto a 300px minimap at (1000, 500).
    private static readonly Meta CalibratedMeta = new()
    {
        Schema = 1,
        WorldBounds = new WorldBounds { MinX = 0, MinY = 0, MaxX = 15000, MaxY = 15000 },
    };
    private static readonly MinimapRect Rect = new(1000, 500, 300, 300);

    private static AttentionPolicy NewPolicy(FrameEnvelope baseline)
    {
        var policy = new AttentionPolicy(Rect, new AttentionOptions());
        policy.Configure(CalibratedMeta);
        policy.Resync(baseline);
        return policy;
    }

    private static ChampionRow Champ(
        int track, string team, double wx, double wy,
        bool isSelf = false, bool visible = true, bool? alive = true, double sinceSeen = 0) => new()
    {
        TrackId = track,
        Team = team,
        Champion = $"champ{track}",
        X = wx / 50,
        Y = wy / 50,
        WorldX = wx,
        WorldY = wy,
        IsSelf = isSelf,
        Visible = visible,
        Alive = alive,
        SecondsSinceSeen = sinceSeen,
    };

    private static FrameEnvelope Frame(double videoTime, int? alliesDead, params ChampionRow[] champions) => new()
    {
        Seq = (long)(videoTime * 10),
        VideoTime = videoTime,
        AlliesDead = alliesDead,
        Champions = champions,
    };

    private static MouseMove Move(IReadOnlyList<Intent> intents) =>
        (MouseMove)intents.Single();

    [TestMethod]
    public void World_maps_onto_the_minimap_rect_with_y_flipped()
    {
        var map = ScreenMap.FromMeta(CalibratedMeta, Rect)!;
        Assert.AreEqual(((ushort)1000, (ushort)800), map.WorldToScreen(0, 0), "world origin is bottom-left");
        Assert.AreEqual(((ushort)1300, (ushort)500), map.WorldToScreen(15000, 15000));
        Assert.AreEqual(((ushort)1150, (ushort)650), map.WorldToScreen(7500, 7500));
    }

    [TestMethod]
    public void Nearby_enemy_vanish_snaps_the_cursor_to_its_position()
    {
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var policy = NewPolicy(Frame(10.0, 0, self, Champ(2, "red", 9000, 7500)));

        var intents = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Vanished, Team = "red", TrackId = 2,
            VideoTime = 10.1, WorldX = 9000, WorldY = 7500,
        });

        Assert.AreEqual(new MouseMove(1180, 650), Move(intents));
    }

    [TestMethod]
    public void Distant_enemy_vanish_is_ignored()
    {
        var self = Champ(1, "blue", 1000, 1000, isSelf: true);
        var policy = NewPolicy(Frame(10.0, 0, self, Champ(2, "red", 14000, 14000)));

        var intents = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Vanished, Team = "red", TrackId = 2,
            VideoTime = 10.1, WorldX = 14000, WorldY = 14000,
        });

        Assert.IsEmpty(intents);
    }

    [TestMethod]
    public void Long_missing_enemy_reappearing_far_away_still_gets_a_look()
    {
        var self = Champ(1, "blue", 1000, 1000, isSelf: true);
        var enemy = Champ(2, "red", 14000, 14000, visible: true, alive: null);
        var policy = NewPolicy(Frame(10.0, 0, self, enemy));

        var intents = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Reappeared, Team = "red", TrackId = 2,
            VideoTime = 10.1, GoneFor = 12.0,
        });

        Assert.AreEqual(new MouseMove(1280, 520), Move(intents));
    }

    [TestMethod]
    public void Briefly_missing_far_enemy_reappearing_is_ignored()
    {
        var self = Champ(1, "blue", 1000, 1000, isSelf: true);
        var policy = NewPolicy(Frame(10.0, 0, self, Champ(2, "red", 14000, 14000)));

        var intents = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Reappeared, Team = "red", TrackId = 2,
            VideoTime = 10.1, GoneFor = 1.5,
        });

        Assert.IsEmpty(intents);
    }

    [TestMethod]
    public void Own_team_events_never_trigger_a_glance()
    {
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var policy = NewPolicy(Frame(10.0, 0, self, Champ(3, "blue", 8000, 7500)));

        var intents = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Vanished, Team = "blue", TrackId = 3,
            VideoTime = 10.1, WorldX = 8000, WorldY = 7500,
        });

        Assert.IsEmpty(intents);
    }

    [TestMethod]
    public void Allies_dead_rising_glances_at_the_most_recently_lost_ally()
    {
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var lostLongAgo = Champ(3, "blue", 3000, 3000, visible: false, alive: null, sinceSeen: 20);
        var justLost = Champ(4, "blue", 12000, 12000, visible: false, alive: null, sinceSeen: 0.5);
        var policy = NewPolicy(Frame(10.0, 1, self, lostLongAgo, justLost));

        var intents = policy.OnFrame(Frame(10.1, 2, self, lostLongAgo, justLost));

        Assert.AreEqual(new MouseMove(1240, 560), Move(intents));
    }

    [TestMethod]
    public void After_the_dwell_expires_the_cursor_glides_home()
    {
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var enemy = Champ(2, "red", 9000, 7500);
        var policy = NewPolicy(Frame(10.0, 0, self, enemy));
        policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Vanished, Team = "red", TrackId = 2,
            VideoTime = 10.1, WorldX = 9000, WorldY = 7500,
        });

        // Inside the dwell window: attention holds, no movement.
        Assert.IsEmpty(policy.OnFrame(Frame(10.5, 0, self, enemy)));

        // Past it: one eased step from the glance point (1180) toward home (1150).
        var step = Move(policy.OnFrame(Frame(11.5, 0, self, enemy)));
        Assert.IsTrue(step.X < 1180 && step.X > 1150, $"expected an eased step, got {step}");
        Assert.AreEqual(650, step.Y);

        // And it converges: after enough frames the cursor rests at home.
        MouseMove? last = null;
        for (var t = 11.6; t < 13.0; t += 0.1)
        {
            var intents = policy.OnFrame(Frame(t, 0, self, enemy));
            if (intents.Count > 0)
                last = Move(intents);
        }
        Assert.AreEqual(new MouseMove(1150, 650), last);
    }

    [TestMethod]
    public void Higher_priority_glance_preempts_a_held_one()
    {
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var enemy = Champ(2, "red", 9000, 7500);
        var fallen = Champ(4, "blue", 12000, 12000, visible: false, alive: null, sinceSeen: 0.5);
        var policy = NewPolicy(Frame(10.0, 0, self, enemy, fallen));

        policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Vanished, Team = "red", TrackId = 2,
            VideoTime = 10.1, WorldX = 9000, WorldY = 7500,
        });
        var intents = policy.OnFrame(Frame(10.2, 1, self, enemy, fallen));

        Assert.AreEqual(new MouseMove(1240, 560), Move(intents), "ally death outranks a fog glance");
    }

    [TestMethod]
    public void Self_latches_to_the_majority_when_the_camera_flag_flaps()
    {
        // Annie carries is_self for a stretch, then the camera (death cam,
        // spectating) flags Nami for a few frames. Home must stay Annie's.
        var policy = NewPolicy(Frame(0, 0,
            Champ(1, "blue", 7500, 7500, isSelf: true), Champ(3, "blue", 3000, 3000)));
        for (var i = 0; i < 10; i++)
            policy.OnFrame(Frame(i * 0.1, 0,
                Champ(1, "blue", 7500, 7500, isSelf: true), Champ(3, "blue", 3000, 3000)));

        // Flag flaps to the ally; Annie (champ1) still on the map, not flagged.
        var flapped = Frame(2.0, 0,
            Champ(1, "blue", 7500, 7500), Champ(3, "blue", 3000, 3000, isSelf: true));
        var intents = new List<Intent>();
        for (var i = 0; i < 5; i++)
            intents.AddRange(policy.OnFrame(flapped with { VideoTime = 2.0 + i * 0.1 }));

        // Any moves emitted glide toward champ1's home (1150, 650), not champ3's (1060, 740).
        foreach (var move in intents.OfType<MouseMove>())
            Assert.IsTrue(Math.Abs(move.X - 1150) <= 30 && Math.Abs(move.Y - 650) <= 30,
                $"cursor should stay anchored to the majority self, got {move}");
    }

    [TestMethod]
    public void Explicit_self_champion_overrides_the_flag_entirely()
    {
        var policy = new AttentionPolicy(Rect, new AttentionOptions { SelfChampion = "champ1" });
        policy.Configure(CalibratedMeta);
        // The flag points at champ3, but champ1 is the configured self.
        var frame = Frame(10.0, 0,
            Champ(1, "blue", 7500, 7500), Champ(3, "blue", 3000, 3000, isSelf: true));
        policy.Resync(frame);

        var move = Move(policy.OnFrame(Frame(10.1, 0,
            Champ(1, "blue", 7500, 7500), Champ(3, "blue", 3000, 3000, isSelf: true))));

        Assert.AreEqual(new MouseMove(1150, 650), move);
    }

    [TestMethod]
    public void Uncalibrated_feed_leaves_the_policy_inert()
    {
        var policy = new AttentionPolicy(Rect, new AttentionOptions());
        policy.Configure(new Meta { Schema = 1, WorldBounds = null });
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        policy.Resync(Frame(10.0, 0, self));

        Assert.IsEmpty(policy.OnFrame(Frame(10.1, 0, self)));
        Assert.IsEmpty(policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Vanished, Team = "red", TrackId = 2,
            VideoTime = 10.1, WorldX = 9000, WorldY = 7500,
        }));
    }
}
