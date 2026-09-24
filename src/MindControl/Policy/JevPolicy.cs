using System.Collections.Concurrent;
using System.Diagnostics;
using Jev;
using MindControl.Feed;

namespace MindControl.Policy;

public sealed record JevOptions
{
    /// <summary>
    /// The coached player's champion, when known. Without it the policy
    /// latches onto the cumulative majority of is_self rows -- the per-frame
    /// flag is resolved geometrically from the camera and flaps onto allies
    /// whenever the viewport roams (death cam, spectating fights).
    /// </summary>
    public string? SelfChampion { get; init; }

    /// <summary>
    /// How often, in video seconds, the coach is asked about the moment
    /// itself (which buttons to press). One such question is in flight at a
    /// time, so the model's own latency paces it too; this floor keeps a fast
    /// replay inside the request budget. Events are asked about as they
    /// come, on top of this.
    /// </summary>
    public double AskEverySeconds { get; init; } = 0.25;

    /// <summary>
    /// A yes below this probability is a no. The model's yes/no answers are
    /// calibrated probabilities, and the ghost acts on a yes with a margin,
    /// because a key pressed or a step taken on a coin flip is not coaching.
    /// On the fixture, a button the coach would throw sits at 0.55–0.85 and
    /// one it would hold at 0.1–0.4, with a press dropping the next answer
    /// to about 0.35 (the coach is told what it just did); a step it owes
    /// sits at 0.75–0.86 and one it does not at 0.06–0.3.
    /// </summary>
    public double YesAt { get; init; } = 0.6;

    /// <summary>How long a glance holds before attention drifts home.</summary>
    public double DwellSeconds { get; init; } = 1.2;

    /// <summary>Per-frame fraction of the remaining distance covered gliding home.</summary>
    public double ReturnEase { get; init; } = 0.45;

    /// <summary>Cursor moves smaller than this are not worth recording.</summary>
    public double MinMovePx { get; init; } = 2;

    /// <summary>How many of the player's recent seen shots the coach is shown when asked about aim.</summary>
    public int RecentShots { get; init; } = 10;

    /// <summary>How many of its own recent actions the coach is reminded of.</summary>
    public int RecentActions { get; init; } = 8;
}

/// <summary>One question put to the coach model and what came back, for the audit log.</summary>
public sealed record Consultation(
    double VideoTime, string Occasion, Moment State, IReadOnlyDictionary<string, Question> Questions,
    SystemOneResponse? Response, string? Error, long ElapsedMs);

