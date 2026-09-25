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

    /// <summary>
    /// How often, in video seconds, a player who stands on one spot is asked
    /// about: should they be walking to lane? It is also the least time on
    /// the spot before the first such question, since a player who stopped a
    /// moment ago is not yet standing still. One in flight at a time, apart
    /// from the button questions.
    /// </summary>
    public double IdleAskEverySeconds { get; init; } = 3;

    /// <summary>
    /// How far, in game units, the player's model can drift and still be on
    /// the same spot. The minimap read jitters by tens of units on a
    /// champion that has not moved; a walking one covers this in a third of
    /// a second.
    /// </summary>
    public double StillRadiusUnits { get; init; } = 100;

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
/// The coach: every coaching decision -- which button to press, whether and
/// which way to step, whether a shot or a bolt is worth a word, which
/// ability a new level's point goes into -- is a
/// question put to Jev, TypeSafe's System One model, and
/// answered as a probability, a level or an option. Nothing here decides; it
/// measures, asks, and turns the answer into the output the reactor already
/// knows how to log, stream, trace and record.
///
/// <para><b>What stays in code, and why.</b> Perception: which row is the
/// player (<c>--self</c> or the is_self majority, with the pipeline's identity
/// corrections applied), how long each enemy has been in view, which buttons
/// the HUD has shown a cooldown for and when they come back, the last few
/// shots seen at a target, the last bolt that landed, how long the player
/// has stood on one spot, the highest level a point has been asked about
/// at. Rules of the game: the ultimate takes a point at 6, 11 and 16 and at
/// no other level. Geometry: the two sides of a bolt's line, which way
/// is toward home, which way on the screen an enemy is, where on the map the
/// player stands and how far each lane is (<see cref="RiftMap"/>). Fair
/// play: <see cref="Moment"/> is built from visible
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
public sealed class JevPolicy(IJevClient jev, JevOptions? options = null, Action<Consultation>? audit = null) : IPolicy
{
    /// <summary>A question about the moment itself is not retried: the next frame asks again.</summary>
    private static readonly RequestOptions NowRequest = new()
    {
        Retry = RetryPolicy.None, Timeout = TimeSpan.FromSeconds(1),
    };

    /// <summary>A question about an event is one-shot, so it gets the client's retries.</summary>
    private static readonly RequestOptions OccasionRequest = new() { Timeout = TimeSpan.FromSeconds(2) };

    private readonly JevOptions _options = options ?? new JevOptions();

    // Perception: identity.
    private FrameEnvelope? _frame;
    private readonly Dictionary<string, int> _selfVotes = [];
    private string? _selfName;
    private (double VideoTime, string Champion, string Replaces, int Moved, bool RenamedSelf)? _lastCorrection;

    // Perception: what has been seen, and for how long.
    private readonly Dictionary<int, double> _visibleSince = [];
    private readonly Dictionary<int, double> _withinReachSince = [];
    private readonly Dictionary<string, (double At, int? Countdown)> _casts = [];
    private double? _resource, _health;
    private readonly Queue<ShotFact> _shots = new();
    private (double Arrival, int? Damage)? _lastLanding;
    private readonly List<(string Did, double At)> _recent = [];

    // Perception: the spot the player has stood on, and since when.
    private (double X, double Y)? _restAt;
    private double _stillSince;

    // Perception: the levels whose points have been asked about -- the first,
    // since which the coach has been watching, and the highest, since a level
    // only rises and a level-up at or below it is the same one read again (the
    // tracker's rows trade places under the name) -- and where the coach put
    // each point. Those are the coach's own placements, a lower bound on the
    // ability's points and never a reading of the HUD's rank pips, which the
    // feed does not carry.
    private int _firstLevelAsked, _highestLevelAsked;
    private readonly Dictionary<string, int> _pointsPlaced = [];

    // What the coach said since the last drain.
    private readonly List<CoachCue> _cues = [];
    private readonly List<KeyPress> _keys = [];
    private readonly List<MoveStep> _moves = [];

    // Questions in flight. Answers land here from the client's thread and are
    // applied on the reactor's, in Settle.
    private readonly ConcurrentQueue<Action> _arrivals = new();
    private int _generation;
    private bool _asking;
    private double _lastAskAt = double.NegativeInfinity;
    private bool _askingIdle;
    private double _lastIdleAskAt = double.NegativeInfinity;
    private bool _failing;

    // Questions on the wire, by occasion, oldest first. Touched from the
    // client's thread as well as the reactor's, so it has its own lock.
    private readonly List<string> _onTheWire = [];
    private readonly Lock _wireLock = new();

