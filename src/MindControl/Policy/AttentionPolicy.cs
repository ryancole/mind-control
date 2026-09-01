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

    /// <summary>
    /// A vanish whose last sighting is older than this is old news, not a
    /// moment: glancing at it would be the fog-chasing this policy avoids.
    /// </summary>
    public double VanishFreshSeconds { get; init; } = 3.0;

    /// <summary>
    /// An enemy must have been on the map at least this long for their fade to
    /// be news. A blip flickering at the vision edge was never solidly "seen",
    /// and calling every flicker missing is noise, not awareness — recorded
    /// sessions carry hundreds of such fog-edge vanishes.
    /// </summary>
    public double MinSeenSeconds { get; init; } = 4.0;

    /// <summary>
    /// One "missing" call per champion per this window. A laner cycling
    /// through the vision edge is the same fact each time; a coach says "ss"
    /// once, not every ten seconds.
    /// </summary>
    public double VanishRepeatSeconds { get; init; } = 20.0;
}

/// <summary>
/// A mouse-only attention demonstrator, restricted to fair play: the ghost's
/// cursor lives on the minimap — resting on the self champion, snapping to
/// things a good player would have clocked <em>on their own screen</em>, dwelling,
/// then gliding home. It reacts only to enemies the player can currently see
/// (<c>visible == true</c>), to the moment an enemy fades from the minimap —
/// which the player watched happen, or should have — and to allied deaths and
/// respawns, which the game announces. It never consumes information the player
/// does not have: no enemy positions in fog, no "seconds since seen", no stale
/// last-known spots, no level or cast sensed through the fog. All timing runs
/// on video_time so replays are deterministic.
/// Without world calibration the policy stays inert. The output is where to look
/// and why — a coaching cue, never input to the game.
/// </summary>
public sealed class AttentionPolicy(MinimapRect minimap, AttentionOptions options) : IPolicy
{
    private sealed record Glance(ushort X, ushort Y, double UntilVideoTime, int Priority);

    private ScreenMap? _map;
    private bool _hasLiveness;
    private FrameEnvelope? _frame;
    private Glance? _glance;
    private (double X, double Y)? _cursor;
    private int? _alliesDead;
    private readonly Dictionary<int, double> _visibleSince = [];
    private readonly Dictionary<int, double> _seenFor = [];
    private readonly Dictionary<string, double> _lastMissingCall = [];
    private readonly Dictionary<string, int> _selfVotes = [];
    private string? _selfName;
    private (double VideoTime, string Champion, string Replaces, int Moved, bool RenamedSelf)? _lastCorrection;
    private readonly List<GlanceNote> _notes = [];

    public IReadOnlyList<GlanceNote> DrainNotes()
    {
        if (_notes.Count == 0)
            return [];
        var drained = _notes.ToArray();
        _notes.Clear();
        return drained;
    }

    public void Configure(Meta meta)
    {
        _map = ScreenMap.FromMeta(meta, minimap);
        _hasLiveness = meta.HasLiveness;
    }

    public void Resync(FrameEnvelope? latest)
    {
        _frame = latest;
        _glance = null;
        _notes.Clear();
        _alliesDead = latest?.AlliesDead;
        // Visible spells restart from the baseline: a span that straddles a
        // gap is a claim about frames we never saw. The missing-call memory
        // goes with them — video_time can restart with a new run, and one
        // repeated "ss" after a pause beats a wedged comparison.
        _visibleSince.Clear();
        _seenFor.Clear();
        _lastMissingCall.Clear();
        if (latest is not null)
            foreach (var row in latest.Champions.Where(c => c.Visible))
                _visibleSince[row.TrackId] = latest.VideoTime;
        // _cursor survives: the physical pointer is wherever we last put it.
        // _selfVotes survive too: identity outlives a gap.
    }

