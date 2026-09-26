using System.Text.Json;
using MindControl.Feed;

namespace MindControl.Tests;

/// <summary>
/// Parses hand-built samples shaped like docs/output-format.md. These pin the
/// snake_case mapping and the omitted-vs-null distinction the format leans on.
/// </summary>
[TestClass]
public sealed class FeedMessageTests
{
    [TestMethod]
    public void Frame_envelope_round_trips_the_documented_fields()
    {
        const string json = """
            {"t":"frame","seq":417,"video_time":123.456,"captured_at":null,
             "game_time":754,"game_time_observed":true,"allies_dead":1,
             "fps":9.8,"dropped":0,"lag":0.031,
             "champions":[
               {"video_time":123.456,"game_time":754,"game_time_observed":true,
                "track_id":12,"team":"red","champion":"Xerath","x":88.25,"y":41.5,
                "visible":true,"seconds_since_seen":0.0,"is_self":false,"alive":null,
                "allies_dead":1,"world_x":9142.1,"world_y":3300.5,
                "health":0.62,"resource":0.41,"level":9,
                "cast_drop":0.18,"cast_at":123.456,"cast_span":0.3,
                "cast_continuous":true,"cast_confirmed":true},
               {"video_time":123.456,"game_time":754,"game_time_observed":true,
                "track_id":3,"team":"blue","champion":"Ahri","x":10.0,"y":20.0,
                "visible":false,"seconds_since_seen":4.25,"is_self":true,"alive":true,
                "allies_dead":1}
             ]}
            """;

        var frame = JsonSerializer.Deserialize<FrameEnvelope>(json, FeedJson.Options)!;
        Assert.AreEqual(417, frame.Seq);
        Assert.IsNull(frame.CapturedAt);
        Assert.AreEqual(754, frame.GameTime);
        Assert.AreEqual(0.031, frame.Lag);
        Assert.HasCount(2, frame.Champions);

        var xerath = frame.Champions[0];
        Assert.AreEqual("Xerath", xerath.Champion);
        Assert.IsNull(xerath.Alive, "enemy liveness is always unknown");
        Assert.AreEqual(0.62, xerath.Health);
        Assert.AreEqual(0.18, xerath.CastDrop);

        var self = frame.Champions[1];
        Assert.IsTrue(self.IsSelf);
        Assert.IsFalse(self.Visible);
        Assert.IsNull(self.Health, "omitted means not measured");
        Assert.IsNull(self.Level);
    }

    [TestMethod]
    public void Death_event_parses_with_flattened_fields()
    {
        const string json = """
            {"t":"event","kind":"death","seq":500,"video_time":130.0,"game_time":761,
             "team":"blue","champion":"Ahri","track_id":3,"allies_dead":2}
            """;

        var evt = JsonSerializer.Deserialize<GameEvent>(json, FeedJson.Options)!;
        Assert.AreEqual(EventKind.Death, evt.Kind);
        Assert.AreEqual("Ahri", evt.Champion);
        Assert.AreEqual(2, evt.AlliesDead);
        Assert.IsNull(evt.DownFor);
    }

    [TestMethod]
    public void Skill_point_and_skill_spent_events_parse_with_their_fields()
    {
        const string point = """
            {"t":"event","kind":"skill_point","seq":610,"video_time":385.7,"game_time":312,
             "team":"blue","champion":"Ezreal","track_id":3,"is_self":true,"slots":["W","E","R"]}
            """;
        var evt = JsonSerializer.Deserialize<GameEvent>(point, FeedJson.Options)!;
        Assert.AreEqual(EventKind.SkillPoint, evt.Kind);
        Assert.IsTrue(evt.IsSelf);
        CollectionAssert.AreEqual(new[] { "W", "E", "R" }, evt.Slots);
        Assert.IsNull(evt.HeldFor);

        const string spent = """
            {"t":"event","kind":"skill_spent","seq":615,"video_time":386.3,"game_time":313,
             "team":"blue","champion":"Ezreal","track_id":3,"is_self":true,"held_for":0.6}
            """;
        evt = JsonSerializer.Deserialize<GameEvent>(spent, FeedJson.Options)!;
        Assert.AreEqual(EventKind.SkillSpent, evt.Kind);
        Assert.AreEqual(0.6, evt.HeldFor);
        Assert.IsNull(evt.Slots);

        const string unseen = """
            {"t":"event","kind":"skill_spent","seq":9,"video_time":2.0,"game_time":null,
             "team":"blue","champion":"Ezreal","track_id":3,"is_self":true}
            """;
        evt = JsonSerializer.Deserialize<GameEvent>(unseen, FeedJson.Options)!;
        Assert.IsNull(evt.HeldFor, "omitted when the point's arrival was never seen");
    }

