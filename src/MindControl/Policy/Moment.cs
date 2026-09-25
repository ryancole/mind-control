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
        + "the minimap. Enemies hidden in fog of war are not listed, because the player cannot "
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

/// <summary>Something the coach already did: "pressed Q", "stepped up-left", "remarked on aim".</summary>
public sealed record RecentAction(string Did, double SecondsAgo);

/// <summary>A bolt that came at the player, as their own screen showed it.</summary>
public sealed record BoltOccasion(
    string Kind, string? From, string Outcome, int? Damage, double? MovedAcrossPx,
    double? WarningSeconds, double? PreviousLandingSecondsAgo, int? PreviousLandingDamage);

/// <summary>A shot of the player's that was seen leaving them with an enemy in front of it.</summary>
public sealed record ShotOccasion(
    string Kind, string Slot, double PassedPx, string Side, bool Wide,
    IReadOnlyList<ShotFact> RecentShotsSeenAtATarget);

public sealed record ShotFact(string Slot, double PassedPx, bool Wide);
