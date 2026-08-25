using MindControl.Device;
using MindControl.Feed;

namespace MindControl.Policy;

public sealed record AttentionOptions
{
    /// <summary>How long a glance holds before attention drifts home.</summary>
    public double DwellSeconds { get; init; } = 1.2;

    /// <summary>World-unit radius around self inside which enemy activity demands a look.</summary>
    public double NearUnits { get; init; } = 4000;

    /// <summary>An enemy unseen this long reappearing is map information wherever it happens.</summary>
    public double LongAbsenceSeconds { get; init; } = 8;

    /// <summary>Per-frame fraction of the remaining distance covered gliding home.</summary>
    public double ReturnEase { get; init; } = 0.45;

    /// <summary>Cursor moves smaller than this are not worth a wire frame.</summary>
    public double MinMovePx { get; init; } = 2;

    /// <summary>Levels whose power spike is worth a look wherever it happens; others only near self.</summary>
    public IReadOnlyCollection<int> SpikeLevels { get; init; } = [6, 11, 16];

    /// <summary>An enemy unseen this long makes the map tense: idle attention re-checks last-known spots.</summary>
    public double TensionAfterSeconds { get; init; } = 4;

    /// <summary>Minimum spacing between tension re-checks, so idling never thrashes.</summary>
    public double TensionEverySeconds { get; init; } = 2.5;

    /// <summary>
    /// The coached player's champion, when known. Without it the policy latches
    /// onto the cumulative majority of is_self rows — the per-frame flag is
    /// resolved geometrically from the camera and flaps onto allies whenever
    /// the viewport roams (death cam, spectating fights).
    /// </summary>
    public string? SelfChampion { get; init; }
}

/// <summary>
/// The first real policy: a mouse-only attention demonstrator. The ghost's
/// cursor lives on the minimap — resting on the self champion, snapping to
/// events a good player would have clocked (enemy fog changes and casts near
/// self, long-missing enemies reappearing, allies dying), dwelling, then
/// gliding home. All timing runs on video_time so replays are deterministic.
/// Without world calibration the policy stays inert.
/// </summary>
public sealed class AttentionPolicy(MinimapRect minimap, AttentionOptions options) : IPolicy
{
    private sealed record Glance(ushort X, ushort Y, double UntilVideoTime, int Priority);