    [TestMethod]
    public void Learnable_on_the_self_row_tells_an_empty_reading_from_none()
    {
        const string json = """
            {"t":"frame","seq":700,"video_time":400.0,"captured_at":null,
             "game_time":320,"game_time_observed":true,"allies_dead":0,
             "fps":9.9,"dropped":0,"lag":0.02,
             "champions":[
               {"video_time":400.0,"game_time":320,"game_time_observed":true,
                "track_id":3,"team":"blue","champion":"Ezreal","x":40.0,"y":60.0,
                "visible":true,"seconds_since_seen":0.0,"is_self":true,"alive":true,
                "allies_dead":0,"level":6,"learnable":["Q","W","E","R"]},
               {"video_time":400.0,"game_time":320,"game_time_observed":true,
                "track_id":3,"team":"blue","champion":"Ezreal","x":40.0,"y":60.0,
                "visible":true,"seconds_since_seen":0.0,"is_self":true,"alive":true,
                "allies_dead":0,"level":6,"learnable":[]},
               {"video_time":400.0,"game_time":320,"game_time_observed":true,
                "track_id":12,"team":"red","champion":"Xerath","x":88.0,"y":41.0,
                "visible":true,"seconds_since_seen":0.0,"is_self":false,"alive":null,
                "allies_dead":0}
             ]}
            """;

        var frame = JsonSerializer.Deserialize<FrameEnvelope>(json, FeedJson.Options)!;
        CollectionAssert.AreEqual(new[] { "Q", "W", "E", "R" }, frame.Champions[0].Learnable);
        Assert.IsNotNull(frame.Champions[1].Learnable, "an empty array is a reading: no point waiting");
        Assert.IsEmpty(frame.Champions[1].Learnable!);
        Assert.IsNull(frame.Champions[2].Learnable, "absent means nothing looked");
    }