/// <summary>
/// The coach: every coaching decision -- where to look, which button to
/// press, whether and which way to step, whether a shot or a bolt is worth
/// a word -- is a question put to Jev, TypeSafe's System One model, and
/// answered as a probability, a level or an option. Nothing here decides; it
/// measures, asks, and turns the answer into the output the reactor already
/// knows how to log, stream, trace and record.
///
/// <para><b>What stays in code, and why.</b> Perception: which row is the
/// player (<c>--self</c> or the is_self majority, with the pipeline's identity
/// corrections applied), how long each enemy has been in view, which buttons
/// the HUD has shown a cooldown for and when they come back, the last few
/// shots seen at a target, the last bolt that landed. Geometry: world units to
/// minimap pixels, the two sides of a bolt's line, which way is toward home.
/// The cursor's motor: a glance holds for a dwell and then glides back to the
/// player's own blip. Fair play: <see cref="Moment"/> is built from visible
/// rows only, so the model is never shown a thing in fog. Each of those is a
/// measurement or a mechanism, not a judgement; the judgements are in
/// <see cref="CoachQuestions"/>.</para>
///
/// <para><b>Asking.</b> The model answers in about a tenth of a second, so
/// questions are sent and the answers collected: <see cref="OnFrame"/> and
/// <see cref="OnEvent"/> never wait on the network. An answer is applied on
/// the next call, on the reactor's thread, stamped with the video time of
/// the moment it was asked about -- so the trace and the log carry the
/// coach's timing, and the live ghost runs the model's latency behind it.
/// Answers from before a <see cref="Resync"/> are dropped: they are about a
/// past the reactor has stopped trusting. A failure to answer is said once
/// as a cue and coaching goes on; the next question is asked as usual.</para>
///
/// <para>The only I/O is the injected <see cref="IJevClient"/>, so a replayed
/// timeline with a scripted client exercises this exactly; with the real
/// one, the same timeline is a real coaching run.</para>
/// </summary>
public sealed class JevPolicy(
    IJevClient jev, MinimapRect minimap, JevOptions? options = null, Action<Consultation>? audit = null) : IPolicy
{
    /// <summary>A question about the moment itself is not retried: the next frame asks again.</summary>
    private static readonly RequestOptions NowRequest = new()
    {
        Retry = RetryPolicy.None, Timeout = TimeSpan.FromSeconds(1),
    };

    /// <summary>A question about an event is one-shot, so it gets the client's retries.</summary>
    private static readonly RequestOptions OccasionRequest = new() { Timeout = TimeSpan.FromSeconds(2) };

    private sealed record Glance(ushort X, ushort Y, double UntilVideoTime, int Priority);

    private readonly JevOptions _options = options ?? new JevOptions();

    // The feed's capabilities.
    private ScreenMap? _map;
    private bool _hasLiveness;

    // Perception: identity.
    private FrameEnvelope? _frame;
    private readonly Dictionary<string, int> _selfVotes = [];
    private string? _selfName;
    private (double VideoTime, string Champion, string Replaces, int Moved, bool RenamedSelf)? _lastCorrection;

    // Perception: what has been seen, and for how long.
    private readonly Dictionary<int, double> _visibleSince = [];
    private readonly Dictionary<int, double> _seenFor = [];
    private readonly Dictionary<int, double> _withinReachSince = [];
    private readonly Dictionary<string, (double At, int? Countdown)> _casts = [];
    private double? _resource, _health;
    private readonly Queue<ShotFact> _shots = new();
    private (double Arrival, int? Damage)? _lastLanding;
    private int? _alliesDead;
    private readonly List<(string Did, double At)> _recent = [];

    // The cursor's motor.
    private Glance? _glance;
    private (double X, double Y)? _cursor;
    private (ushort X, ushort Y)? _snap;

    // What the coach said since the last drain.
    private readonly List<GlanceNote> _notes = [];
    private readonly List<CoachCue> _cues = [];
    private readonly List<KeyPress> _keys = [];
    private readonly List<MoveStep> _moves = [];

    // Questions in flight. Answers land here from the client's thread and are
    // applied on the reactor's, in Settle.
    private readonly ConcurrentQueue<Action> _arrivals = new();
    private int _generation;
    private bool _asking;
    private double _lastAskAt = double.NegativeInfinity;
    private bool _failing;

    public void Configure(Meta meta)
    {
        _map = ScreenMap.FromMeta(meta, minimap);
        _hasLiveness = meta.HasLiveness;

        // A false flag means the stage did not run, not that nothing happened.
        // Without it the questions that need it are never asked, so say why,
        // once, rather than be a silent coach that looks broken.
        List<string> missing = [];
        if (!meta.HasAbilities)
            missing.Add("ability HUD (no button will be pressed)");
        if (!meta.HasThreats)
            missing.Add("threats (no step will be taken)");
        if (!meta.HasSkillshots)
            missing.Add("skillshots (aim will not be remarked on)");
        if (missing.Count > 0)
            _cues.Add(new CoachCue(0, 1,
                $"this feed carries no {string.Join(", ", missing)}; spectral-sight needs a --coach run"));
    }

    public IReadOnlyList<GlanceNote> DrainNotes() => Drain(_notes);

    public IReadOnlyList<CoachCue> DrainCues() => Drain(_cues);

    public IReadOnlyList<KeyPress> DrainKeys() => Drain(_keys);

    public IReadOnlyList<MoveStep> DrainMoves() => Drain(_moves);

    private static IReadOnlyList<T> Drain<T>(List<T> list)
    {
        if (list.Count == 0)
            return [];
        var drained = list.ToArray();
        list.Clear();
        return drained;
    }

    public void Resync(FrameEnvelope? latest)
    {
        // Every answer still on its way is about a past we have stopped
        // trusting; the generation check in Settle drops it.
        _generation++;
        _asking = false;
        _lastAskAt = double.NegativeInfinity;

        _frame = latest;
        _glance = null;
        _snap = null;
        _alliesDead = latest?.AlliesDead;
        // Visible spells restart from the baseline: a span that straddles a
        // gap is a claim about frames we never saw. Cooldowns, shots, landings
        // and the coach's own memory go with them: a gap may be a new game,
        // and any of those straddling two games is wrong in the way that
        // matters. Re-learning a cooldown costs one cast per slot.
        _visibleSince.Clear();
        _seenFor.Clear();
        _withinReachSince.Clear();
        _casts.Clear();
        _resource = null;
        _health = null;
        _shots.Clear();
        _lastLanding = null;
        _recent.Clear();
        if (latest is not null)
            foreach (var row in latest.Champions.Where(c => c.Visible))
                _visibleSince[row.TrackId] = latest.VideoTime;
        // _cursor survives: the physical pointer is wherever we last put it.
        // _selfVotes survive too: identity outlives a gap.
    }

    public GhostCursor? OnFrame(FrameEnvelope frame)
    {
        Settle();
        _frame = frame;
        VoteSelf(frame);
        TrackVisibility(frame);
        var self = Self();
        if (self is not null)
        {
            if (self.Resource is { } resource)
                _resource = resource;
            if (self.Health is { } health)
                _health = health;
            TrackReach(frame, self);
        }

        // The counter is the fallback for feeds that cannot corroborate
        // liveness; with liveness, the death *event* names the casualty and
        // carries the count, so the guess below would only double-announce.
        if (_map is not null && !_hasLiveness && frame.AlliesDead is { } dead)
        {
            var rising = dead > (_alliesDead ?? dead);
            _alliesDead = dead;
            // An allied death is announced to the player (minimap indicator,
            // death recap), so looking where it happened is fair play.
            if (rising && self is not null && FallenAlly(self) is { } fallen)
                AskLook(frame.VideoTime, self,
                    new AllyOccasion("the HUD counted another ally death", fallen.Champion ?? "?",
                        Distance(self, fallen), null),
                    _map.WorldToScreen(fallen.WorldX!.Value, fallen.WorldY!.Value),
                    $"ally down, likely {fallen.Champion ?? "?"}");
        }

        if (self is not null)
            AskNow(frame, self);

        Settle();
        if (TakeSnap() is { } snap)
            return snap;
        if (_map is null)
            return null;
        if (_glance is { } glance && frame.VideoTime < glance.UntilVideoTime)
            return null;   // holding the look
        _glance = null;
        if (self is { WorldX: { } wx, WorldY: { } wy })
            return GlideToward(_map.WorldToScreen(wx, wy));
        return null;
    }

    public GhostCursor? OnEvent(GameEvent evt)
    {
        Settle();
        switch (evt.Kind)
        {
            case EventKind.Identified:
                OnIdentified(evt);
                break;
            case EventKind.Ability:
                OnAbility(evt);
                break;
            case EventKind.Threat:
                OnThreat(evt);
                break;
            case EventKind.Skillshot:
                OnSkillshot(evt);
                break;
            default:
                OnLookEvent(evt);
                break;
        }
        Settle();
        return TakeSnap();
    }

    // --- The moment itself: which button a good player would press now ---

    /// <summary>
    /// Asks about the buttons that are up, when there is anything to ask: a
    /// player who is alive, a button the HUD has shown come back, and an
    /// enemy on the screen to throw it at. A dead player, a greyed button and
    /// an empty screen are not questions. One question in flight at a time,
    /// and no more often than <see cref="JevOptions.AskEverySeconds"/>.
    /// </summary>
    private void AskNow(FrameEnvelope frame, ChampionRow self)
    {
        if (_asking || frame.VideoTime - _lastAskAt < _options.AskEverySeconds)
            return;
        if (self.Alive == false || self is not { WorldX: not null, WorldY: not null })
            return;
        var slots = _casts
            .Where(c => c.Value.Countdown is { } countdown && frame.VideoTime >= c.Value.At + countdown)
            .Select(c => c.Key)
            .Order()
            .ToArray();
        if (slots.Length == 0)
            return;
        var moment = Describe(frame, self, occasion: null, frame.VideoTime);
        if (moment.VisibleEnemies.Count == 0)
            return;

        _asking = true;
        _lastAskAt = frame.VideoTime;
        var asked = frame.VideoTime;
        Ask("now", moment, CoachQuestions.Press(slots), asked, NowRequest, now: true, answered: response =>
        {
            var nearest = moment.VisibleEnemies[0];
            foreach (var slot in slots)
            {
                if (!response.TryGet<NoulAnswer>($"press_{slot}", out var answer) || !answer!.IsYes(_options.YesAt))
                    continue;
                var facts = moment.Abilities.First(a => a.Slot == slot);
                var reason = facts.Range is { } range && nearest.DistanceUnits <= range
                    ? $"{nearest.Champion} has been in {slot} range ({nearest.DistanceUnits:0} units)"
                      + (nearest.WithinReachForSeconds is { } held ? $" for {held:0.0}s" : "")
                      + $" with {slot} up"
                    : $"{nearest.Champion} is {nearest.DistanceUnits:0} units away with {slot} up";
                _keys.Add(new KeyPress(asked, slot, 2, reason));
                Remember($"pressed {slot}", asked);
            }
        });
    }

    /// <summary>
    /// The player's own cast, named to a button off the HUD: the one fact that
    /// tells us a slot exists and when it comes back. A cast whose countdown
    /// could not be read leaves the slot unknown until the next one that can.
    /// </summary>
    private void OnAbility(GameEvent evt)
    {
        if (evt.Slot is not { } slot || evt.At is not { } at)
            return;
        if (evt.Countdown is { } countdown)
            _casts[slot] = (at, countdown);
        else
            _casts.Remove(slot);
    }

    // --- A bolt at the player: a remark, and a step ---

    private void OnThreat(GameEvent evt)
    {
        var landing = evt.Arrival ?? evt.VideoTime;
        var previous = _lastLanding;
        _lastLanding = (landing, evt.Damage);

        var heading = evt.Heading is [var ux, var uy] && double.Hypot(ux, uy) > 0 ? (ux, uy) : ((double, double)?)null;
        var from = heading is { } h ? ScreenDirections.Source(h.Item1, h.Item2) : null;
        var outcome = evt.Outcome ?? "unknown";
        var occasion = new BoltOccasion(
            "a bolt came at you", from, outcome, evt.Damage, evt.MovedAcross,
            evt.At is { } at && evt.Arrival is { } arrival ? Math.Round(arrival - at, 2) : null,
            previous is { } p ? Math.Round(landing - p.Arrival, 2) : null,
            previous?.Damage);
        var self = Self();
        var moment = Describe(_frame, self, occasion, evt.VideoTime);

        var questions = new Questions().Noul("remark", CoachQuestions.BoltRemark);
        Dictionary<string, (double Dx, double Dy)>? sides = null;
        if (heading is { } line)
        {
            var (a, b) = ScreenDirections.Across(line.Item1, line.Item2);
            sides = new() { [ScreenDirections.Name(a.Dx, a.Dy)] = a, [ScreenDirections.Name(b.Dx, b.Dy)] = b };
            var criteria = new ChoiceCriteria();
            foreach (var (name, step) in sides)
                criteria[name] = DescribeSide(step, self, moment.VisibleEnemies);
            questions.Noul("step", CoachQuestions.BoltStep).Choice("side", CoachQuestions.BoltSide, criteria);
        }

        var sentence = BoltSentence(occasion);
        var stamp = evt.At ?? evt.VideoTime;
        Ask("bolt", moment, questions, evt.VideoTime, OccasionRequest, now: false, answered: response =>
        {
            if (response.TryGet<NoulAnswer>("remark", out var remark) && remark!.IsYes(_options.YesAt))
            {
                _cues.Add(new CoachCue(evt.VideoTime, 3, sentence));
                Remember("remarked on a bolt", evt.VideoTime);
            }
            if (sides is null
                || !response.TryGet<NoulAnswer>("step", out var step) || !step!.IsYes(_options.YesAt)
                || !response.TryGet<ChoiceAnswer>("side", out var side) || !sides.TryGetValue(side!.Choice, out var way))
                return;
            _moves.Add(new MoveStep(stamp, side.Choice, way.Dx, way.Dy, 3, sentence));
            Remember($"stepped {side.Choice}", stamp);
        });
    }

    /// <summary>
    /// A side of the bolt's line, described by what the code can measure
    /// about it: toward home or not, and toward or away from the nearest
    /// enemy on the screen. Which of the two to take is the model's call.
    /// </summary>
    private string DescribeSide((double Dx, double Dy) step, ChampionRow? self, IReadOnlyList<EnemyFacts> enemies)
    {
        var home = ScreenDirections.TowardBase(step.Dx, step.Dy) switch
        {
            > 0.2 => "toward the player's own base",
            < -0.2 => "away from the player's own base",
            _ => "neither toward nor away from the player's own base",
        };
        if (enemies.Count == 0 || self is not { WorldX: { } sx, WorldY: { } sy })
            return $"across the bolt's line, {home}";
        var nearest = Visible(_frame, self).OrderBy(r => Distance(self, r)).First();
        // World y grows north, screen y grows down: flip to compare with the step.
        var ex = nearest.WorldX!.Value - sx;
        var ey = -(nearest.WorldY!.Value - sy);
        var length = double.Hypot(ex, ey);
        var toward = length == 0 ? 0 : (step.Dx * ex + step.Dy * ey) / length;
        var enemy = toward switch
        {
            > 0.2 => $"toward the nearest visible enemy ({enemies[0].Champion})",
            < -0.2 => $"away from the nearest visible enemy ({enemies[0].Champion})",
            _ => $"neither toward nor away from the nearest visible enemy ({enemies[0].Champion})",
        };
        return $"across the bolt's line, {home}, and {enemy}";
    }

    /// <summary>
    /// What the player reads about a bolt: what came at them, from where,
    /// what it did, and what they were doing. It is "a bolt" and never an
    /// ability, because a threat is any bolt launched at their plate and a
    /// ranged auto-attack qualifies as readily as a skillshot; and the
    /// warning is stated, never judged.
    /// </summary>
    private static string BoltSentence(BoltOccasion bolt)
    {
        var from = bolt.From is { } source ? $" from {source}" : "";
        var did = bolt.Outcome switch
        {
            "hit" => $"hit you{(bolt.Damage is { } d ? $" for {d}" : "")}",
            "dodged" => "missed you",
            _ => "came at you",
        };
        var motion = bolt.MovedAcrossPx switch
        {
            null => "",
            < 5 => " while you stood still",
            var px => $" while you moved {px:0}px across its line",
        };
        var warning = bolt.WarningSeconds is { } w ? $", {w:0.00}s after it came into view" : "";
        return $"a bolt{from} {did}{motion}{warning}";
    }

    // --- A shot of the player's: a word about aim ---

    private void OnSkillshot(GameEvent evt)
    {
        // No `miss` means nothing was seen to judge: the stage saw no bolt
        // leave the player's model (two thirds of casts), or the bolt flew
        // with no enemy on screen in front of it. Not a fact about the player.
        if (evt.Miss is not { } miss || evt.Slot is not { } slot)
            return;
        var wide = evt.Outcome == "missed";
        var passed = Math.Round(miss, MidpointRounding.AwayFromZero);
        _shots.Enqueue(new ShotFact(slot, passed, wide));
        while (_shots.Count > _options.RecentShots)
            _shots.Dequeue();
        var recent = _shots.ToArray();

        // The side comes from `lead`, whose sign is unvalidated upstream.
        var side = evt.Lead switch { > 0 => "ahead of them", < 0 => "behind them", _ => "wide of them" };
        var occasion = new ShotOccasion(
            "you threw a skillshot at a visible enemy", slot, passed, side, wide, recent);
        var moment = Describe(_frame, Self(), occasion, evt.VideoTime);
        var wideCount = recent.Count(s => s.Wide);
        // The denominator is the shots that were *seen*, and the copy says
        // so; "of N casts" would be wrong by a factor of three. A wide shot
        // is where the bolt passed, never "you missed": the verdict upstream
        // is geometric, against a hit radius nothing has justified.
        var sentence = $"{slot} passed {passed}px {side}; "
            + $"{wideCount} of the last {recent.Length} shots that were seen went wide";

        Ask("shot", moment, new Questions().Noul("remark", CoachQuestions.ShotRemark),
            evt.VideoTime, OccasionRequest, now: false, answered: response =>
            {
                if (!response.TryGet<NoulAnswer>("remark", out var remark) || !remark!.IsYes(_options.YesAt))
                    return;
                _cues.Add(new CoachCue(evt.VideoTime, 2, sentence));
                Remember("remarked on aim", evt.VideoTime);
            });
    }

    // --- Attention: what on the screen deserves a look ---

    private void OnLookEvent(GameEvent evt)
    {
        if (_map is null || Self() is not { } self || evt.Team is not { } team)
            return;
        if (team == self.Team)
            OnAllyEvent(evt, self);
        else if (evt.Kind == EventKind.Vanished)
            OnEnemyVanished(evt, self);
        else
            OnEnemyEvent(evt, self, team);
    }

    /// <summary>
    /// Own-team events. An ally's death and respawn are announced to the
    /// player (kill banner, portrait timer), and own-team positions are
    /// always on the player's own minimap, so unlike enemies no visibility
    /// gate applies. The player's own death and respawn are nothing to look
    /// at: they lived it.
    /// </summary>
    private void OnAllyEvent(GameEvent evt, ChampionRow self)
    {
        // Even without a place to look, the event's count supersedes the
        // frame counter heuristic for this death: never announce it twice.
        if (evt.Kind == EventKind.Death && evt.AlliesDead is { } counted)
            _alliesDead = Math.Max(_alliesDead ?? counted, counted);

        // By track first; by name when the track is gone -- a corpse's track
        // is often dropped before the frame that reaches us (latest wins),
        // and deaths are keyed by champion upstream for the same reason.
        var row = _frame?.Champions.FirstOrDefault(c =>
            c.Team == self.Team && c is { WorldX: not null, WorldY: not null }
            && (c.TrackId == evt.TrackId || (evt.Champion is not null && c.Champion == evt.Champion)));
        var who = evt.Champion ?? row?.Champion;
        if (row is null || who == self.Champion)
            return;

        var point = _map!.WorldToScreen(row.WorldX!.Value, row.WorldY!.Value);
        var distance = Distance(self, row);
        switch (evt.Kind)
        {
            case EventKind.Death:
                AskLook(evt.VideoTime, self, new AllyOccasion("an ally died", who ?? "?", distance, null),
                    point, $"ally {who ?? "?"} down");
                break;
            case EventKind.Respawn:
                AskLook(evt.VideoTime, self, new AllyOccasion("an ally respawned", who ?? "?", distance, evt.DownFor),
                    point, evt.DownFor is { } downFor
                        ? $"ally {who ?? "?"} back up after {downFor:0}s"
                        : $"ally {who ?? "?"} back up");
                break;
        }
    }

    /// <summary>
    /// Fair play: an enemy is asked about only when the player can currently
    /// see them. A champion in fog -- its last position, how long it has been
    /// missing, a level or cast sensed through the fog -- is information the
    /// player does not have, so it is resolved from the <em>current</em> row
    /// and only when that row is visible. (Enemy death and respawn never
    /// arrive: liveness is HUD-corroborated and only allies have HUD panels.)
    /// </summary>
    private void OnEnemyEvent(GameEvent evt, ChampionRow self, string team)
    {
        var row = _frame?.Champions.FirstOrDefault(c =>
            c.TrackId == evt.TrackId && c.Team == team && c.Visible
            && c is { WorldX: not null, WorldY: not null });
        if (row is null || Distance(self, row) is not { } distance)
            return;

        var who = evt.Champion ?? row.Champion ?? $"track {evt.TrackId}";
        var direction = Direction(self, row)!;
        var point = _map!.WorldToScreen(row.WorldX!.Value, row.WorldY!.Value);
        var (occasion, reason) = evt.Kind switch
        {
            EventKind.Cast => (
                new EnemyOccasion("a visible enemy cast an ability", who, distance, direction, null, null),
                $"{who} cast nearby"),
            EventKind.LevelUp when evt.Level is { } level => (
                new EnemyOccasion("a visible enemy levelled up", who, distance, direction, level, null),
                $"{who} reached {level} nearby"),
            EventKind.Reappeared => (
                new EnemyOccasion("an enemy came back into your view", who, distance, direction, null, evt.GoneFor),
                $"{who} back in your view"),
            _ => (null, null),
        };
        if (occasion is not null)
            AskLook(evt.VideoTime, self, occasion, point, reason!);
    }

    /// <summary>
    /// The one exception to the visible-row rule, because the vanish
    /// <em>moment</em> is the player's own information: the blip sat on
    /// their minimap until seconds ago and they watched it fade -- or should
    /// have, which is the coaching point. The event carries where that was.
    /// Everything after the moment stays out of bounds: one look, then no
    /// recheck and no drift back. Whether the fade is worth a look -- a solid
    /// spell in view against a flicker at the vision edge, a fresh fade
    /// against a stale one, a new call against a repeat -- is the model's
    /// judgement, from the measurements here.
    /// </summary>
    private void OnEnemyVanished(GameEvent evt, ChampionRow self)
    {
        if (evt is not { WorldX: { } worldX, WorldY: { } worldY, TrackId: { } trackId })
            return;
        // The fade predates the event by the tracker's debounce, carried on
        // the row as seconds_since_seen.
        var row = _frame?.Champions.FirstOrDefault(c => c.TrackId == trackId);
        var fadedAgo = row?.SecondsSinceSeen ?? 0;
        var fadeAt = evt.VideoTime - fadedAgo;
        // The spell may still be open here when this event outruns the frame
        // that closes it, so measure from whichever record exists.
        var seenFor = _visibleSince.TryGetValue(trackId, out var since)
            ? fadeAt - since
            : _seenFor.GetValueOrDefault(trackId);
        var who = evt.Champion ?? row?.Champion ?? $"track {trackId}";
        double? distance = self is { WorldX: { } sx, WorldY: { } sy }
            ? Math.Round(double.Hypot(worldX - sx, worldY - sy))
            : null;
        var direction = self is { WorldX: { } x, WorldY: { } y }
            ? ScreenDirections.NameOfWorldOffset(worldX - x, worldY - y)
            : null;
        var occasion = new FadeOccasion("an enemy faded from your minimap", who,
            Math.Round(Math.Max(seenFor, 0), 1), Math.Round(fadedAgo, 1), distance, direction);
        AskLook(evt.VideoTime, self, occasion, _map!.WorldToScreen(worldX, worldY), $"{who} missing",
            looked: () => Remember($"called {who} missing", evt.VideoTime));
    }

    /// <summary>
    /// Asks how much an event deserves a glance. The most likely level is the
    /// glance's priority; level 0 is no glance. The most likely level and not
    /// the probability-weighted score: a fade the model puts at "not worth a
    /// look" with some weight on the levels above averages to half a level,
    /// and rounding that up would send the ghost to every flicker at the
    /// vision edge, which is the noise the rubric is there to keep it from.
    /// </summary>
    private void AskLook(double videoTime, ChampionRow self, object occasion, (ushort X, ushort Y) point,
        string reason, Action? looked = null)
    {
        var moment = Describe(_frame, self, occasion, videoTime);
        var questions = new Questions().Score("look", CoachQuestions.Look, CoachQuestions.LookLevels);
        Ask("look", moment, questions, videoTime, OccasionRequest, now: false, answered: response =>
        {
            if (!response.TryGet<ScoreAnswer>("look", out var look) || look!.MostLikely.Index < 1)
                return;
            if (SnapTo(point, videoTime, Math.Min(3, look.MostLikely.Index), reason))
                looked?.Invoke();
        });
    }

    // --- Asking, and collecting the answers ---

    /// <summary>
    /// Sends one question set and collects the answer without waiting for it.
    /// The continuation only queues work; every touch of this policy's state
    /// happens in <see cref="Settle"/>, on the caller's thread.
    /// </summary>
    private void Ask(string occasion, Moment moment, Questions questions, double videoTime,
        RequestOptions request, bool now, Action<SystemOneResponse> answered)
    {
        var generation = _generation;
        _ = RunAsync();

        async Task RunAsync()
        {
            var clock = Stopwatch.StartNew();
            SystemOneResponse? response = null;
            string? error = null;
            try
            {
                response = await jev.SystemOneAsync(moment, questions, model: null, request);
            }
            catch (Exception e)
            {
                error = e.Message;
            }
            var elapsed = clock.ElapsedMilliseconds;
            _arrivals.Enqueue(() =>
            {
                if (now && generation == _generation)
                    _asking = false;
                audit?.Invoke(new Consultation(videoTime, occasion, moment, questions, response, error, elapsed));
                if (generation != _generation)
                    return;
                if (response is null)
                {
                    Fail(videoTime, error ?? "no answer");
                    return;
                }
                _failing = false;
                try
                {
                    answered(response);
                }
                catch (Exception e)
                {
                    Fail(videoTime, $"the answer could not be read: {e.Message}");
                }
            });
        }
    }

    private void Settle()
    {
        while (_arrivals.TryDequeue(out var apply))
            apply();
    }

    /// <summary>Said once per outage: the first failure, and not again until an answer has come back.</summary>
    private void Fail(double videoTime, string why)
    {
        if (_failing)
            return;
        _failing = true;
        _cues.Add(new CoachCue(videoTime, 1, $"the coach model did not answer: {why}"));
    }

    private void Remember(string did, double at)
    {
        _recent.Add((did, at));
        while (_recent.Count > _options.RecentActions)
            _recent.RemoveAt(0);
    }

    // --- Describing the moment ---

    /// <summary>
    /// The state the model is shown. Visible enemy rows only, own team
    /// always, the player's own HUD readings, and what the coach did lately.
    /// </summary>
    private Moment Describe(FrameEnvelope? frame, ChampionRow? self, object? occasion, double now)
    {
        var champion = self?.Champion;
        List<SlotFacts> abilities = [];
        foreach (var slot in AbilityKits.Slots.Union(_casts.Keys).Order())
        {
            var note = AbilityKits.For(champion, slot);
            if (_casts.TryGetValue(slot, out var cast) && cast.Countdown is { } countdown)
            {
                var ready = cast.At + countdown;
                abilities.Add(now >= ready
                    ? new SlotFacts(slot, note?.Kind, note?.Range, "up", Math.Round(now - ready, 1), null, null)
                    : new SlotFacts(slot, note?.Kind, note?.Range, "cooldown", null, Math.Round(ready - now, 1), null));
            }
            else
                abilities.Add(new SlotFacts(slot, note?.Kind, note?.Range, "unknown", null, null,
                    _casts.ContainsKey(slot)
                        ? "the last cast's cooldown could not be read"
                        : "never seen cast; it may not be skilled yet"));
        }

        List<EnemyFacts> enemies = [];
        List<AllyFacts> allies = [];
        if (frame is not null && self is not null)
        {
            foreach (var row in Visible(frame, self).OrderBy(r => Distance(self, r)))
            {
                var distance = Distance(self, row)!.Value;
                var inRange = AbilityKits.Slots
                    .Where(s => AbilityKits.For(champion, s)?.Range is { } range && distance <= range)
                    .ToArray();
                enemies.Add(new EnemyFacts(
                    row.Champion ?? $"track {row.TrackId}", distance, Direction(self, row)!,
                    Math.Round(now - _visibleSince.GetValueOrDefault(row.TrackId, now), 1),
                    row.Health, row.Level, inRange,
                    _withinReachSince.TryGetValue(row.TrackId, out var since) ? Math.Round(now - since, 1) : null));
            }
            foreach (var row in frame.Champions.Where(c => c.Team == self.Team && c.TrackId != self.TrackId))
                allies.Add(new AllyFacts(row.Champion ?? $"track {row.TrackId}", row.Alive, Distance(self, row)));
        }

        return new Moment
        {
            VideoTime = now,
            GameClock = frame?.GameTime is { } seconds ? $"{seconds / 60}:{seconds % 60:00}" : null,
            Player = self is null ? null : new PlayerFacts(
                self.Champion ?? "?", self.Team, self.Alive != false, _health, _resource, self.Level),
            Abilities = abilities,
            VisibleEnemies = enemies,
            Allies = allies,
            Coach = _recent.Select(r => new RecentAction(r.Did, Math.Round(now - r.At, 1))).ToArray(),
            Occasion = occasion,
        };
    }

    /// <summary>The enemies the player can see, with a place on the map. Nothing in fog is ever listed.</summary>
    private static IEnumerable<ChampionRow> Visible(FrameEnvelope? frame, ChampionRow self) =>
        frame?.Champions.Where(c => c.Team != self.Team && c.Visible && c is { WorldX: not null, WorldY: not null })
        ?? [];

    private static double? Distance(ChampionRow self, ChampionRow row) =>
        self is { WorldX: { } sx, WorldY: { } sy } && row is { WorldX: { } x, WorldY: { } y }
            ? Math.Round(double.Hypot(x - sx, y - sy))
            : null;

    private static string? Direction(ChampionRow self, ChampionRow row) =>
        self is { WorldX: { } sx, WorldY: { } sy } && row is { WorldX: { } x, WorldY: { } y }
            ? ScreenDirections.NameOfWorldOffset(x - sx, y - sy)
            : null;

    // --- Perception ---

    /// <summary>
    /// Visible-spell bookkeeping: when each track's current spell began, and
    /// how long its last completed one ran. The spell closes at the last
    /// sighting (frame time less <c>seconds_since_seen</c>), not at the
    /// debounced frame that reports it.
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

    /// <summary>How long each visible enemy has been inside the player's longest known range.</summary>
    private void TrackReach(FrameEnvelope frame, ChampionRow self)
    {
        if (AbilityKits.Reach(self.Champion) is not { } reach)
            return;
        var within = new HashSet<int>();
        foreach (var row in Visible(frame, self))
        {
            if (Distance(self, row) is { } distance && distance <= reach)
            {
                within.Add(row.TrackId);
                _withinReachSince.TryAdd(row.TrackId, frame.VideoTime);
            }
        }
        foreach (var track in _withinReachSince.Keys.Where(t => !within.Contains(t)).ToArray())
            _withinReachSince.Remove(track);
    }

    /// <summary>A new majority must strictly overtake the incumbent, so ties never flap.</summary>
    private void VoteSelf(FrameEnvelope frame)
    {
        if (_options.SelfChampion is not null)
            return;
        if (frame.Champions.FirstOrDefault(c => c.IsSelf)?.Champion is not { } flagged)
            return;
        _selfVotes[flagged] = _selfVotes.GetValueOrDefault(flagged) + 1;
        if (_selfName is null || _selfVotes[flagged] > _selfVotes.GetValueOrDefault(_selfName))
            _selfName = flagged;
    }

    /// <summary>
    /// Identity bookkeeping, never a glance. When the pipeline renames a track
    /// it announces the correction with <c>replaces</c>; votes earned under the
    /// old name belong to the new one, or a corrected self would go
    /// unrecognized until the majority re-accumulated from scratch.
    /// A repaired crossing swap arrives as a mutual pair at one instant --
    /// "X correcting Y" then "Y correcting X" -- which migration alone gets
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

    private ChampionRow? Self() => (_options.SelfChampion ?? _selfName) is { } name
        ? _frame?.Champions.FirstOrDefault(c => c.Champion == name)
        : _frame?.Champions.FirstOrDefault(c => c.IsSelf);

    /// <summary>
    /// The HUD counted a new ally death without naming the casualty (liveness
    /// often cannot); the ally most recently lost from the minimap is the best
    /// guess for where to look. This is own-team information the player already
    /// has, paired with a death the game announces.
    /// </summary>
    private ChampionRow? FallenAlly(ChampionRow self) =>
        _frame!.Champions
            .Where(c => c.Champion != self.Champion && c.Team == self.Team
                && c.Alive != true && !c.Visible
                && c is { WorldX: not null, WorldY: not null })
            .OrderBy(c => c.SecondsSinceSeen)
            .FirstOrDefault();

    // --- The cursor's motor ---

    /// <summary>
    /// Points the ghost at something, unless a look of higher priority is
    /// still being held. The move itself is emitted by the next
    /// <see cref="TakeSnap"/>, which is the end of whichever call settled the
    /// answer.
    /// </summary>
    private bool SnapTo((ushort X, ushort Y) point, double videoTime, int priority, string reason)
    {
        if (_glance is { } held && videoTime < held.UntilVideoTime && priority < held.Priority)
            return false;
        _glance = new Glance(point.X, point.Y, videoTime + _options.DwellSeconds, priority);
        _notes.Add(new GlanceNote(videoTime, point.X, point.Y, priority, reason));
        _snap = point;
        return true;
    }

    private GhostCursor? TakeSnap()
    {
        if (_snap is not { } point)
            return null;
        _snap = null;
        return Emit(point.X, point.Y);
    }

    private GhostCursor? GlideToward((ushort X, ushort Y) home)
    {
        if (_cursor is not { } cursor)
            return Emit(home.X, home.Y);
        var stepX = (home.X - cursor.X) * _options.ReturnEase;
        var stepY = (home.Y - cursor.Y) * _options.ReturnEase;
        if (Math.Abs(stepX) + Math.Abs(stepY) < _options.MinMovePx)
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
}
