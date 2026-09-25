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
    }
}
