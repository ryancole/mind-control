using System.Text.Json;
using System.Text.Json.Serialization;

namespace MindControl.Feed;

/// <summary>
/// Typed mirror of spectral-sight's wire format (docs/output-format.md,
/// schema 1). Optional row fields are omitted-not-null on the wire, so a null
/// here means "not measured". Unknown keys are ignored by deserialization,
/// as the format requires.
/// </summary>
public static class FeedJson
{
    public const int MaxSchema = 1;

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

public sealed record Meta
{
    public int Schema { get; init; }
    public string Source { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public int Stride { get; init; }
    public string Created { get; init; } = "";
    public bool HasGameTime { get; init; }
    public bool HasLiveness { get; init; }
    public bool HasNameplates { get; init; }

    // The coaching stages. All three need a spectral-sight run made with
    // --coach, which feeds every source frame and is offline-VOD only, so a
    // live capture leaves them false. A policy must check rather than assume:
    // false means nothing looked, never that nothing happened.
    public bool HasAbilities { get; init; }
    public bool HasThreats { get; init; }
    public bool HasSkillshots { get; init; }
    public WorldBounds? WorldBounds { get; init; }
    public double[]? WorldUnitsPerPixel { get; init; }
}

public sealed record WorldBounds
{
    public double MinX { get; init; }
    public double MinY { get; init; }
    public double MaxX { get; init; }
    public double MaxY { get; init; }
}

public sealed record ChampionRow
{
    public double VideoTime { get; init; }
    public int? GameTime { get; init; }
    public bool GameTimeObserved { get; init; }
    public int TrackId { get; init; }
    public string Team { get; init; } = "";
    public string? Champion { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public bool Visible { get; init; }
    public double SecondsSinceSeen { get; init; }
    public bool IsSelf { get; init; }
    public bool? Alive { get; init; }
    public int? AlliesDead { get; init; }

    // Present only when measured; absent means "not looked at", never "unchanged".
    public double? WorldX { get; init; }
    public double? WorldY { get; init; }
    public double? Health { get; init; }
    public double? Resource { get; init; }
    public int? Level { get; init; }

    // The five cast_* fields travel as a group.
    public double? CastDrop { get; init; }
    public double? CastAt { get; init; }
    public double? CastSpan { get; init; }
    public bool? CastContinuous { get; init; }
    public bool? CastConfirmed { get; init; }

    // The coaching arrays, on the is_self row only: the client draws nobody
    // else's cooldowns, nobody else's printed health, and the camera is the
    // player's own. Null means none resolved on this frame, which is the
    // usual case -- they are moments, not state.
    public AbilityUse[]? Abilities { get; init; }
    public Threat[]? Threats { get; init; }
    public Skillshot[]? Skillshots { get; init; }
}

/// <summary>One of the player's own casts, named to a button by the HUD.</summary>
public sealed record AbilityUse
{
    public string Slot { get; init; } = "";
    public double At { get; init; }
    public int? Countdown { get; init; }
    public bool Confirmed { get; init; }
}

/// <summary>
/// A bolt that came at the player, resolved. `Outcome` is read off their own
/// printed health, so unlike a skillshot's it is a measurement rather than a
/// geometric verdict -- "unknown" means the health text did not resolve in the
/// window, which happens on about half the frames.
/// </summary>
public sealed record Threat
{
    public double At { get; init; }
    public double Arrival { get; init; }
    public double Closest { get; init; }
    public double Speed { get; init; }
    public double[]? Heading { get; init; }
    public string Outcome { get; init; } = "";
    public int? Damage { get; init; }

    /// <summary>How far the player moved across the bolt's line between its
    /// first sighting and its arrival. The one number separating a dodge from
    /// standing still and not being hit anyway.</summary>
    public double? MovedAcross { get; init; }

    public double? Origin { get; init; }
}

/// <summary>
/// A bolt the player threw. `Outcome` is GEOMETRIC -- whether `Miss` came
/// inside spectral-sight's hit radius -- and is not read off the target's
/// health: measured on that project's footage an enemy's bar falls in half of
/// all windows of the length involved, so `Fall` is corroboration a consumer
/// may weigh and never a label. Coaching copy must not present it as truth.
///
/// Since spectral-sight's origin gate (2026-09-02, `AimConfig.max_origin_miss`)
/// a credited bolt is one whose line traces back through the player's model.
/// Before it, most credited bolts were not the player's shot. What remains is
/// a stray floor of about 7% on credited bolts, and a hit radius whose
/// justification was measured on the strays: hit/miss is a tendency over many
/// shots, never a score for one. Strays cannot be filtered on this side --
/// the event carries the bolt's launch, speed, heading, miss and flight but
/// not its position relative to the player -- so the test lives upstream.
/// See `spectral-sight/docs/aim-bolt-findings.md`.
/// </summary>
public sealed record Skillshot
{
    public string Slot { get; init; } = "";
    public double At { get; init; }