    [TestMethod]
    public void Minions_cs_and_last_hits_on_the_self_row_parse()
    {
        const string json = """
            {"t":"frame","seq":800,"video_time":410.0,"captured_at":null,
             "game_time":330,"game_time_observed":true,"allies_dead":0,
             "fps":7.1,"dropped":0,"lag":0.04,
             "champions":[
               {"video_time":410.0,"game_time":330,"game_time_observed":true,
                "track_id":3,"team":"blue","champion":"Ezreal","x":40.0,"y":60.0,
                "visible":true,"seconds_since_seen":0.0,"is_self":true,"alive":true,
                "allies_dead":0,"cs":27,
                "minions":[
                  {"team":"red","x":1210.5,"y":640.0,"health":0.22,"world_x":9800.0,"world_y":4100.0},
                  {"team":"blue","x":980.0,"y":700.0}],
                "minion_dots":[{"team":"red","x":201.0,"y":188.0,"world_x":9750.0,"world_y":4150.0}],
                "last_hits":[
                  {"at":408.4,"outcome":"last_hit","health":0.08,"x":1190.0,"y":655.0},
                  {"at":408.9,"outcome":"missed","health":0.12,"x":1260.0,"y":610.0}]},
               {"video_time":410.0,"game_time":330,"game_time_observed":true,
                "track_id":3,"team":"blue","champion":"Ezreal","x":40.0,"y":60.0,
                "visible":true,"seconds_since_seen":0.0,"is_self":true,"alive":true,
                "allies_dead":0,"minions":[],"minion_dots":[]},
               {"video_time":410.0,"game_time":330,"game_time_observed":true,
                "track_id":12,"team":"red","champion":"Xerath","x":88.0,"y":41.0,
                "visible":true,"seconds_since_seen":0.0,"is_self":false,"alive":null,
                "allies_dead":0}
             ]}
            """;

        var frame = JsonSerializer.Deserialize<FrameEnvelope>(json, FeedJson.Options)!;
        var self = frame.Champions[0];
        Assert.AreEqual(27, self.Cs);
        Assert.HasCount(2, self.Minions!);
        Assert.AreEqual(MinionTeam.Red, self.Minions![0].Team);
        Assert.AreEqual(0.22, self.Minions[0].Health);
        Assert.AreEqual(9800.0, self.Minions[0].WorldX);
        Assert.IsNull(self.Minions[1].Health, "omitted when the bar was covered or cut");
        Assert.IsNull(self.Minions[1].WorldX);
        Assert.AreEqual(201.0, self.MinionDots!.Single().X);
        Assert.AreEqual(LastHitOutcome.LastHit, self.LastHits![0].Outcome);
        Assert.AreEqual(408.9, self.LastHits[1].At);
        Assert.AreEqual(LastHitOutcome.Missed, self.LastHits[1].Outcome);

        var empty = frame.Champions[1];
        Assert.IsEmpty(empty.Minions!, "an empty array is a reading: no minions on screen");
        Assert.IsEmpty(empty.MinionDots!);
        Assert.IsNull(empty.Cs, "absent until read");
        Assert.IsNull(empty.LastHits);

        var enemy = frame.Champions[2];
        Assert.IsNull(enemy.Minions, "absent means nothing looked");
        Assert.IsNull(enemy.MinionDots);
    }

    [TestMethod]
    public void Turrets_on_the_self_row_and_their_events_parse()
    {
        const string meta = """{"t":"meta","schema":1,"has_turrets":true}""";
        Assert.IsTrue(JsonSerializer.Deserialize<Meta>(meta, FeedJson.Options)!.HasTurrets);

        const string row = """
            {"video_time":410.0,"track_id":3,"team":"blue","champion":"Ezreal","x":40.0,"y":60.0,
             "visible":true,"seconds_since_seen":0.0,"is_self":true,
             "turrets":[
               {"team":"blue","lane":"bot","tier":"outer","standing":false},
               {"team":"blue","lane":"mid","tier":"inner","standing":null},
               {"team":"red","lane":"base","tier":"nexus","standing":true,"side":"top"}]}
            """;
        var turrets = JsonSerializer.Deserialize<ChampionRow>(row, FeedJson.Options)!.Turrets!;
        Assert.AreEqual(new Turret { Team = "blue", Lane = "bot", Tier = TurretTier.Outer, Standing = false }, turrets[0]);
        Assert.IsNull(turrets[1].Standing, "null is not called yet: neither standing nor fallen");
        Assert.AreEqual("top", turrets[2].Side);
        Assert.IsNull(turrets[0].Side, "only the nexus turrets carry a side");

        const string fell = """
            {"t":"event","kind":"turret_destroyed","seq":900,"video_time":512.0,"game_time":432,
             "team":"blue","champion":null,"track_id":null,"lane":"bot","tier":"outer"}
            """;
        var evt = JsonSerializer.Deserialize<GameEvent>(fell, FeedJson.Options)!;
        Assert.AreEqual(EventKind.TurretDestroyed, evt.Kind);
        Assert.AreEqual("blue", evt.Team, "the owner: we lost one");
        Assert.IsNull(evt.TrackId);
        Assert.AreEqual("bot", evt.Lane);
        Assert.AreEqual(TurretTier.Outer, evt.Tier);

        const string rebuilt = """
            {"t":"event","kind":"turret_rebuilt","seq":950,"video_time":800.0,
             "team":"red","champion":null,"track_id":null,"lane":"base","tier":"nexus","side":"bot"}
            """;
        evt = JsonSerializer.Deserialize<GameEvent>(rebuilt, FeedJson.Options)!;
        Assert.AreEqual(EventKind.TurretRebuilt, evt.Kind);
        Assert.AreEqual("bot", evt.Side);
    }

