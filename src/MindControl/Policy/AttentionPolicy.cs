using MindControl.Feed;

namespace MindControl.Policy;

public sealed record AttentionOptions
{
    /// <summary>How long a glance holds before attention drifts home.</summary>
    public double DwellSeconds { get; init; } = 1.2;

    /// <summary>World-unit radius around self inside which a visible enemy's activity demands a look.</summary>
    public double NearUnits { get; init; } = 4000;

    /// <summary>Per-frame fraction of the remaining distance covered gliding home.</summary>
    public double ReturnEase { get; init; } = 0.45;

    /// <summary>Cursor moves smaller than this are not worth recording.</summary>
    public double MinMovePx { get; init; } = 2;

    /// <summary>
    /// The coached player's champion, when known. Without it the policy latches
    /// onto the cumulative majority of is_self rows — the per-frame flag is
    /// resolved geometrically from the camera and flaps onto allies whenever
    /// the viewport roams (death cam, spectating fights).
    /// </summary>
    public string? SelfChampion { get; init; }
}

/// <summary>
/// A mouse-only attention demonstrator, restricted to fair play: the ghost's
/// cursor lives on the minimap — resting on the self champion, snapping to
/// things a good player would have clocked <em>on their own screen</em>, dwelling,
/// then gliding home. It reacts only to enemies the player can currently see
/// (<c>visible == true</c>) and to allied deaths, which the game announces. It
/// never consumes information the player does not have: no enemy positions in
/// fog, no "seconds since seen", no last-known spots, no level or cast sensed
/// through the fog. All timing runs on video_time so replays are deterministic.
/// Without world calibration the policy stays inert. The output is where to look
/// and why — a coaching cue, never input to the game.
/// </summary>
public sealed class AttentionPolicy(MinimapRect minimap, AttentionOptions options) : IPolicy
{
    private sealed record Glance(ushort X, ushort Y, double UntilVideoTime, int Priority);

    private ScreenMap? _map;
    private FrameEnvelope? _frame;
    private Glance? _glance;
    private (double X, double Y)? _cursor;
    private int? _alliesDead;
    private readonly Dictionary<string, int> _selfVotes = [];
    private string? _selfName;
    private readonly List<GlanceNote> _notes = [];

    public IReadOnlyList<GlanceNote> DrainNotes()
    {
        if (_notes.Count == 0)
            return [];
        var drained = _notes.ToArray();
        _notes.Clear();
        return drained;
    }

    public void Configure(Meta meta) => _map = ScreenMap.FromMeta(meta, minimap);

    public void Resync(FrameEnvelope? latest)
    {
        _frame = latest;
        _glance = null;
        _notes.Clear();
        _alliesDead = latest?.AlliesDead;
        // _cursor survives: the physical pointer is wherever we last put it.
        // _selfVotes survive too: identity outlives a gap.
    }

    public GhostCursor? OnFrame(FrameEnvelope frame)
    {
        _frame = frame;
        VoteSelf(frame);
        if (_map is null)
            return null;

        if (frame.AlliesDead is { } dead)
        {
            var rising = dead > (_alliesDead ?? dead);
            _alliesDead = dead;
            // An allied death is announced to the player (minimap indicator,
            // death recap), so looking where it happened is fair play.
            if (rising && FallenAlly() is { } fallen
                && SnapTo(_map.WorldToScreen(fallen.WorldX!.Value, fallen.WorldY!.Value),
                    frame.VideoTime, priority: 3, $"ally down, likely {fallen.Champion ?? "?"}") is { } snap)
                return snap;
        }

        if (_glance is { } glance && frame.VideoTime < glance.UntilVideoTime)
            return null;   // holding the look
        _glance = null;

        if (Self() is { WorldX: { } wx, WorldY: { } wy })
            return GlideToward(_map.WorldToScreen(wx, wy));
        return null;
    }

