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

    [TestMethod]
    public void World_maps_onto_the_minimap_rect_with_y_flipped()
    {
        var map = ScreenMap.FromMeta(CalibratedMeta, Rect)!;
        Assert.AreEqual(((ushort)1000, (ushort)800), map.WorldToScreen(0, 0), "world origin is bottom-left");
        Assert.AreEqual(((ushort)1300, (ushort)500), map.WorldToScreen(15000, 15000));
        Assert.AreEqual(((ushort)1150, (ushort)650), map.WorldToScreen(7500, 7500));
    }

    // --- Fair play: only visible enemies near self are ever acted on ---

    [TestMethod]
    public void Visible_enemy_cast_nearby_snaps_the_cursor_to_it()
    {
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var enemy = Champ(2, "red", 9000, 7500, visible: true);
        var policy = NewPolicy(Frame(10.0, 0, self, enemy));

        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Cast, Team = "red", TrackId = 2, VideoTime = 10.1,
        });

        Assert.AreEqual(new GhostCursor(1180, 650), cue);
        StringAssert.Contains(policy.DrainNotes().Single().Reason, "cast nearby");
    }

    [TestMethod]
    public void A_cast_by_an_enemy_in_fog_is_never_acted_on()
    {
        // Same nearby enemy, but not currently visible to the player: whatever
        // the feed sensed through the fog, the policy must not use it.
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var fogged = Champ(2, "red", 9000, 7500, visible: false, sinceSeen: 3);
        var policy = NewPolicy(Frame(10.0, 0, self, fogged));

        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Cast, Team = "red", TrackId = 2, VideoTime = 10.1,
        });

        Assert.IsNull(cue);
        Assert.IsEmpty(policy.DrainNotes());
    }

    [TestMethod]
    public void A_distant_visible_enemy_is_ignored()
    {
        var self = Champ(1, "blue", 1000, 1000, isSelf: true);
        var enemy = Champ(2, "red", 14000, 14000, visible: true);
        var policy = NewPolicy(Frame(10.0, 0, self, enemy));

        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Cast, Team = "red", TrackId = 2, VideoTime = 10.1,
        });

        Assert.IsNull(cue);
    }

    [TestMethod]
    public void A_fresh_vanish_gets_one_look_at_the_fade_point()
    {
        // The vanish *moment* is the player's own information: the blip was on
        // their minimap until seconds ago. One look at where it faded — the
        // classic "ss/mia" call — then nothing (No_idle_recheck covers after).
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var enemy = Champ(2, "red", 9000, 7500, visible: true);
        var policy = NewPolicy(Frame(5.0, 0, self, enemy));
        policy.OnFrame(Frame(10.0, 0, self,
            Champ(2, "red", 9000, 7500, visible: false, sinceSeen: 0.1)));

        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Vanished, Team = "red", TrackId = 2,
            VideoTime = 10.1, WorldX = 9000, WorldY = 7500,
        });

        Assert.AreEqual(new GhostCursor(1180, 650), cue);
        StringAssert.Contains(policy.DrainNotes().Single().Reason, "missing");
    }

    [TestMethod]
    public void A_missing_call_is_not_repeated_inside_the_window()
    {
        // The enemy shows for a solid spell, fades, shows again, fades again
        // 15s after the first call: same fact, no second call.
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var enemy = Champ(2, "red", 9000, 7500, visible: true);
        var faded = Champ(2, "red", 9000, 7500, visible: false, sinceSeen: 0.1);
        var policy = NewPolicy(Frame(5.0, 0, self, enemy));
        policy.OnFrame(Frame(10.0, 0, self, faded));
        Assert.IsNotNull(policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Vanished, Team = "red", TrackId = 2, Champion = "champ2",
            VideoTime = 10.1, WorldX = 9000, WorldY = 7500,
        }));

        policy.OnFrame(Frame(11.0, 0, self, enemy));   // back for another solid spell
        policy.OnFrame(Frame(25.0, 0, self, faded));
        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Vanished, Team = "red", TrackId = 2, Champion = "champ2",
            VideoTime = 25.1, WorldX = 9000, WorldY = 7500,
        });

        Assert.IsNull(cue);
        Assert.HasCount(1, policy.DrainNotes(), "one fact, one call");
    }

    [TestMethod]
    public void A_blip_flickering_at_the_vision_edge_is_not_missing()
    {
        // Fog-edge traffic: visible for under a second, gone again. That enemy
        // was never solidly on the map, so each flicker is noise, not an MIA.
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var enemy = Champ(2, "red", 9000, 7500, visible: true);
        var policy = NewPolicy(Frame(10.0, 0, self, enemy));
        policy.OnFrame(Frame(11.0, 0, self,
            Champ(2, "red", 9000, 7500, visible: false, sinceSeen: 0.1)));

        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Vanished, Team = "red", TrackId = 2,
            VideoTime = 11.1, WorldX = 9000, WorldY = 7500,
        });

        Assert.IsNull(cue);
        Assert.IsEmpty(policy.DrainNotes());
    }

    [TestMethod]
    public void A_stale_vanish_is_old_news_not_a_moment()
    {
        // A slow upstream debounce means the fade happened long before the
        // event fired; looking now would be fog-chasing, not awareness — even
        // for an enemy who had been solidly on the map.
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var enemy = Champ(2, "red", 9000, 7500, visible: true);
        var policy = NewPolicy(Frame(5.0, 0, self, enemy));
        policy.OnFrame(Frame(20.0, 0, self,
            Champ(2, "red", 9000, 7500, visible: false, sinceSeen: 6)));

        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Vanished, Team = "red", TrackId = 2,
            VideoTime = 20.1, WorldX = 9000, WorldY = 7500,
        });

        Assert.IsNull(cue);
        Assert.IsEmpty(policy.DrainNotes());
    }

    [TestMethod]
    public void A_vanish_without_world_coordinates_is_ignored()
    {
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var enemy = Champ(2, "red", 9000, 7500, visible: false, sinceSeen: 0.1);
        var policy = NewPolicy(Frame(10.0, 0, self, enemy));

        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Vanished, Team = "red", TrackId = 2, VideoTime = 10.1,
        });

        Assert.IsNull(cue);
    }

    [TestMethod]
    public void A_visible_enemy_reappearing_nearby_gets_a_look()
    {
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var enemy = Champ(2, "red", 9000, 7500, visible: true);
        var policy = NewPolicy(Frame(10.0, 0, self, enemy));

        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Reappeared, Team = "red", TrackId = 2, VideoTime = 10.1, GoneFor = 12.0,
        });

        Assert.AreEqual(new GhostCursor(1180, 650), cue);
    }

    [TestMethod]
    public void A_reappearance_that_is_not_visible_on_screen_is_ignored()
    {
        // The feed says the enemy re-entered vision somewhere, but its current
        // row is not visible to this player: no cross-map reveal.
        var self = Champ(1, "blue", 1000, 1000, isSelf: true);
        var enemy = Champ(2, "red", 14000, 14000, visible: false, sinceSeen: 12);
        var policy = NewPolicy(Frame(10.0, 0, self, enemy));

        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Reappeared, Team = "red", TrackId = 2, VideoTime = 10.1, GoneFor = 12.0,
        });

        Assert.IsNull(cue);
    }

    [TestMethod]
    public void A_visible_enemy_level_up_nearby_gets_a_look()
    {
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var enemy = Champ(2, "red", 9000, 7500, visible: true);
        var policy = NewPolicy(Frame(10.0, 0, self, enemy));

        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.LevelUp, Team = "red", TrackId = 2, Level = 6, VideoTime = 10.1,
        });

        Assert.AreEqual(new GhostCursor(1180, 650), cue);
        StringAssert.Contains(policy.DrainNotes().Single().Reason, "reached 6");
    }

    [TestMethod]
    public void An_enemy_spike_in_fog_is_not_revealed()
    {
        // Level 6 across the map, sensed through the fog — the classic map-hack
        // tell. A fair coach cannot know a fogged enemy just spiked.
        var self = Champ(1, "blue", 1000, 1000, isSelf: true);
        var fogged = Champ(2, "red", 14000, 14000, visible: false, sinceSeen: 20);
        var policy = NewPolicy(Frame(10.0, 0, self, fogged));

        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.LevelUp, Team = "red", TrackId = 2, Level = 6, VideoTime = 10.1,
        });

        Assert.IsNull(cue);
        Assert.IsEmpty(policy.DrainNotes());
    }

    [TestMethod]
    public void An_allys_cast_never_triggers_a_glance()
    {
        // Own-team deaths and respawns are announced and reacted to; allies'
        // routine spellwork is not what this flags.
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var policy = NewPolicy(Frame(10.0, 0, self, Champ(3, "blue", 8000, 7500)));

        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Cast, Team = "blue", TrackId = 3, VideoTime = 10.1,
        });

        Assert.IsNull(cue);
    }

    [TestMethod]
    public void No_idle_recheck_of_a_missing_enemys_last_spot()
    {
        // A long-missing enemy in fog: a fair coach never drifts attention to
        // where it was last seen. The cursor just rests at home.
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var missing = Champ(2, "red", 11000, 11000, visible: false, alive: null, sinceSeen: 9);
        var policy = NewPolicy(Frame(10.0, 0, self, missing));

        // First frame parks the cursor at home; from then on, well past any old
        // tension interval, it never drifts to the missing enemy's last spot.
        Assert.AreEqual(new GhostCursor(1150, 650), policy.OnFrame(Frame(10.1, 0, self, missing)));
        for (var t = 10.2; t < 20.0; t += 0.1)
            Assert.AreEqual((ushort)1150, (policy.OnFrame(Frame(t, 0, self, missing)) ?? new GhostCursor(1150, 650)).X,
                "cursor rests at home, never rechecks the fogged enemy");
        Assert.IsEmpty(policy.DrainNotes());
    }

    // --- Allied deaths (announced to the player) and the home anchor ---

    [TestMethod]
    public void Allies_dead_rising_glances_at_the_most_recently_lost_ally()
    {
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var lostLongAgo = Champ(3, "blue", 3000, 3000, visible: false, alive: null, sinceSeen: 20);
        var justLost = Champ(4, "blue", 12000, 12000, visible: false, alive: null, sinceSeen: 0.5);
        var policy = NewPolicy(Frame(10.0, 1, self, lostLongAgo, justLost));

        var cue = policy.OnFrame(Frame(10.1, 2, self, lostLongAgo, justLost));

        Assert.AreEqual(new GhostCursor(1240, 560), cue);
    }

    [TestMethod]
    public void An_ally_death_event_names_the_casualty_and_snaps_to_them()
    {
        // Unlike the counter heuristic, the event says exactly who fell; the
        // last-known spot of an own-team champion is the player's own minimap.
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var fallen = Champ(4, "blue", 12000, 12000, visible: false, alive: null, sinceSeen: 0.5);
        var policy = NewPolicy(Frame(10.0, 0, self, fallen));

        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Death, Team = "blue", TrackId = 4, Champion = "champ4",
            AlliesDead = 1, VideoTime = 10.1,
        });

        Assert.AreEqual(new GhostCursor(1240, 560), cue);
        StringAssert.Contains(policy.DrainNotes().Single().Reason, "ally champ4 down");
    }

    [TestMethod]
    public void A_death_event_is_never_double_announced_by_the_counter()
    {
        // The event carries allies_dead; when the counter frame then arrives
        // showing the same rise, that death is already old news.
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var fallen = Champ(4, "blue", 12000, 12000, visible: false, alive: null, sinceSeen: 0.5);
        var policy = NewPolicy(Frame(10.0, 0, self, fallen));

        policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Death, Team = "blue", TrackId = 4, Champion = "champ4",
            AlliesDead = 1, VideoTime = 10.1,
        });
        policy.OnFrame(Frame(10.2, 1, self, fallen));

        Assert.HasCount(1, policy.DrainNotes(), "one death, one note");
    }

    [TestMethod]
    public void With_liveness_the_counter_heuristic_stays_quiet()
    {
        // A liveness-corroborating feed announces deaths as events; the
        // counter guess would only shadow them with a vaguer note.
        var policy = new AttentionPolicy(Rect, new AttentionOptions());
        policy.Configure(CalibratedMeta with { HasLiveness = true });
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var fallen = Champ(4, "blue", 12000, 12000, visible: false, alive: null, sinceSeen: 0.5);
        policy.Resync(Frame(10.0, 0, self, fallen));

        var cue = policy.OnFrame(Frame(10.1, 1, self, fallen));

        Assert.AreEqual(new GhostCursor(1150, 650), cue, "the cursor just parks at home");
        Assert.IsEmpty(policy.DrainNotes());
    }

    [TestMethod]
    public void An_ally_respawn_gets_a_low_priority_look()
    {
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var respawned = Champ(4, "blue", 3500, 3500);
        var policy = NewPolicy(Frame(10.0, 0, self, respawned));

        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Respawn, Team = "blue", TrackId = 4, Champion = "champ4",
            DownFor = 25.0, VideoTime = 10.1,
        });

        Assert.AreEqual(new GhostCursor(1070, 730), cue);
        var note = policy.DrainNotes().Single();
        Assert.AreEqual(1, note.Priority);
        StringAssert.Contains(note.Reason, "ally champ4 back up after 25s");
    }

    [TestMethod]
    public void The_players_own_death_gets_no_glance()
    {
        // The player lived it; there is nothing to point at.
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var policy = NewPolicy(Frame(10.0, 0, self));

        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Death, Team = "blue", TrackId = 1, Champion = "champ1",
            AlliesDead = 1, VideoTime = 10.1,
        });

        Assert.IsNull(cue);
        Assert.IsEmpty(policy.DrainNotes());
    }

    [TestMethod]
    public void An_identity_correction_migrates_the_self_majority()
    {
        // The pipeline renames the self track before the roster locks; the
        // votes earned under the old name must follow it, or self would go
        // unrecognized until the majority re-accumulated.
        var ally = Champ(3, "blue", 3000, 3000);
        var flagged = Champ(1, "blue", 7500, 7500, isSelf: true);
        var policy = NewPolicy(Frame(0, 0, flagged, ally));
        for (var i = 0; i < 5; i++)
            policy.OnFrame(Frame(i * 0.1, 0, flagged, ally));

        policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Identified, Team = "blue", TrackId = 1,
            Champion = "Annie", Replaces = "champ1", VideoTime = 1.0,
        });

        // Self renamed and no longer flagged; a nearby enemy cast must still land.
        var renamed = Champ(1, "blue", 7500, 7500) with { Champion = "Annie" };
        var enemy = Champ(2, "red", 9000, 7500, visible: true);
        policy.OnFrame(Frame(1.1, 0, renamed, ally, enemy));
        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Cast, Team = "red", TrackId = 2, VideoTime = 1.2,
        });

        Assert.AreEqual(new GhostCursor(1180, 650), cue, "self survives the rename");
    }

    [TestMethod]
    public void A_mutual_identity_exchange_swaps_the_self_votes()
    {
        // A repaired crossing swap arrives as two corrections at the same
        // instant: "X correcting Y" and "Y correcting X". Handled naively the
        // second migration hands the first one's merged pile straight back,
        // leaving self on the name it was just corrected away from.
        var mislabeledSelf = Champ(1, "blue", 7500, 7500, isSelf: true) with { Champion = "Leona" };
        var mislabeledAlly = Champ(3, "blue", 3000, 3000) with { Champion = "Ezreal" };
        var policy = NewPolicy(Frame(0, 0, mislabeledSelf, mislabeledAlly));
        for (var i = 0; i < 5; i++)
            policy.OnFrame(Frame(i * 0.1, 0, mislabeledSelf, mislabeledAlly));

        policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Identified, Team = "blue", TrackId = 1,
            Champion = "Ezreal", Replaces = "Leona", VideoTime = 1.0,
        });
        policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Identified, Team = "blue", TrackId = 3,
            Champion = "Leona", Replaces = "Ezreal", VideoTime = 1.0,
        });

        // Labels now honest: the player is Ezreal. Their own death must be
        // suppressed — a glance here means self is still on the old name.
        var self = mislabeledSelf with { Champion = "Ezreal" };
        var ally = mislabeledAlly with { Champion = "Leona" };
        policy.OnFrame(Frame(1.1, 0, self, ally));
        var cue = policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Death, Team = "blue", TrackId = 1, Champion = "Ezreal",
            AlliesDead = 1, VideoTime = 1.2,
        });

        Assert.IsNull(cue, "own death coached: self did not survive the exchange");
        Assert.IsEmpty(policy.DrainNotes());
    }

    [TestMethod]
    public void After_the_dwell_expires_the_cursor_glides_home()
    {
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var enemy = Champ(2, "red", 9000, 7500, visible: true);
        var policy = NewPolicy(Frame(10.0, 0, self, enemy));
        policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Cast, Team = "red", TrackId = 2, VideoTime = 10.1,
        });

        // Inside the dwell window: attention holds, no movement.
        Assert.IsNull(policy.OnFrame(Frame(10.5, 0, self, enemy)));

        // Past it: one eased step from the glance point (1180) toward home (1150).
        var step = policy.OnFrame(Frame(11.5, 0, self, enemy))!;
        Assert.IsTrue(step.X < 1180 && step.X > 1150, $"expected an eased step, got {step}");
        Assert.AreEqual(650, step.Y);

        // And it converges: after enough frames the cursor rests at home.
        GhostCursor? last = null;
        for (var t = 11.6; t < 13.0; t += 0.1)
        {
            var cue = policy.OnFrame(Frame(t, 0, self, enemy));
            if (cue is not null)
                last = cue;
        }
        Assert.AreEqual(new GhostCursor(1150, 650), last);
    }

    [TestMethod]
    public void Higher_priority_glance_preempts_a_held_one()
    {
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var enemy = Champ(2, "red", 9000, 7500, visible: true);
        var fallen = Champ(4, "blue", 12000, 12000, visible: false, alive: null, sinceSeen: 0.5);
        var policy = NewPolicy(Frame(10.0, 0, self, enemy, fallen));

        policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Cast, Team = "red", TrackId = 2, VideoTime = 10.1,
        });
        var cue = policy.OnFrame(Frame(10.2, 1, self, enemy, fallen));

        Assert.AreEqual(new GhostCursor(1240, 560), cue, "ally death outranks a cast glance");
    }

    [TestMethod]
    public void Glances_are_explained_and_drained_once()
    {
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var policy = NewPolicy(Frame(10.0, 0, self, Champ(2, "red", 9000, 7500, visible: true)));
        policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Cast, Team = "red", Champion = "Zaahen", TrackId = 2, VideoTime = 10.1,
        });

        var note = policy.DrainNotes().Single();
        Assert.AreEqual(new GlanceNote(10.1, 1180, 650, 2, "Zaahen cast nearby"), note);
        Assert.IsEmpty(policy.DrainNotes(), "notes drain once");
    }

    [TestMethod]
    public void Suppressed_snaps_leave_no_note()
    {
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        var enemy = Champ(2, "red", 9000, 7500, visible: true);
        var fallen = Champ(4, "blue", 12000, 12000, visible: false, alive: null, sinceSeen: 0.5);
        var policy = NewPolicy(Frame(10.0, 0, self, enemy, fallen));

        // Ally death takes the glance...
        policy.OnFrame(Frame(10.1, 1, self, enemy, fallen));
        var note = policy.DrainNotes().Single();
        StringAssert.StartsWith(note.Reason, "ally down");

        // ...and a lower-priority cast during the dwell is suppressed, note included.
        policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Cast, Team = "red", Champion = "Zaahen", TrackId = 2, VideoTime = 10.2,
        });
        Assert.IsEmpty(policy.DrainNotes());
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
        var cues = new List<GhostCursor>();
        for (var i = 0; i < 5; i++)
            if (policy.OnFrame(flapped with { VideoTime = 2.0 + i * 0.1 }) is { } cue)
                cues.Add(cue);

        // Any moves emitted glide toward champ1's home (1150, 650), not champ3's (1060, 740).
        foreach (var move in cues)
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

        var cue = policy.OnFrame(Frame(10.1, 0,
            Champ(1, "blue", 7500, 7500), Champ(3, "blue", 3000, 3000, isSelf: true)));

        Assert.AreEqual(new GhostCursor(1150, 650), cue);
    }

    [TestMethod]
    public void Uncalibrated_feed_leaves_the_policy_inert()
    {
        var policy = new AttentionPolicy(Rect, new AttentionOptions());
        policy.Configure(new Meta { Schema = 1, WorldBounds = null });
        var self = Champ(1, "blue", 7500, 7500, isSelf: true);
        policy.Resync(Frame(10.0, 0, self));

        Assert.IsNull(policy.OnFrame(Frame(10.1, 0, self)));
        Assert.IsNull(policy.OnEvent(new GameEvent
        {
            Kind = EventKind.Cast, Team = "red", TrackId = 2, VideoTime = 10.1,
        }));
    }
}