    public GhostCursor? OnFrame(FrameEnvelope frame)
    {
        _frame = frame;
        VoteSelf(frame);
        TrackVisibility(frame);
        if (_map is null)
            return null;

        // The counter is the fallback for feeds that cannot corroborate
        // liveness; with liveness, the death *event* names the casualty and
        // carries the count, so the guess below would only double-announce.
        if (!_hasLiveness && frame.AlliesDead is { } dead)
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
        if (evt.Kind == EventKind.Identified)
        {
            OnIdentified(evt);
            return null;
        }
        if (_map is null || Self() is not { } self)
            return null;
        if (evt.Team is not { } team)
            return null;
        return team == self.Team ? OnAllyEvent(evt, self) : OnEnemyEvent(evt, self, team);
    }

    /// <summary>
    /// Identity bookkeeping, never a glance. When the pipeline renames a track
    /// it announces the correction with <c>replaces</c>; votes earned under the
    /// old name belong to the new one, or a corrected self would go
    /// unrecognized until the majority re-accumulated from scratch.
    /// A repaired crossing swap arrives as a mutual pair at one instant —
    /// "X correcting Y" then "Y correcting X" — which migration alone gets
    /// wrong: the second event would hand the first one's merged pile straight
    /// back. The pair means the two names traded tracks, so their vote counts
    /// trade too.
    /// </summary>
    private void OnIdentified(GameEvent evt)
    {
        if (evt.Champion is not { } name || evt.Replaces is not { } previous || name == previous)
            return;

        if (_lastCorrection is { } pair && pair.VideoTime == evt.VideoTime
            && pair.Champion == previous && pair.Replaces == name)
        {
            // Second half of the exchange. The first half left `previous`
            // holding both originals (its own plus `Moved`); unwind to a swap.
            var merged = _selfVotes.GetValueOrDefault(previous);
            SetVotes(name, merged - pair.Moved);
            SetVotes(previous, pair.Moved);
            // Self followed the first half's rename if it was on that name;
            // otherwise it was the other side of the trade and moves now.
            if (!pair.RenamedSelf && _selfName == previous)
                _selfName = name;
            _lastCorrection = null;
            return;
        }

        _selfVotes.Remove(previous, out var moved);
        if (moved > 0)
            _selfVotes[name] = _selfVotes.GetValueOrDefault(name) + moved;
        var renamedSelf = _selfName == previous;
        if (renamedSelf)
            _selfName = name;
        _lastCorrection = (evt.VideoTime, name, previous, moved, renamedSelf);
    }

    private void SetVotes(string name, int votes)
    {
        if (votes > 0)
            _selfVotes[name] = votes;
        else
            _selfVotes.Remove(name);
    }

    /// <summary>
    /// Own-team events. An ally's death and respawn are announced to the player
    /// (kill banner, portrait timer), and own-team positions are always on the
    /// player's own minimap, so unlike enemies no visibility gate applies. The
    /// player's own death and respawn need no glance — they lived it.
    /// </summary>
    private GhostCursor? OnAllyEvent(GameEvent evt, ChampionRow self)
    {
        // Even without a place to look, the event's count supersedes the
        // frame counter heuristic for this death: never announce it twice.
        if (evt.Kind == EventKind.Death && evt.AlliesDead is { } counted)
            _alliesDead = Math.Max(_alliesDead ?? counted, counted);

        // By track first; by name when the track is gone — a corpse's track is
        // often dropped before the frame that reaches us (latest wins), and
        // deaths are keyed by champion upstream for the same reason.
        var row = _frame?.Champions.FirstOrDefault(c =>
            c.Team == self.Team && c is { WorldX: not null, WorldY: not null }
            && (c.TrackId == evt.TrackId || (evt.Champion is not null && c.Champion == evt.Champion)));
        var who = evt.Champion ?? row?.Champion;
        if (row is null || who == self.Champion)
            return null;

        var at = _map!.WorldToScreen(row.WorldX!.Value, row.WorldY!.Value);
        return evt.Kind switch
        {
            EventKind.Death => SnapTo(at, evt.VideoTime, priority: 3, $"ally {who ?? "?"} down"),
            EventKind.Respawn => SnapTo(at, evt.VideoTime, priority: 1,
                evt.DownFor is { } downFor
                    ? $"ally {who ?? "?"} back up after {downFor:0}s"
                    : $"ally {who ?? "?"} back up"),
            _ => null,
        };
    }