    /// <summary>
    /// The occasions ("now", "idle", "bolt", "shot", "level") of the questions
    /// on their way to the model, oldest first, raised each time that changes: when one
    /// is sent and when its answer or failure comes back. It follows the
    /// network, not <see cref="Settle"/>, so it is raised on whichever thread
    /// the answer arrived on, and a question whose answer a resync will drop
    /// is still counted until it is back. For a panel's "asking" light; it
    /// decides nothing.
    /// </summary>
    public event Action<IReadOnlyList<string>>? AskingChanged;

    public void Configure(Meta meta)
    {
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
        if (meta.WorldBounds is null)
            missing.Add("world calibration (nobody will be walked to lane)");
        if (!meta.HasNameplates)
            missing.Add("nameplates (no level-up will have its point placed)");
        if (missing.Count > 0)
            _cues.Add(new CoachCue(0, 1,
                $"this feed carries no {string.Join(", ", missing)}; spectral-sight needs a --coach run"));
    }

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
        _askingIdle = false;
        _lastIdleAskAt = double.NegativeInfinity;

        _frame = latest;
        // Visible spells restart from the baseline: a span that straddles a
        // gap is a claim about frames we never saw. Cooldowns, shots, landings
        // and the coach's own memory go with them: a gap may be a new game,
        // and any of those straddling two games is wrong in the way that
        // matters. Re-learning a cooldown costs one cast per slot.
        _visibleSince.Clear();
        _withinReachSince.Clear();
        _casts.Clear();
        _resource = null;
        _health = null;
        _shots.Clear();
        _lastLanding = null;
        _recent.Clear();
        _restAt = null;
        _firstLevelAsked = 0;
        _highestLevelAsked = 0;
        _pointsPlaced.Clear();
        if (latest is not null)
        {
            foreach (var row in latest.Champions.Where(c => c.Visible))
                _visibleSince[row.TrackId] = latest.VideoTime;
            // The spot restarts from the baseline too: standing still across
            // a gap is a claim about frames we never saw.
            if (Self() is { } self)
                TrackStillness(latest, self);
        }
        // _selfVotes survive: identity outlives a gap.
    }

    public void OnFrame(FrameEnvelope frame)
    {
        Settle();
        _frame = frame;
        VoteSelf(frame);
        TrackVisibility(frame);
        if (Self() is { } self)
        {
            if (self.Resource is { } resource)
                _resource = resource;
            if (self.Health is { } health)
                _health = health;
            TrackReach(frame, self);
            TrackStillness(frame, self);
            AskNow(frame, self);
            AskIdle(frame, self);
        }
        Settle();
    }

    public void OnEvent(GameEvent evt)
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
            case EventKind.LevelUp:
                OnLevelUp(evt);
                break;
        }
        Settle();
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
        Ask("now", moment, CoachQuestions.Press(slots), asked, NowRequest, released: () => _asking = false, answered: response =>
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

    // --- Standing still: would a good player be walking to lane? ---

    /// <summary>
    /// Asks about a player who has stood on one spot for the ask interval,
    /// when there is something to ask: alive, placed on the map, with a game
    /// clock running (before it the player cannot move). Whether standing
    /// there is idling, and which lane a good player would be walking to,
    /// are the model's calls; a yes is a step toward that lane's nearest
    /// point, and the ghost keeps stepping every interval until the player
    /// moves.
    /// </summary>
    private void AskIdle(FrameEnvelope frame, ChampionRow self)
    {
        if (_askingIdle || frame.VideoTime - _lastIdleAskAt < _options.IdleAskEverySeconds)
            return;
        if (self.Alive == false || _restAt is not { } rest || frame.GameTime is null)
            return;
        if (frame.VideoTime - _stillSince < _options.IdleAskEverySeconds)
            return;
        var moment = Describe(frame, self, occasion: null, frame.VideoTime);
        if (moment.Whereabouts is not { } whereabouts)
            return;

        var criteria = new ChoiceCriteria();
        foreach (var lane in whereabouts.Lanes)
        {
            var allies = lane.AlliesThere.Count == 0 ? "none" : string.Join(", ", lane.AlliesThere);
            criteria[lane.Lane] = lane.ScreenDirection is { } direction
                ? $"{lane.Lane} lane: {lane.DistanceUnits:0} units away, {direction} on the screen; allies there: {allies}"
                : $"{lane.Lane} lane: the player is standing in it; allies there: {allies}";
        }
        var questions = new Questions()
            .Noul("walk", CoachQuestions.Walk,
                yes: "they are idling off lane and a good player would be on the way to a lane by now",
                no: "they are in a lane, held there by something on the screen, or have only just paused")
            .Choice("lane", CoachQuestions.Lane, criteria);

        _askingIdle = true;
        _lastIdleAskAt = frame.VideoTime;
        var asked = frame.VideoTime;
        Ask("idle", moment, questions, asked, NowRequest, released: () => _askingIdle = false, answered: response =>
        {
            if (!response.TryGet<NoulAnswer>("walk", out var walk) || !walk!.IsYes(_options.YesAt))
                return;
            if (!response.TryGet<ChoiceAnswer>("lane", out var lane) || !RiftMap.Lanes.Contains(lane!.Choice))
                return;
            var toward = RiftMap.Toward(lane.Choice, rest.X, rest.Y);
            // World y grows north, screen y grows down: flip for the step.
            var (dx, dy) = (toward.X - rest.X, -(toward.Y - rest.Y));
            var length = double.Hypot(dx, dy);
            if (length == 0)
                return;   // walking to the lane they stand in: nothing to demonstrate
            var direction = ScreenDirections.Name(dx, dy);
            var clock = moment.GameClock is { } time ? $" at {time}" : "";
            var reason = $"you have stood still for {whereabouts.StoodStillForSeconds:0.0}s in {whereabouts.Place}{clock}; "
                + $"a good player would be on the way to {lane.Choice} lane ({toward.Distance:0} units {direction})";
            _moves.Add(new MoveStep(asked, direction, dx / length, dy / length, 2, reason));
            Remember($"stepped toward {lane.Choice} lane", asked);
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
        Ask("bolt", moment, questions, evt.VideoTime, OccasionRequest, released: null, answered: response =>
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
            evt.VideoTime, OccasionRequest, released: null, answered: response =>
            {
                if (!response.TryGet<NoulAnswer>("remark", out var remark) || !remark!.IsYes(_options.YesAt))
                    return;
                _cues.Add(new CoachCue(evt.VideoTime, 2, sentence));
                Remember("remarked on aim", evt.VideoTime);
            });
    }

    // --- A new level: a point to put into an ability ---

    /// <summary>
    /// The player's own level rising, off their nameplate: a point to spend.
    /// Whether to spend it now and which ability takes it are the model's
    /// calls; the code offers the buttons the game would accept a point in
    /// (the ultimate only at 6, 11 and 16, and nothing the coach's own points
    /// have filled: five in a basic ability, three in the ultimate, since the
    /// game refuses more and the coach demonstrates one line), says of each
    /// what it is, whether it has been seen cast, and how many points the
    /// coach itself has put in it since it began watching, and turns a yes
    /// into the level-up chord, Ctrl and the slot. An ally's level is theirs;
    /// a level at or below one already asked about is the same level-up read
    /// twice.
    /// </summary>
    private void OnLevelUp(GameEvent evt)
    {
        if (evt.Level is not { } level || Self() is not { } self)
            return;
        if (evt.Champion is { } champion ? champion != self.Champion : evt.TrackId != self.TrackId)
            return;
        if (level <= _highestLevelAsked)
            return;
        _highestLevelAsked = level;
        if (_firstLevelAsked == 0)
            _firstLevelAsked = level;

        var ultimate = level is 6 or 11 or 16;
        var occasion = new LevelOccasion("you reached a new level", level, ultimate, _firstLevelAsked);
        var moment = Describe(_frame, self, occasion, evt.VideoTime);

        var criteria = new ChoiceCriteria();
        foreach (var slot in AbilityKits.Slots)
        {
            if (slot == "R" && !ultimate)
                continue;
            var points = _pointsPlaced.GetValueOrDefault(slot);
            if (points >= (slot == "R" ? 3 : 5))
                continue;   // full on the coach's own line: the game would refuse another
            var known = AbilityKits.For(self.Champion, slot);
            var what = known?.Kind ?? "what it is is not on file";
            var order = known?.UsuallyMaxed is { } place
                ? $"the ability this champion usually maxes {place}"
                : slot == "R" ? "the ultimate, which takes a point at levels 6, 11 and 16, and this is one of them: it comes before any other ability"
                : "its place in this champion's usual skill order is not on file";
            var seen = _casts.ContainsKey(slot)
                ? "seen cast this game, so it holds a point already"
                : "never seen cast this game, so it may hold no point yet";
            var placed = points switch
            {
                0 => "the coach has put no point in it since it began watching",
                1 => "the coach has put 1 point in it since it began watching",
                var n => $"the coach has put {n} points in it since it began watching",
            };
            criteria[slot] = $"{slot}: {what}; {order}; {seen}; {placed}";
        }
        if (criteria.Count == 0)
            return;   // every button the coach could offer is full on its own line
        var questions = new Questions()
            .Noul("spend", CoachQuestions.Spend,
                yes: "a good player would put the point into an ability right now",
                no: "a good player would hold the point, which after level one they do not")
            .Choice("slot", CoachQuestions.Slot, criteria);

        var clock = moment.GameClock is { } time ? $" at {time}" : "";
        Ask("level", moment, questions, evt.VideoTime, OccasionRequest, released: null, answered: response =>
        {
            if (!response.TryGet<NoulAnswer>("spend", out var spend) || !spend!.IsYes(_options.YesAt))
                return;
            if (!response.TryGet<ChoiceAnswer>("slot", out var slot) || !criteria.ContainsKey(slot!.Choice))
                return;
            var note = AbilityKits.For(self.Champion, slot.Choice) is { } known ? $" ({known.Kind})" : "";
            var reason = $"you reached level {level}{clock}; a good player would put the point in {slot.Choice}{note}";
            _keys.Add(new KeyPress(evt.VideoTime, slot.Choice, 2, reason) { WithControl = true });
            _pointsPlaced[slot.Choice] = _pointsPlaced.GetValueOrDefault(slot.Choice) + 1;
            Remember($"put the point in {slot.Choice}", evt.VideoTime);
        });
    }

    // --- Asking, and collecting the answers ---

    /// <summary>
    /// Sends one question set and collects the answer without waiting for it.
    /// The continuation only queues work; every touch of this policy's state
    /// happens in <see cref="Settle"/>, on the caller's thread.
    /// <paramref name="released"/> clears the in-flight flag of a question
    /// about the moment once its answer is in, good or bad; an event's
    /// question has none.
    /// </summary>
    private void Ask(string occasion, Moment moment, Questions questions, double videoTime,
        RequestOptions request, Action? released, Action<SystemOneResponse> answered)
    {
        var generation = _generation;
        OnTheWire(occasion, sent: true);
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
            OnTheWire(occasion, sent: false);
            _arrivals.Enqueue(() =>
            {
                if (generation == _generation)
                    released?.Invoke();
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

    /// <summary>
    /// Raised under the lock, so two threads' changes reach a listener in the
    /// order they happened and the last one it hears is the truth.
    /// </summary>
    private void OnTheWire(string occasion, bool sent)
    {
        lock (_wireLock)
        {
            if (sent)
                _onTheWire.Add(occasion);
            else
                _onTheWire.Remove(occasion);
            AskingChanged?.Invoke(_onTheWire.ToArray());
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
        WhereaboutsFacts? whereabouts = null;
        if (frame is not null && self is not null)
        {
            whereabouts = Whereabouts(frame, self, now);
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
            Whereabouts = whereabouts,
            Coach = _recent.Select(r => new RecentAction(r.Did, Math.Round(now - r.At, 1))).ToArray(),
            Occasion = occasion,
        };
    }

    /// <summary>
    /// Where the player stands and how far each lane is, off their own
    /// minimap. The still time counts from the spot they stopped on; a player
    /// without a place on the map has no whereabouts.
    /// </summary>
    private WhereaboutsFacts? Whereabouts(FrameEnvelope frame, ChampionRow self, double now)
    {
        if (self is not { WorldX: { } x, WorldY: { } y })
            return null;
        var placed = frame.Champions
            .Where(c => c.Team == self.Team && c.TrackId != self.TrackId && c.Alive != false
                && c is { WorldX: not null, WorldY: not null })
            .ToArray();
        var lanes = RiftMap.Lanes.Select(lane =>
        {
            var toward = RiftMap.Toward(lane, x, y);
            var there = placed
                .Where(c => RiftMap.Toward(lane, c.WorldX!.Value, c.WorldY!.Value).Distance <= RiftMap.LaneHalfWidth)
                .Select(c => c.Champion ?? $"track {c.TrackId}")
                .ToArray();
            return new LaneFacts(lane, Math.Round(toward.Distance),
                toward.Distance < 1 ? null : ScreenDirections.NameOfWorldOffset(toward.X - x, toward.Y - y), there);
        }).ToArray();
        var still = _restAt is null ? 0 : Math.Max(0, Math.Round(now - _stillSince, 1));
        return new WhereaboutsFacts(RiftMap.Place(x, y), still, lanes);
    }

    /// <summary>The spot the player stands on: a new one once they have left the old by more than the jitter.</summary>
    private void TrackStillness(FrameEnvelope frame, ChampionRow self)
    {
        if (self is not { WorldX: { } x, WorldY: { } y })
        {
            _restAt = null;
            return;
        }
        if (_restAt is { } rest && double.Hypot(x - rest.X, y - rest.Y) <= _options.StillRadiusUnits)
            return;
        _restAt = (x, y);
        _stillSince = frame.VideoTime;
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

    /// <summary>Visible-spell bookkeeping: when each track's current spell began.</summary>
    private void TrackVisibility(FrameEnvelope frame)
    {
        foreach (var row in frame.Champions)
        {
            if (row.Visible)
                _visibleSince.TryAdd(row.TrackId, frame.VideoTime);
            else
                _visibleSince.Remove(row.TrackId);
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
    /// Identity bookkeeping, never coaching. When the pipeline renames a track
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
}