    /// <summary>When the bolt was first seen leaving the player's model. Null
    /// when none was: a blink or a self-buff (which is how a non-projectile
    /// ability excludes itself with no per-champion table), or a shot the
    /// stage did not see, which is about two thirds of casts. A null errs
    /// toward under-counting shots thrown, never toward inventing one; a
    /// consumer treats it as silence.</summary>
    public double? Launched { get; init; }

    public double? Speed { get; init; }
    public double[]? Heading { get; init; }

    /// <summary>Closest approach of the bolt's line to the target's model, px
    /// -- the aim error. Null when no enemy was on screen in front of it,
    /// which is most of a lane phase.</summary>
    public double? Miss { get; init; }

    public double? Flight { get; init; }
    public string Outcome { get; init; } = "";
    public double? Fall { get; init; }

    /// <summary>Signed offset past the target: positive went by on the side
    /// they were walking toward, negative behind them. Null when they were not
    /// moving fast enough for the direction to mean anything. Unvalidated
    /// upstream -- the footage has too few misses to check the sign.</summary>
    public double? Lead { get; init; }
}

public sealed record FrameEnvelope
{
    public long Seq { get; init; }
    public double VideoTime { get; init; }
    public double? CapturedAt { get; init; }
    public int? GameTime { get; init; }
    public bool GameTimeObserved { get; init; }
    public int? AlliesDead { get; init; }
    public double? Fps { get; init; }
    public int Dropped { get; init; }
    public double? Lag { get; init; }
    public ChampionRow[] Champions { get; init; } = [];
}

/// <summary>
/// One event, kind-specific fields flattened alongside the common ones.
/// Unrecognized kinds are ignored upstream, never fatal.
/// </summary>
public sealed record GameEvent
{
    public string Kind { get; init; } = "";
    public long Seq { get; init; }
    public double VideoTime { get; init; }
    public int? GameTime { get; init; }
    public string? Team { get; init; }
    public string? Champion { get; init; }
    public int? TrackId { get; init; }

    // identified
    public bool? IsSelf { get; init; }
    public string? Replaces { get; init; }

    // level_up
    public int? Level { get; init; }

    // death
    public int? AlliesDead { get; init; }

    // respawn / reappeared
    public double? DownFor { get; init; }
    public double? GoneFor { get; init; }

    // vanished
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? WorldX { get; init; }
    public double? WorldY { get; init; }

    // cast
    public double? Drop { get; init; }
    public double? At { get; init; }
    public double? Span { get; init; }
    public bool? Continuous { get; init; }
    public bool? Confirmed { get; init; }

    // roster
    public string[]? Champions { get; init; }

    // ability -- shares At and Confirmed with cast
    public string? Slot { get; init; }
    public int? Countdown { get; init; }

    // threat and skillshot -- both carry a bolt, so both carry these
    public double? Speed { get; init; }
    public double[]? Heading { get; init; }
    public string? Outcome { get; init; }

    // threat
    public double? Arrival { get; init; }
    public double? Closest { get; init; }
    public int? Damage { get; init; }
    public double? MovedAcross { get; init; }
    public double? Origin { get; init; }

    // skillshot
    public double? Launched { get; init; }
    public double? Miss { get; init; }
    public double? Flight { get; init; }
    public double? Fall { get; init; }
    public double? Lead { get; init; }
}

public static class EventKind
{
    public const string Identified = "identified";
    public const string LevelUp = "level_up";
    public const string Death = "death";
    public const string Respawn = "respawn";
    public const string Vanished = "vanished";
    public const string Reappeared = "reappeared";
    public const string Cast = "cast";
    public const string Roster = "roster";

    /// <summary>The player's own cast, named to a button off the HUD.</summary>
    public const string Ability = "ability";

    /// <summary>A bolt that came at the player, and what came of it.</summary>
    public const string Threat = "threat";

    /// <summary>A bolt the player threw, and how near it passed.</summary>
    public const string Skillshot = "skillshot";
}
