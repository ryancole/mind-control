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

    // The lane stages. Unlike the three above these run live too. Each flag
    // gates its own fields the same way: false means nothing looked, never
    // that there were no minions or no creep score.
    public bool HasMinions { get; init; }

    /// <summary>Needs a minimap panel of 400px or more (the in-game minimap
    /// scale turned up); the small default panel leaves this false.</summary>
    public bool HasMinionDots { get; init; }

    /// <summary>Gates both <c>cs</c> and <c>last_hits</c>.</summary>
    public bool HasLastHits { get; init; }

    /// <summary>Gates <c>turrets</c> and the turret events. Needs the world
    /// calibration and a minimap panel of 400px or more, like
    /// <see cref="HasMinionDots"/>.</summary>
    public bool HasTurrets { get; init; }
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

    /// <summary>
    /// The slots ("Q"/"W"/"E"/"R") whose level-up chevron is lit: the
    /// abilities an unspent point could go into. Unlike the arrays above this
    /// is state, repeated on every self row while it holds. Empty means the
    /// HUD was read and shows no point; null means nothing looked (no
    /// calibration, not the game, or the player dead).
    /// </summary>
    public string[]? Learnable { get; init; }

    /// <summary>
    /// Minions on the player's own screen, read from their health bars. State,
    /// like <see cref="Learnable"/>: empty means the view was read and shows
    /// none, null means nothing looked. A floor on the count (bars in a clump
    /// overlap); the positions are the useful part.
    /// </summary>
    public Minion[]? Minions { get; init; }

    /// <summary>
    /// Minions anywhere on the map, from the minimap. Positions are
    /// minimap-crop pixels like the row's own X/Y. Empty and null mean what
    /// they do for <see cref="Minions"/>; a dot under a champion marker is not
    /// seen, so this too is a floor.
    /// </summary>
    public MinionDot[]? MinionDots { get; init; }

    /// <summary>The player's creep score. Filtered upstream so it never falls,
    /// and a rise can lag the HUD by a reading or two. Null until first read.</summary>
    public int? Cs { get; init; }

    /// <summary>Enemy minion deaths on the player's screen, judged against the
    /// creep score. A moment, not state: only on the frame an entry resolved,
    /// about 1.5s after the death.</summary>
    public LastHit[]? LastHits { get; init; }

    /// <summary>
    /// Every turret on the map, off the minimap icons: always all 22, in a
    /// fixed order, once the minimap has been read. State, like
    /// <see cref="Learnable"/>; null means nothing looked.
    /// </summary>
    public Turret[]? Turrets { get; init; }
}

/// <summary>
/// One turret, as the minimap shows it. <see cref="Team"/> is its owner, in
/// <see cref="MinionTeam"/>'s terms (blue is the player's own).
/// </summary>
public sealed record Turret
{
    public string Team { get; init; } = "";

    /// <summary>"top", "mid", "bot", or "base" for the two nexus turrets.</summary>
    public string Lane { get; init; } = "";

    /// <summary>One of <see cref="TurretTier"/>.</summary>
    public string Tier { get; init; } = "";

    /// <summary>
    /// Null means not yet called: its spot has not been seen clearly either
    /// way. Not standing and not fallen. A fall is adopted about 5 s after it
    /// happens, later if something sits on the spot. A lane turret never
    /// stands again; a nexus turret is rebuilt by the game.
    /// </summary>
    public bool? Standing { get; init; }

    /// <summary>"top" or "bot", on the two nexus turrets only: the one thing telling them apart.</summary>
    public string? Side { get; init; }
}

public static class TurretTier
{
    public const string Outer = "outer";
    public const string Inner = "inner";
    public const string Inhibitor = "inhibitor";
    public const string Nexus = "nexus";
}

public static class MinionTeam
{
    /// <summary>The player's own side.</summary>
    public const string Blue = "blue";
    public const string Red = "red";
}

/// <summary>One minion on the world view.</summary>
public sealed record Minion
{
    public string Team { get; init; } = "";

    /// <summary>World-view pixels (the space <see cref="Threat"/> uses) of the
    /// minion's body, estimated below its bar.</summary>
    public double X { get; init; }
    public double Y { get; init; }

    /// <summary>Bar fill in [0, 1]. Null when another bar covered this one or
    /// the frame edge cut it: the minion is there, its health is not legible.</summary>
    public double? Health { get; init; }

    /// <summary>Projected from the screen; expect roughly a hundred units of error.</summary>
    public double? WorldX { get; init; }
    public double? WorldY { get; init; }
}

/// <summary>One minion dot on the minimap. No health.</summary>
public sealed record MinionDot
{
    public string Team { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double? WorldX { get; init; }
    public double? WorldY { get; init; }
}

/// <summary>
/// An enemy minion that died on the player's screen, and whether their creep
/// score rose for it. A "missed" says only that the player did not get the
/// kill -- not that they were in range to; that is this side's judgement.
/// Judged only while the player is alive.
/// </summary>
public sealed record LastHit
{
    /// <summary>The video_time the minion's bar was last seen.</summary>
    public double At { get; init; }

    /// <summary>One of <see cref="LastHitOutcome"/>.</summary>
    public string Outcome { get; init; } = "";

    /// <summary>The bar's last legible fill. A last hit well above ~0.35 was
    /// an ability kill rather than an auto-attack.</summary>
    public double Health { get; init; }

    /// <summary>World-view pixels where it died.</summary>
    public double X { get; init; }
    public double Y { get; init; }
}

public static class LastHitOutcome
{
    public const string LastHit = "last_hit";
    public const string Missed = "missed";

    /// <summary>The score was unreadable around the death. Makes no event.</summary>
    public const string Unknown = "unknown";
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

    // skill_point: the slots the waiting point could go into
    public string[]? Slots { get; init; }

    // skill_spent: seconds from the point's first reading to its spending;
    // absent when the arrival was never seen
    public double? HeldFor { get; init; }

    // death
    public int? AlliesDead { get; init; }

    // respawn / reappeared
    public double? DownFor { get; init; }
    public double? GoneFor { get; init; }

    // vanished; last_hit and missed_cs (world-view pixels there)
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? WorldX { get; init; }
    public double? WorldY { get; init; }

    // cast
    public double? Drop { get; init; }

    // shared by cast, ability, last_hit and missed_cs
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

    // last_hit and missed_cs: the minion bar's last legible fill
    public double? Health { get; init; }

    // turret_destroyed and turret_rebuilt: Team is the turret's owner, and
    // Side is set on a nexus turret only
    public string? Lane { get; init; }
    public string? Tier { get; init; }
    public string? Side { get; init; }
}

public static class EventKind
{
    public const string Identified = "identified";
    public const string LevelUp = "level_up";

    /// <summary>A skill point waiting on the player's HUD, and the slots it could go into. Always the player's own.</summary>
    public const string SkillPoint = "skill_point";

    /// <summary>The waiting point went into an ability; the HUD's chevrons cleared.</summary>
    public const string SkillSpent = "skill_spent";
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

    /// <summary>An enemy minion died on the player's screen and their creep score rose for it.</summary>
    public const string LastHit = "last_hit";

    /// <summary>A low enemy minion died on the player's screen and someone or something else got it.</summary>
    public const string MissedCs = "missed_cs";

    /// <summary>A turret fell, about 5 s ago. The event's team is the turret's owner.</summary>
    public const string TurretDestroyed = "turret_destroyed";

    /// <summary>A fallen nexus turret stands again. Lane turrets never do.</summary>
    public const string TurretRebuilt = "turret_rebuilt";
}
