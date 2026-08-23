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
    public JsonElement? WorldBounds { get; init; }
    public double[]? WorldUnitsPerPixel { get; init; }
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
}
