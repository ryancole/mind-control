using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MindControl.Policy;

/// <summary>
/// What the coach model is told about a moment: the state of every question
/// put to it. This is where fair play lives now. The model cannot see the
/// feed; it sees this, and this carries only what the player can see on
/// their own screen -- their HUD, the champions drawn in front of them, their
/// own team on the minimap -- and nothing sensed through the fog of war: no
/// enemy in fog appears here at all, not their position, not how long they
/// have been gone. Every number is a measurement the code made; the model is
/// asked for judgement, never for arithmetic.
/// </summary>
public sealed record Moment
{
    /// <summary>The standing brief that opens every state.</summary>
    public const string SettingText =
        "League of Legends on Summoner's Rift, seen from the coached player's own screen. "
        + "You sit in a coach's seat over their shoulder, deciding what a good player would do "
        + "in their place at this exact moment. Everything below is what the player can see: "
        + "their own HUD, champions drawn on the screen in front of them, and their own team on "
        + "the minimap, themselves included (`whereabouts` is where they stand on it, how long they "
        + "have stood there, and how far each lane is). Enemies hidden in fog of war are not listed, because the player cannot "
        + "see them. Distances are in game units; directions are as they appear on the player's "
        + "screen, where their own base is at the lower left. `coach` lists what you, the coach, "
        + "have already done recently, so you do not repeat yourself.";

    /// <summary>How a state is written on the wire: snake_case, nothing null, nothing escaped that need not be.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Setting { get; init; } = SettingText;
    public double VideoTime { get; init; }
    public string? GameClock { get; init; }
    public PlayerFacts? Player { get; init; }
    public IReadOnlyList<SlotFacts> Abilities { get; init; } = [];
    public IReadOnlyList<EnemyFacts> VisibleEnemies { get; init; } = [];
    public IReadOnlyList<AllyFacts> Allies { get; init; } = [];
    public WhereaboutsFacts? Whereabouts { get; init; }
    public IReadOnlyList<RecentAction> Coach { get; init; } = [];

    /// <summary>What the question is about, when it is about an event rather than the moment itself.</summary>
    public object? Occasion { get; init; }
}

/// <summary>The coached player, off their own nameplate and HUD.</summary>
public sealed record PlayerFacts(
    string Champion, string Team, bool Alive, double? Health, double? Mana, int? Level);

/// <summary>
/// One of the player's buttons. <see cref="Status"/> is "up", "cooldown" or
/// "unknown"; a slot is only known once the HUD has printed a cooldown for
/// it, and the print is whole seconds, so <see cref="SecondsPastPrintedCooldown"/>
/// can be small and the ability still a fraction from ready.
/// </summary>
public sealed record SlotFacts(
    string Slot, string? Kind, double? Range, string Status,
    double? SecondsPastPrintedCooldown, double? UpInSeconds, string? Note);

/// <summary>An enemy the player can see right now.</summary>
public sealed record EnemyFacts(
    string Champion, double DistanceUnits, string ScreenDirection, double VisibleForSeconds,
    double? Health, int? Level, IReadOnlyList<string> InRangeOf, double? WithinReachForSeconds);

/// <summary>An ally, always on the player's own minimap.</summary>
public sealed record AllyFacts(string Champion, bool? Alive, double? DistanceUnits);

/// <summary>
/// Where the player stands, off their own minimap: the place named as a coach
/// would (<see cref="RiftMap.Place"/>), how long they have stood on that spot,
/// and each lane's distance and screen direction with the allies already in it.
/// </summary>
public sealed record WhereaboutsFacts(string Place, double StoodStillForSeconds, IReadOnlyList<LaneFacts> Lanes);

/// <summary>A lane as seen from where the player stands. No direction when they are standing in it.</summary>
public sealed record LaneFacts(string Lane, double DistanceUnits, string? ScreenDirection, IReadOnlyList<string> AlliesThere);

/// <summary>Something the coach already did: "pressed Q", "stepped up-left", "remarked on aim".</summary>
public sealed record RecentAction(string Did, double SecondsAgo);

/// <summary>A bolt that came at the player, as their own screen showed it.</summary>
public sealed record BoltOccasion(
    string Kind, string? From, string Outcome, int? Damage, double? MovedAcrossPx,
    double? WarningSeconds, double? PreviousLandingSecondsAgo, int? PreviousLandingDamage);

/// <summary>
/// The player reaching a new level, off their own HUD: a point to spend.
/// <see cref="UltimateTakesAPoint"/> is the game's rule that the ultimate
/// takes a point at levels 6, 11 and 16 and at no other.
/// <see cref="CoachWatchingSinceLevel"/> is the first level-up the coach saw
/// this game: the points placed before it, the level-one point among them,
/// are in nobody's count. Which buttons have been seen cast, and so
/// certainly hold a point already, is in <see cref="Moment.Abilities"/>.
/// </summary>
public sealed record LevelOccasion(string Kind, int Level, bool UltimateTakesAPoint, int CoachWatchingSinceLevel);

/// <summary>A shot of the player's that was seen leaving them with an enemy in front of it.</summary>
public sealed record ShotOccasion(
    string Kind, string Slot, double PassedPx, string Side, bool Wide,
    IReadOnlyList<ShotFact> RecentShotsSeenAtATarget);

public sealed record ShotFact(string Slot, double PassedPx, bool Wide);