    private GhostCursor? OnEnemyEvent(GameEvent evt, ChampionRow self, string team)
    {
        if (evt.Kind == EventKind.Vanished)
            return OnEnemyVanished(evt);

        // Fair play: act only on an enemy the player can currently see. A
        // champion in fog — its last position, how long it has been missing, a
        // level or cast sensed through the fog — is information the player does
        // not have, so it is resolved from the *current* row and only when that
        // row is visible. (Enemy death and respawn never arrive: liveness is
        // HUD-corroborated and only allies have HUD panels, so they fall
        // through with the rest.)
        var row = _frame?.Champions.FirstOrDefault(c =>
            c.TrackId == evt.TrackId && c.Team == team && c.Visible
            && c is { WorldX: not null, WorldY: not null });
        if (row is null || !Near(self, row.WorldX!.Value, row.WorldY!.Value))
            return null;

        var who = evt.Champion ?? row.Champion ?? $"track {evt.TrackId}";
        var at = _map!.WorldToScreen(row.WorldX!.Value, row.WorldY!.Value);
        return evt.Kind switch
        {
            EventKind.Cast => SnapTo(at, evt.VideoTime, priority: 2, $"{who} cast nearby"),
            EventKind.LevelUp when evt.Level is { } level =>
                SnapTo(at, evt.VideoTime, priority: 1, $"{who} reached {level} nearby"),
            EventKind.Reappeared => SnapTo(at, evt.VideoTime, priority: 1, $"{who} back in your view"),
            _ => null,
        };
    }

    /// <summary>
    /// The one exception to the visible-row rule, because the vanish *moment*
    /// is the player's own information: the blip sat on their minimap until
    /// seconds ago and they watched it fade — or should have, which is the
    /// coaching point. The event carries where that was. Everything after the
    /// moment stays out of bounds: one look, then no recheck and no drift back
    /// (see <see cref="OnFrame"/>). Map-wide on purpose — a missing enemy is
    /// news wherever they faded, which is what "ss/mia" discipline teaches.
    /// </summary>
    private GhostCursor? OnEnemyVanished(GameEvent evt)
    {
        if (evt is not { WorldX: { } worldX, WorldY: { } worldY, TrackId: { } trackId })
            return null;
        // The fade predates the event by the tracker's debounce, carried on
        // the row as seconds_since_seen. Beyond the bound it is old news.
        var row = _frame?.Champions.FirstOrDefault(c => c.TrackId == trackId);
        if (row is { SecondsSinceSeen: var age } && age > options.VanishFreshSeconds)
            return null;
        // Only an enemy who was solidly on the map is missing when they fade;
        // the spell may still be open here when this event outruns the frame
        // that closes it, so measure from whichever record exists.
        var fadeAt = evt.VideoTime - (row?.SecondsSinceSeen ?? 0);
        var seenFor = _visibleSince.TryGetValue(trackId, out var since)
            ? fadeAt - since
            : _seenFor.GetValueOrDefault(trackId);
        if (seenFor < options.MinSeenSeconds)
            return null;
        var who = evt.Champion ?? row?.Champion ?? $"track {trackId}";
        // Said once, it is said: the same champion fading again inside the
        // window is the same fact, not a new call.
        if (evt.VideoTime - _lastMissingCall.GetValueOrDefault(who, double.NegativeInfinity)
            < options.VanishRepeatSeconds)
            return null;
        _lastMissingCall[who] = evt.VideoTime;
        return SnapTo(_map!.WorldToScreen(worldX, worldY), evt.VideoTime, priority: 2, $"{who} missing");
    }

    /// <summary>
    /// Visible-spell bookkeeping for the vanish gate: when each track's current
    /// spell began, and how long its last completed one ran. The spell closes
    /// at the last sighting (frame time less <c>seconds_since_seen</c>), not at
    /// the debounced frame that reports it.
    /// </summary>
    private void TrackVisibility(FrameEnvelope frame)
    {
        foreach (var row in frame.Champions)
        {
            if (row.Visible)
                _visibleSince.TryAdd(row.TrackId, frame.VideoTime);
            else if (_visibleSince.Remove(row.TrackId, out var since))
                _seenFor[row.TrackId] = frame.VideoTime - row.SecondsSinceSeen - since;
        }
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