    [TestMethod]
    public void Last_hit_and_missed_cs_events_parse_with_their_fields()
    {
        const string hit = """
            {"t":"event","kind":"last_hit","seq":820,"video_time":409.9,"game_time":330,
             "team":"blue","champion":"Ezreal","track_id":3,"is_self":true,
             "at":408.4,"health":0.08,"x":1190.0,"y":655.0}
            """;
        var evt = JsonSerializer.Deserialize<GameEvent>(hit, FeedJson.Options)!;
        Assert.AreEqual(EventKind.LastHit, evt.Kind);
        Assert.IsTrue(evt.IsSelf);
        Assert.AreEqual(408.4, evt.At);
        Assert.AreEqual(0.08, evt.Health);
        Assert.AreEqual(1190.0, evt.X);
        Assert.AreEqual(655.0, evt.Y);

        const string missed = """
            {"t":"event","kind":"missed_cs","seq":821,"video_time":410.4,"game_time":331,
             "team":"blue","champion":"Ezreal","track_id":3,"is_self":true,
             "at":408.9,"health":0.12,"x":1260.0,"y":610.0}
            """;
        evt = JsonSerializer.Deserialize<GameEvent>(missed, FeedJson.Options)!;
        Assert.AreEqual(EventKind.MissedCs, evt.Kind);
        Assert.AreEqual(0.12, evt.Health);
        Assert.AreEqual(610.0, evt.Y);
    }

    [TestMethod]
    public void Unknown_keys_are_ignored_not_fatal()
    {
        const string json = """
            {"t":"event","kind":"death","seq":1,"video_time":1.0,"game_time":null,
             "team":null,"champion":null,"track_id":7,"some_future_field":{"nested":true}}
            """;

        var evt = JsonSerializer.Deserialize<GameEvent>(json, FeedJson.Options)!;
        Assert.AreEqual(7, evt.TrackId);
    }

    [TestMethod]
    public void Meta_gating_flags_parse()
    {
        const string json = """
            {"t":"meta","schema":1,"source":"clip.mp4","width":2560,"height":1440,
             "stride":3,"created":"2026-08-19T20:30:00Z","has_game_time":true,
             "has_liveness":true,"has_nameplates":false,
             "world_bounds":null,"world_units_per_pixel":null}
            """;

        var meta = JsonSerializer.Deserialize<Meta>(json, FeedJson.Options)!;
        Assert.AreEqual(1, meta.Schema);
        Assert.IsTrue(meta.HasLiveness);
        Assert.IsFalse(meta.HasNameplates);
        Assert.IsNull(meta.WorldUnitsPerPixel);
        Assert.IsFalse(meta.HasMinions, "a feed from before the lane stages reads as not measured");
        Assert.IsFalse(meta.HasLastHits);
    }

    [TestMethod]
    public void Lane_stage_flags_parse()
    {
        const string json = """
            {"t":"meta","schema":1,"source":"live","width":2560,"height":1440,
             "stride":1,"created":"2026-09-25T20:30:00Z","has_game_time":true,
             "has_liveness":true,"has_nameplates":true,
             "has_minions":true,"has_minion_dots":false,"has_last_hits":true,
             "world_bounds":null,"world_units_per_pixel":null}
            """;

        var meta = JsonSerializer.Deserialize<Meta>(json, FeedJson.Options)!;
        Assert.IsTrue(meta.HasMinions);
        Assert.IsFalse(meta.HasMinionDots);
        Assert.IsTrue(meta.HasLastHits);
    }
}
