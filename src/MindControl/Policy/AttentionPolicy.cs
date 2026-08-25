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
    private readonly Dictionary<string, int> _selfVotes = [];
    private string? _selfName;

    public void Configure(Meta meta) => _map = ScreenMap.FromMeta(meta, minimap);

    public void Resync(FrameEnvelope? latest)
    {
        _frame = latest;
        _glance = null;
        _alliesDead = latest?.AlliesDead;
        // _cursor survives: the physical pointer is wherever we last put it.
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
            if (dead > (_alliesDead ?? dead) && FallenAllyPosition() is { } fallen)
                SnapTo(fallen, frame.VideoTime, priority: 3, intents);
            _alliesDead = dead;
        }

        if (_glance is { } glance && frame.VideoTime < glance.UntilVideoTime)
            return intents;   // holding the look
        _glance = null;

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
        switch (evt.Kind)
        {
            case EventKind.Vanished when enemy:
                if (evt is { WorldX: { } wx, WorldY: { } wy } && Near(self, wx, wy))
                    SnapTo(_map.WorldToScreen(wx, wy), evt.VideoTime, priority: 1, intents);
                break;

            case EventKind.Reappeared when enemy:
                if (Position(evt) is { } pos
                    && (evt.GoneFor >= options.LongAbsenceSeconds || Near(self, pos.X, pos.Y)))
                    SnapTo(_map.WorldToScreen(pos.X, pos.Y), evt.VideoTime, priority: 2, intents);
                break;

            case EventKind.Cast when enemy:
                if (Position(evt) is { } at && Near(self, at.X, at.Y))
                    SnapTo(_map.WorldToScreen(at.X, at.Y), evt.VideoTime, priority: 2, intents);
                break;
        }
        return intents;
    }

    private void SnapTo((ushort X, ushort Y) point, double videoTime, int priority, List<Intent> intents)
    {
        if (_glance is { } held && videoTime < held.UntilVideoTime && priority < held.Priority)
            return;
        _glance = new Glance(point.X, point.Y, videoTime + options.DwellSeconds, priority);
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
    private (ushort X, ushort Y)? FallenAllyPosition()
    {
        if (Self() is not { } self || _map is null)
            return null;
        var fallen = _frame!.Champions
            .Where(c => !c.IsSelf && c.Team == self.Team && c.Alive != true && !c.Visible)
            .OrderBy(c => c.SecondsSinceSeen)
            .FirstOrDefault();
        return fallen is { WorldX: { } x, WorldY: { } y } ? _map.WorldToScreen(x, y) : null;
    }
}