    public GhostCursor? OnEvent(GameEvent evt)
    {
        if (_map is null || Self() is not { } self)
            return null;
        if (evt.Team is not { } team || team == self.Team)
            return null;   // allies' own movements are not what this flags

        // Fair play: act only on an enemy the player can currently see. A
        // champion in fog — its last position, how long it has been missing, a
        // level or cast sensed through the fog — is information the player does
        // not have, so it is resolved from the *current* row and only when that
        // row is visible. A vanished enemy is by definition no longer visible
        // and so is never followed into the fog.
        var row = _frame?.Champions.FirstOrDefault(c =>
            c.TrackId == evt.TrackId && c.Team == team && c.Visible
            && c is { WorldX: not null, WorldY: not null });
        if (row is null || !Near(self, row.WorldX!.Value, row.WorldY!.Value))
            return null;

        var who = evt.Champion ?? row.Champion ?? $"track {evt.TrackId}";
        var at = _map.WorldToScreen(row.WorldX!.Value, row.WorldY!.Value);
        return evt.Kind switch
        {
            EventKind.Cast => SnapTo(at, evt.VideoTime, priority: 2, $"{who} cast nearby"),
            EventKind.LevelUp when evt.Level is { } level =>
                SnapTo(at, evt.VideoTime, priority: 1, $"{who} reached {level} nearby"),
            EventKind.Reappeared => SnapTo(at, evt.VideoTime, priority: 1, $"{who} back in your view"),
            _ => null,
        };
    }

    private GhostCursor? SnapTo((ushort X, ushort Y) point, double videoTime, int priority, string reason)
    {
        if (_glance is { } held && videoTime < held.UntilVideoTime && priority < held.Priority)
            return null;
        _glance = new Glance(point.X, point.Y, videoTime + options.DwellSeconds, priority);
        _notes.Add(new GlanceNote(videoTime, point.X, point.Y, priority, reason));
        return Emit(point.X, point.Y);
    }

    private GhostCursor? GlideToward((ushort X, ushort Y) home)
    {
        if (_cursor is not { } cursor)
            return Emit(home.X, home.Y);
        var stepX = (home.X - cursor.X) * options.ReturnEase;
        var stepY = (home.Y - cursor.Y) * options.ReturnEase;
        if (Math.Abs(stepX) + Math.Abs(stepY) < options.MinMovePx)
        {
            // Easing from here would dribble sub-pixel moves: finish the glide.
            if (Math.Abs(home.X - cursor.X) + Math.Abs(home.Y - cursor.Y) >= 1)
                return Emit(home.X, home.Y);
            return null;
        }
        return Emit(cursor.X + stepX, cursor.Y + stepY);
    }

    private GhostCursor Emit(double x, double y)
    {
        _cursor = (x, y);
        return new GhostCursor((ushort)Math.Round(x), (ushort)Math.Round(y));
    }

    /// <summary>A new majority must strictly overtake the incumbent, so ties never flap.</summary>
    private void VoteSelf(FrameEnvelope frame)
    {
        if (options.SelfChampion is not null)
            return;
        if (frame.Champions.FirstOrDefault(c => c.IsSelf)?.Champion is not { } flagged)
            return;
        _selfVotes[flagged] = _selfVotes.GetValueOrDefault(flagged) + 1;
        if (_selfName is null || _selfVotes[flagged] > _selfVotes.GetValueOrDefault(_selfName))
            _selfName = flagged;
    }

    private ChampionRow? Self() => (options.SelfChampion ?? _selfName) is { } name
        ? _frame?.Champions.FirstOrDefault(c => c.Champion == name)
        : _frame?.Champions.FirstOrDefault(c => c.IsSelf);

    private bool Near(ChampionRow self, double worldX, double worldY) =>
        self is { WorldX: { } sx, WorldY: { } sy }
        && double.Hypot(worldX - sx, worldY - sy) <= options.NearUnits;

    /// <summary>
    /// The HUD counted a new ally death without naming the casualty (liveness
    /// often cannot); the ally most recently lost from the minimap is the best
    /// guess for where to look. This is own-team information the player already
    /// has, paired with a death the game announces.
    /// </summary>
    private ChampionRow? FallenAlly()
    {
        if (Self() is not { } self)
            return null;
        return _frame!.Champions
            .Where(c => c.Champion != self.Champion && c.Team == self.Team
                && c.Alive != true && !c.Visible
                && c is { WorldX: not null, WorldY: not null })
            .OrderBy(c => c.SecondsSinceSeen)
            .FirstOrDefault();
    }
}