    private ScreenMap? _map;
    private FrameEnvelope? _frame;
    private Glance? _glance;
    private (double X, double Y)? _cursor;
    private int? _alliesDead;
    private double? _lastTension;
    private readonly Dictionary<int, double> _tensionChecked = [];
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
        _lastTension = latest?.VideoTime;
        _tensionChecked.Clear();
        // _cursor survives: the physical pointer is wherever we last put it.
        // _selfVotes survive too: identity outlives a gap.
    }

    public IReadOnlyList<Intent> OnFrame(FrameEnvelope frame)
    {
        _frame = frame;
        VoteSelf(frame);
        if (_map is null)
            return [];

        var intents = new List<Intent>();

        if (frame.AlliesDead is { } dead)
        {
            if (dead > (_alliesDead ?? dead) && FallenAlly() is { } fallen)
                SnapTo(_map.WorldToScreen(fallen.WorldX!.Value, fallen.WorldY!.Value),
                    frame.VideoTime, priority: 3, $"ally down, likely {fallen.Champion ?? "?"}", intents);
            _alliesDead = dead;
        }

        if (_glance is { } glance && frame.VideoTime < glance.UntilVideoTime)
            return intents;   // holding the look
        _glance = null;

        if (TensionCheck(frame) is { } tension)
        {
            SnapTo(tension.Point, frame.VideoTime, priority: 0, tension.Reason, intents);
            return intents;
        }

        if (Self() is { WorldX: { } wx, WorldY: { } wy })
            GlideToward(_map.WorldToScreen(wx, wy), intents);
        return intents;
    }

    public IReadOnlyList<Intent> OnEvent(GameEvent evt)
    {
        if (_map is null || Self() is not { } self)
            return [];

        var intents = new List<Intent>();
        var enemy = evt.Team is { } team && team != self.Team;
        var who = evt.Champion ?? $"track {evt.TrackId}";
        switch (evt.Kind)
        {
            case EventKind.Vanished when enemy:
                if (evt is { WorldX: { } wx, WorldY: { } wy } && Near(self, wx, wy))
                    SnapTo(_map.WorldToScreen(wx, wy), evt.VideoTime, priority: 1,
                        $"{who} vanished nearby", intents);
                break;

            case EventKind.Reappeared when enemy:
                if (Position(evt) is { } pos
                    && (evt.GoneFor >= options.LongAbsenceSeconds || Near(self, pos.X, pos.Y)))
                    SnapTo(_map.WorldToScreen(pos.X, pos.Y), evt.VideoTime, priority: 2,
                        evt.GoneFor >= options.LongAbsenceSeconds
                            ? $"{who} reappeared after {evt.GoneFor:0}s"
                            : $"{who} reappeared nearby", intents);
                break;

            case EventKind.Cast when enemy:
                if (Position(evt) is { } at && Near(self, at.X, at.Y))
                    SnapTo(_map.WorldToScreen(at.X, at.Y), evt.VideoTime, priority: 2,
                        $"{who} cast nearby", intents);
                break;

            case EventKind.LevelUp when enemy && evt.Level is { } level:
                var spike = options.SpikeLevels.Contains(level);
                if (Position(evt) is { } spot && (spike || Near(self, spot.X, spot.Y)))
                    SnapTo(_map.WorldToScreen(spot.X, spot.Y), evt.VideoTime,
                        priority: spike ? 2 : 1,
                        spike ? $"{who} hit {level}" : $"{who} reached {level} nearby", intents);
                break;
        }
        return intents;
    }

    /// <summary>
    /// Idle attention under fog pressure: with an enemy unseen long enough,
    /// re-check its last-known spot instead of resting at home. Rotates through
    /// the missing (least-recently-checked first) at priority 0, so any real
    /// event steals the look.
    /// </summary>
    private ((ushort X, ushort Y) Point, string Reason)? TensionCheck(FrameEnvelope frame)
    {
        if (Self() is not { } self)
            return null;
        if (_lastTension is { } last && frame.VideoTime - last < options.TensionEverySeconds)
            return null;
        var missing = frame.Champions
            .Where(c => c.Team != self.Team && !c.Visible && c.Alive != false
                && c.SecondsSinceSeen >= options.TensionAfterSeconds
                && c is { WorldX: not null, WorldY: not null })
            .OrderBy(c => _tensionChecked.GetValueOrDefault(c.TrackId, double.MinValue))
            .ThenBy(c => c.TrackId)
            .FirstOrDefault();
        if (missing is null)
            return null;
        _lastTension = frame.VideoTime;
        _tensionChecked[missing.TrackId] = frame.VideoTime;
        var who = missing.Champion ?? $"track {missing.TrackId}";
        return (_map!.WorldToScreen(missing.WorldX!.Value, missing.WorldY!.Value),
            $"{who} missing {missing.SecondsSinceSeen:0}s, checking last spot");
    }

    private void SnapTo(
        (ushort X, ushort Y) point, double videoTime, int priority, string reason, List<Intent> intents)
    {
        if (_glance is { } held && videoTime < held.UntilVideoTime && priority < held.Priority)
            return;
        _glance = new Glance(point.X, point.Y, videoTime + options.DwellSeconds, priority);
        _notes.Add(new GlanceNote(videoTime, point.X, point.Y, priority, reason));
        Emit(point.X, point.Y, intents);
    }

    private void GlideToward((ushort X, ushort Y) home, List<Intent> intents)
    {
        if (_cursor is not { } cursor)
        {
            Emit(home.X, home.Y, intents);
            return;
        }
        var stepX = (home.X - cursor.X) * options.ReturnEase;
        var stepY = (home.Y - cursor.Y) * options.ReturnEase;
        if (Math.Abs(stepX) + Math.Abs(stepY) < options.MinMovePx)
        {
            // Easing from here would dribble sub-pixel moves: finish the glide.
            if (Math.Abs(home.X - cursor.X) + Math.Abs(home.Y - cursor.Y) >= 1)
                Emit(home.X, home.Y, intents);
            return;
        }
        Emit(cursor.X + stepX, cursor.Y + stepY, intents);
    }

    private void Emit(double x, double y, List<Intent> intents)
    {
        _cursor = (x, y);
        intents.Add(new MouseMove((ushort)Math.Round(x), (ushort)Math.Round(y)));
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

    /// <summary>Where the event's champion is, from its track's row in the latest frame.</summary>
    private (double X, double Y)? Position(GameEvent evt)
    {
        var row = _frame?.Champions.FirstOrDefault(c => c.TrackId == evt.TrackId);
        return row is { WorldX: { } x, WorldY: { } y } ? (x, y) : null;
    }

    /// <summary>
    /// The HUD counted a new ally death without naming the casualty (liveness
    /// often cannot); the ally most recently lost from the minimap is the best
    /// guess for where to look.
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
