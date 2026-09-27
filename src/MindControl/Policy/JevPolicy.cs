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
    /// itself: the root question, what a good player would do right now. One
    /// is in flight at a time, so the model's own latency paces it too; this
    /// floor keeps a fast replay inside the request budget. Events are asked
    /// about as they come, on top of this.
    /// </summary>
    public double AskEverySeconds { get; init; } = 0.25;

    /// <summary>
    /// The root question's pick is no order below this probability. Its
    /// options share one distribution, so a pick split with a second option
    /// sits well under a yes/no's margin; and <c>carry_on</c>, the no-order
    /// option, is always among them to take up a moment with nothing to do.
    /// Uncalibrated: nothing has yet been replayed through the root question.
    /// </summary>
    public double DecideAt { get; init; } = 0.4;

    /// <summary>
    /// A yes below this probability is a no, for the yes/no questions about
    /// an event (a bolt, a shot). The model's yes/no answers are calibrated
    /// probabilities, and the ghost acts on a yes with a margin, because a
    /// step taken on a coin flip is not coaching. On the fixture, a step it
    /// owes sits at 0.75–0.86 and one it does not at 0.06–0.3.
    /// </summary>
    public double YesAt { get; init; } = 0.6;

    /// <summary>
    /// The least time, in video seconds, between two movement clicks: a step
    /// toward a lane, back out of the enemy wave, to catch a wave at a turret
    /// or into the brush (a dodge counts too). A good player moves in short
    /// clicks, a fresh one about every second, so for this long after one the
    /// root does not offer another, whether the player stands or walks; a
    /// step still being walked is no reason to hold the next after it.
    /// </summary>
    public double MoveEverySeconds { get; init; } = 1;

    /// <summary>
    /// The least time, in video seconds, between two attack orders. One
    /// right-click keeps a champion attacking its target, so this is the pace
    /// of a new target, not of the attacks: for this long after one, the root
    /// does not offer another.
    /// </summary>
    public double AttackEverySeconds { get; init; } = 1;

    /// <summary>
    /// The least time, in video seconds, before the coach says again one of
    /// the things its hands cannot yet do (run away, fall back to a turret,
    /// recall, buy): for this long after it said one, the root does not offer
    /// it, so a moment that goes on being right for it is not said four times
    /// a second.
    /// </summary>
    public double SayEverySeconds { get; init; } = 5;

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
/// ability a new level's point goes into, whether and what to right-click
/// to attack, whether to walk into the brush -- is a
/// question put to Jev, TypeSafe's System One model, and
/// answered as a probability, a level or an option. Nothing here decides; it
/// measures, asks, and turns the answer into the output the reactor already
/// knows how to log, stream, trace and record.
///
/// <para><b>A tree, rooted in one question.</b> The moment itself is one
/// question a quarter second, <c>decide</c>: of the things the state makes
/// possible right now -- level up, run away, use an ability, attack, step
/// back, fall back to a turret, hide in the brush, catch a wave, walk to
/// lane, recall, buy, or carry on -- which would a good player do? Each
/// branch offers its option only when it has something to do, and adds its
/// own follow-up (which ability, which target, which lane, which brush) to
/// the same request; only the picked branch's answers are read, so the
/// coach does one thing at a time and a walk is never undone by an attack
/// ordered the same second. The branches the ghost's hands cannot do yet
/// (run away, fall back, recall, buy) are said as a cue. A bolt at the
/// player and a shot of theirs are events, asked about as they come.</para>
///
/// <para><b>What stays in code, and why.</b> Perception: which row is the
/// player (<c>--self</c> or the is_self majority, with the pipeline's identity
/// corrections applied), how long each enemy has been in view, which buttons
/// the HUD has shown a cooldown for and when they come back, the last few
/// shots seen at a target, the last bolt that landed, how long the player
/// has stood on one spot, which buttons the HUD lights for a waiting skill
/// point and how long it has waited. Geometry: the two sides of a bolt's line, which way
/// is toward home, which way on the screen an enemy is, where on the map the
/// player stands and how far each lane is (<see cref="RiftMap"/>), which lane
/// each minimap minion is in and how far each side's wave has pushed, and
/// how near the minions on the screen are and whether the player stands in
/// front of their own, and which enemy minions and champions the player's
/// basic attack reaches, and which patches of brush are near and what lies
/// around each (<see cref="RiftBrush"/>). Fair
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

    // Perception: the minions on the screen, followed from frame to frame so
    // each bar has a fall rate.
    private readonly MinionTracker _minionTracks = new();

    // Perception: the turrets the minimap last showed, all 22, or null when it
    // has not been read for them since the last resync.
    private Turret[]? _turrets;

    // Perception: the skill point the HUD shows waiting, when one is -- since
    // when the feed has shown it, which slots it lights, and a number that
    // tells an answer whether it is still the point that was asked about --
    // and the chords the coach has pressed for it that the HUD has not yet
    // shown gone in. The first level a point was asked about at, since which
    // the coach has been watching, and where the coach put each point: those
    // are the coach's own placements, counted when the HUD shows the point
    // spent, a lower bound on the ability's points and never a reading of the
    // HUD's rank pips, which the feed does not carry.
    private (double Since, string[] Slots, int Id)? _point;
    private int _pointId;
    private bool _pointAsked;
    private readonly List<string> _pressedFor = [];
    private int? _firstLevelAsked;
    private readonly Dictionary<string, int> _pointsPlaced = [];

    // What the coach said since the last drain.
    private readonly List<CoachCue> _cues = [];
    private readonly List<KeyPress> _keys = [];
    private readonly List<MoveStep> _moves = [];

    // Questions in flight. Answers land here from the client's thread and are
    // applied on the reactor's, in Settle.
    private readonly ConcurrentQueue<Action> _arrivals = new();
    private int _generation;
    private bool _deciding;
    private double _lastDecideAt = double.NegativeInfinity;

    // What the coach last did, by kind, for the pace of the next.
    private double _lastMoveAt = double.NegativeInfinity;
    private double _lastAttackAt = double.NegativeInfinity;
    private readonly Dictionary<string, double> _lastSaid = [];

    private bool _failing;

    // Questions on the wire, by occasion, oldest first. Touched from the
    // client's thread as well as the reactor's, so it has its own lock.
    private readonly List<string> _onTheWire = [];
    private readonly Lock _wireLock = new();

    /// <summary>
    /// The occasions ("decide", "bolt", "shot") of the questions
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
            missing.Add("ability HUD (no button will be pressed, no point put into an ability)");
        if (!meta.HasThreats)
            missing.Add("threats (no step will be taken)");
        if (!meta.HasSkillshots)
            missing.Add("skillshots (aim will not be remarked on)");
        if (meta.WorldBounds is null)
            missing.Add("world calibration (nobody will be walked to lane or into brush)");
        if (!meta.HasMinions)
            missing.Add("minion bars (nobody will be stepped back out of an enemy wave or shown a last hit)");
        if (!meta.HasMinionDots)
            missing.Add("minimap minions (walks go to where a lane is played, not to its wave; "
                + "they need the minimap scale at 400px or more)");
        if (!meta.HasTurrets)
            missing.Add("minimap turrets (a wave is placed by the turret spots, whether or not the turret there has fallen)");
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
        _deciding = false;
        _lastDecideAt = double.NegativeInfinity;
        _lastMoveAt = double.NegativeInfinity;
        _lastAttackAt = double.NegativeInfinity;
        _lastSaid.Clear();

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
        _turrets = null;
        _minionTracks.Clear();
        _point = null;
        _pressedFor.Clear();
        _firstLevelAsked = null;
        _pointsPlaced.Clear();
        if (latest is not null)
        {
            _turrets = Carried(latest, r => r.Turrets);
            _minionTracks.Update(latest.VideoTime, Carried(latest, r => r.Minions));
            foreach (var row in latest.Champions.Where(c => c.Visible))
                _visibleSince[row.TrackId] = latest.VideoTime;
            if (Self() is { } self)
            {
                // The spot restarts from the baseline too: standing still across
                // a gap is a claim about frames we never saw.
                TrackStillness(latest, self);
                // A point the baseline shows waiting is state the feed will
                // not announce again; it is asked about from the frames.
                if (LitSlots(self.Learnable) is { Length: > 0 } lit)
                {
                    _point = (latest.VideoTime, lit, ++_pointId);
                    _pointAsked = false;
                }
            }
        }
        // _selfVotes survive: identity outlives a gap.
    }

    public void OnFrame(FrameEnvelope frame)
    {
        Settle();
        _frame = frame;
        VoteSelf(frame);
        TrackVisibility(frame);
        if (Carried(frame, r => r.Turrets) is { } turrets)
            _turrets = turrets;
        _minionTracks.Update(frame.VideoTime, Carried(frame, r => r.Minions));
        if (Self() is { } self)
        {
            if (self.Resource is { } resource)
                _resource = resource;
            if (self.Health is { } health)
                _health = health;
            TrackReach(frame, self);
            TrackStillness(frame, self);
            TrackPoint(self);
            AskDecide(frame, self);
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
            case EventKind.SkillPoint:
                OnSkillPoint(evt);
                break;
            case EventKind.SkillSpent:
                OnSkillSpent(evt);
                break;
            case EventKind.TurretDestroyed:
            case EventKind.TurretRebuilt:
                OnTurret(evt);
                break;
        }
        Settle();
    }

    // --- The moment itself: what a good player would do now ---

    /// <summary>
    /// The root's option for no new order. It is always offered, and offered
    /// first, so a model that answers nothing useful gives no order.
    /// </summary>
    private const string CarryOn = "carry_on";

    /// <summary>
    /// One option of the root question: its name, its rubric as the option
    /// says it, the follow-up it adds to the same request (which ability,
    /// which lane, which target, which brush), and what a pick of it does.
    /// </summary>
    private sealed record Branch(
        string Option, string Criterion, Action<Questions>? FollowUp, Action<SystemOneResponse> Act);

    /// <summary>
    /// Asks the root of the moment: of the things the state makes possible
    /// right now, which would a good player do? Each branch offers its option
    /// only when there is something for it to do (a button up with an enemy
    /// on the screen, a target in reach, a point waiting, a patch of brush
    /// near); when none does, nothing is asked. The follow-ups every offered
    /// branch needs go in the same request, since the model answers each
    /// question on its own and a second round trip would cost the coach a
    /// tenth of a second; only the picked branch's are read. A pick below
    /// <see cref="JevOptions.DecideAt"/>, or <see cref="CarryOn"/>, is no
    /// order. One in flight at a time, and no more often than
    /// <see cref="JevOptions.AskEverySeconds"/>.
    /// </summary>
    private void AskDecide(FrameEnvelope frame, ChampionRow self)
    {
        if (_deciding || frame.VideoTime - _lastDecideAt < _options.AskEverySeconds)
            return;
        var asked = frame.VideoTime;
        var moment = Describe(frame, self, occasion: null, asked);
        var branches = new[]
        {
            LevelUp(self, moment, asked),
            RunAway(frame, self, moment, asked),
            UseAbility(frame, self, moment, asked),
            Attack(frame, self, asked),
            StepBack(frame, self, moment, asked),
            GoToTurret(frame, self, moment, asked),
            HideInBrush(frame, self, moment, asked),
            CatchWave(frame, self, moment, asked),
            WalkToLane(frame, self, moment, asked),
            Recall(frame, self, moment, asked),
            Buy(frame, self, moment, asked),
        }.OfType<Branch>().ToArray();
        if (branches.Length == 0)
            return;

        var criteria = new ChoiceCriteria { [CarryOn] = CoachQuestions.CarryOnOption };
        foreach (var branch in branches)
            criteria[branch.Option] = branch.Criterion;
        var questions = new Questions().Choice("decide", CoachQuestions.Decide, criteria);
        foreach (var branch in branches)
            branch.FollowUp?.Invoke(questions);

        _deciding = true;
        _lastDecideAt = asked;
        Ask("decide", moment, questions, asked, NowRequest, released: () => _deciding = false, answered: response =>
        {
            if (!response.TryGet<ChoiceAnswer>("decide", out var decide) || decide!.Choice == CarryOn)
                return;
            if (decide.Probabilities.GetValueOrDefault(decide.Choice) < _options.DecideAt)
                return;
            branches.FirstOrDefault(b => b.Option == decide.Choice)?.Act(response);
        });
    }

    /// <summary>
    /// The pick of a follow-up choice: the one option when there was only
    /// one (no question was asked), otherwise the model's, when it names one
    /// of the options.
    /// </summary>
    private static string? Picked(SystemOneResponse response, string question, IReadOnlyCollection<string> options) =>
        options.Count == 1 ? options.First()
        : response.TryGet<ChoiceAnswer>(question, out var pick) && options.Contains(pick!.Choice) ? pick.Choice
        : null;

    /// <summary>Whether a movement click would come sooner than a good player's one a second after the last.</summary>
    private bool Stepping(double now) => now - _lastMoveAt < _options.MoveEverySeconds;

    // --- Buttons: which one a good player would throw now ---

    /// <summary>
    /// Offered while the player is alive and placed, a button the HUD has
    /// shown come back is up, and an enemy is on the screen to throw it at.
    /// A greyed button and an empty screen offer nothing. A pick is one key
    /// press; with more than one button up, which is the follow-up's call.
    /// </summary>
    private Branch? UseAbility(FrameEnvelope frame, ChampionRow self, Moment moment, double asked)
    {
        if (self.Alive == false || self is not { WorldX: not null, WorldY: not null })
            return null;
        var slots = _casts
            .Where(c => c.Value.Countdown is { } countdown && frame.VideoTime >= c.Value.At + countdown)
            .Select(c => c.Key)
            .Order()
            .ToArray();
        if (slots.Length == 0 || moment.VisibleEnemies.Count == 0)
            return null;

        var criteria = new ChoiceCriteria();
        foreach (var slot in slots)
        {
            var facts = moment.Abilities.First(a => a.Slot == slot);
            var inside = moment.VisibleEnemies.Where(e => e.InRangeOf.Contains(slot)).Select(e => e.Champion).ToArray();
            criteria[slot] = $"{slot}: {facts.Kind ?? "what it is is not on file"}, "
                + (facts.Range is { } range ? $"reaching {range:0} units" : "its range not on file")
                + (inside.Length > 0 ? $"; inside its range: {string.Join(", ", inside)}" : "; no visible enemy inside its range");
        }
        return new Branch("use_ability", CoachQuestions.UseAbilityOption(slots),
            slots.Length > 1 ? q => q.Choice("ability", CoachQuestions.Ability, criteria) : null,
            response =>
            {
                if (Picked(response, "ability", slots) is not { } slot)
                    return;
                var nearest = moment.VisibleEnemies[0];
                var facts = moment.Abilities.First(a => a.Slot == slot);
                var reason = facts.Range is { } range && nearest.DistanceUnits <= range
                    ? $"{nearest.Champion} has been in {slot} range ({nearest.DistanceUnits:0} units)"
                      + (nearest.WithinReachForSeconds is { } held ? $" for {held:0.0}s" : "")
                      + $" with {slot} up"
                    : $"{nearest.Champion} is {nearest.DistanceUnits:0} units away with {slot} up";
                _keys.Add(new KeyPress(asked, slot, 2, reason));
                Remember($"pressed {slot}", asked);
            });
    }

    // --- A skill point waiting: which ability it goes into ---

    /// <summary>
    /// Offered while the HUD shows a skill point waiting that the coach has
    /// not already pressed the chord for, alive or dead (a point goes in from
    /// the death screen too). Whether to spend it now, or hold it -- the first
    /// point of a game, against an invade -- is the root's call, from
    /// <see cref="Moment.SkillPoint"/>; which ability takes it is the
    /// follow-up's, a choice among exactly the slots the HUD lights, each
    /// said with what it is, its place in the champion's usual skill order,
    /// whether it has been seen cast, and how many points the coach itself
    /// has put in it since it began watching. A pick is the level-up chord,
    /// Ctrl and the slot. An answer that lands after the point was spent, or
    /// announced anew with another set, is about nothing and is dropped.
    /// </summary>
    private Branch? LevelUp(ChampionRow self, Moment moment, double asked)
    {
        if (_point is not { } point || _pressedFor.Count > 0 || moment.SkillPoint is not { } facts)
            return null;
        var again = _pointAsked;
        _pointAsked = true;
        var slots = point.Slots;

        var criteria = new ChoiceCriteria();
        foreach (var slot in slots)
        {
            var points = _pointsPlaced.GetValueOrDefault(slot);
            var known = AbilityKits.For(self.Champion, slot);
            var what = known?.Kind ?? "what it is is not on file";
            var order = known?.UsuallyMaxed is { } place
                ? $"the ability this champion usually maxes {place}"
                : slot == "R" ? "the ultimate, which takes a point at levels 6, 11 and 16, and the HUD offers it now: it comes before any other ability"
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

        var clock = moment.GameClock is { } time ? $" at {time}" : "";
        var since = facts.Level is { } l ? $"level {l}" : "your last level";
        return new Branch("level_up", CoachQuestions.LevelUpOption,
            slots.Length > 1 ? q => q.Choice("slot", CoachQuestions.Slot, criteria) : null,
            response =>
            {
                if (_point?.Id != point.Id)
                    return;   // spent, or announced anew with another set, since it was asked
                if (Picked(response, "slot", slots) is not { } slot)
                    return;
                var note = AbilityKits.For(self.Champion, slot) is { } known ? $" ({known.Kind})" : "";
                var reason = again
                    ? $"you have held the point from {since} for {facts.HeldForSeconds:0.0}s{clock}; a good player would put it in {slot}{note} by now"
                    : $"you reached {since}{clock}; a good player would put the point in {slot}{note}";
                _keys.Add(new KeyPress(asked, slot, 2, reason) { WithControl = true });
                _pressedFor.Add(slot);
                Remember($"put the point in {slot}", asked);
            });
    }

    /// <summary>The skill point the HUD shows waiting, as the state tells it; null when none is.</summary>
    private SkillPointFacts? SkillPointNow(ChampionRow? self, double now)
    {
        if (_point is not { } point)
            return null;
        var level = self?.Level;
        _firstLevelAsked ??= level;
        return new SkillPointFacts(level, point.Slots, point.Slots.Contains("R"), _firstLevelAsked,
            Math.Max(0, Math.Round(now - point.Since, 1)));
    }

    // --- Basic attacks: a last hit, or a trade ---

    /// <summary>How far past the attack's range a target still counts as nearly in reach: a step or two.</summary>
    private const double ApproachUnits = 150;

    /// <summary>
    /// The reach a target is looked for within when the player's attack range
    /// is not on file: a caster minion's, which is also a ranged champion's
    /// usual. Only the gate uses it; the facts say the range is not on file.
    /// </summary>
    private const double ReachWithoutARange = CasterMinionRange;

    /// <summary>How many enemy minions near the player, lowest bars first, are offered as targets.</summary>
    private const int MinionTargets = 4;

    /// <summary>
    /// Offered while the player is alive and placed with something to
    /// attack: an enemy minion whose bar was read, or a visible enemy
    /// champion, within (or a step or two beyond) the player's basic-attack
    /// range. Not offered for <see cref="JevOptions.AttackEverySeconds"/>
    /// after the coach's last attack, which the champion is still carrying
    /// out. Whether an attack is worth it -- a last hit, a trade, clearing the
    /// wave -- is the root's call, and which target the follow-up's; a pick
    /// is one right-click on the target, the order that has the champion
    /// attack it.
    /// </summary>
    private Branch? Attack(FrameEnvelope frame, ChampionRow self, double asked)
    {
        if (self.Alive == false || self is not { WorldX: { } x, WorldY: { } y })
            return null;
        if (frame.VideoTime - _lastAttackAt < _options.AttackEverySeconds)
            return null;
        var range = AbilityKits.AttackRange(self.Champion);
        var reach = (range ?? ReachWithoutARange) + ApproachUnits;

        var targets = new Dictionary<string, (AttackTarget Target, double X, double Y, string Reason)>();
        var criteria = new ChoiceCriteria();
        foreach (var (minion, mx, my) in NearMinions(frame, self))
        {
            var name = $"the enemy minion at {minion.Health:0%} health";
            var where = minion.ScreenDirection is { } way ? $"{minion.DistanceUnits:0} units {way}" : "where the player stands";
            var fall = minion switch
            {
                { SecondsToEmpty: { } empty } => $", its bar falling {minion.FallingPerSecond:0.00} a second (empty in {empty:0.0}s at that rate)",
                { FallingPerSecond: 0 } => ", its bar holding",
                _ => "",
            };
            criteria[minion.Name] = $"{minion.Name}: an enemy minion with {minion.Health:0%} of its health bar left{fall}, "
                + $"{where}, {InReach(minion.InAttackRange)}";
            targets[minion.Name] = (new AttackTarget(name, minion.DistanceUnits), mx, my,
                $"it is {where} with {minion.Health:0%} of its bar left, {InReach(minion.InAttackRange)}; a good player would attack it now");
        }
        foreach (var row in Visible(frame, self).OrderBy(r => Distance(self, r)))
        {
            var distance = Distance(self, row)!.Value;
            var champion = row.Champion ?? $"track {row.TrackId}";
            if (distance > reach || targets.ContainsKey(champion))
                continue;
            bool? inRange = range is { } r ? distance <= r : null;
            var health = row.Health is { } h ? $"{h:0%} health" : "health not read";
            var covered = TurretCover(row.WorldX!.Value, row.WorldY!.Value) is { } turret ? $", standing under {turret.Said}" : "";
            criteria[champion] = $"{champion}: the enemy champion, {health}, {distance:0} units {Direction(self, row)}, {InReach(inRange)}{covered}";
            targets[champion] = (new AttackTarget(champion, distance), row.WorldX!.Value, row.WorldY!.Value,
                $"{champion} is {distance:0} units {Direction(self, row)} with {health}, {InReach(inRange)}; a good player would attack them now");
        }
        if (targets.Count == 0)
            return null;

        return new Branch("attack", CoachQuestions.AttackOption,
            targets.Count > 1 ? q => q.Choice("target", CoachQuestions.AttackTarget, criteria) : null,
            response =>
            {
                if (Picked(response, "target", targets.Keys) is not { } chosen)
                    return;
                var target = targets[chosen];
                // World y grows north, screen y grows down: flip for the click.
                var (dx, dy) = (target.X - x, -(target.Y - y));
                var length = double.Hypot(dx, dy);
                var (ux, uy) = length < 1 ? (0.0, 0.0) : (dx / length, dy / length);
                var direction = length < 1 ? "where you stand" : ScreenDirections.Name(dx, dy);
                _moves.Add(new MoveStep(asked, direction, ux, uy, 2, target.Reason) { Target = target.Target });
                _lastAttackAt = asked;
                Remember($"attacked {target.Target.Name}", asked);
            });

        static string InReach(bool? inRange) => inRange switch
        {
            true => "inside your attack range",
            false => "a step outside your attack range",
            null => "your attack range is not on file",
        };
    }

    /// <summary>
    /// The enemy minions on the player's screen near enough to attack, or a
    /// step or two beyond, whose bars could be read, lowest bar first, with
    /// where each stands in world units. Named "minion 1" and on in that
    /// order: the names the attack follow-up's options and the state share.
    /// </summary>
    private (MinionTarget Fact, double X, double Y)[] NearMinions(FrameEnvelope frame, ChampionRow self)
    {
        if (Carried(frame, r => r.Minions) is not { } minions || self is not { WorldX: { } x, WorldY: { } y })
            return [];
        var range = AbilityKits.AttackRange(self.Champion);
        var reach = (range ?? ReachWithoutARange) + ApproachUnits;
        return minions
            .Where(m => m.Team != MinionTeam.Blue && m is { WorldX: not null, WorldY: not null, Health: not null })
            .Select(m => (Minion: m, Distance: double.Hypot(m.WorldX!.Value - x, m.WorldY!.Value - y)))
            .Where(p => p.Distance <= reach)
            .OrderBy(p => p.Minion.Health).ThenBy(p => p.Distance)
            .Take(MinionTargets)
            .Select((p, i) =>
            {
                var (falling, empty) = _minionTracks.Trend(p.Minion);
                return (new MinionTarget(
                        $"minion {i + 1}", Math.Round(p.Minion.Health!.Value, 2), Math.Round(p.Distance),
                        p.Distance < 1 ? null : ScreenDirections.NameOfWorldOffset(p.Minion.WorldX!.Value - x, p.Minion.WorldY!.Value - y),
                        range is { } r ? p.Distance <= r : null)
                    {
                        FallingPerSecond = falling, SecondsToEmpty = empty,
                    },
                    p.Minion.WorldX!.Value, p.Minion.WorldY!.Value);
            })
            .ToArray();
    }

    // --- In the lane: out of the enemy wave ---

    /// <summary>
    /// How far back down the lane a step out of the enemy wave aims. The
    /// recording clicks a sidestep a fixed distance from the player's model,
    /// so this sets only the direction: along the lane toward home, not
    /// straight at the nexus across the map.
    /// </summary>
    private const double BackStepUnits = 500;

    /// <summary>
    /// Offered while the player is alive, standing in a lane, with an enemy
    /// minion's bar read and placed near them, and no movement click in the
    /// last <see cref="JevOptions.MoveEverySeconds"/>. Whether they stand
    /// too far forward is the root's call, from <see cref="Moment.Minions"/>;
    /// a pick is one sidestep back down the lane toward their own nexus.
    /// </summary>
    private Branch? StepBack(FrameEnvelope frame, ChampionRow self, Moment moment, double asked)
    {
        if (self.Alive == false || self is not { WorldX: { } x, WorldY: { } y } || RiftMap.LaneOf(x, y) is not { } lane)
            return null;
        if (Stepping(frame.VideoTime) || moment.Minions is not { NearestTheirsUnits: { } nearest } minions)
            return null;

        return new Branch("step_back", CoachQuestions.StepBackOption, null, _ =>
        {
            var (_, progress) = RiftMap.Along(lane, x, y);
            var behind = RiftMap.At(lane, progress - BackStepUnits / RiftMap.Length(lane));
            // World y grows north, screen y grows down: flip for the step.
            var (dx, dy) = (behind.X - x, -(behind.Y - y));
            var length = double.Hypot(dx, dy);
            if (length == 0)
                return;
            var direction = ScreenDirections.Name(dx, dy);
            var reach = minions.TheirsWithinCasterRange switch
            {
                0 => $"the nearest enemy minion is {nearest:0} units away",
                1 => "an enemy minion is within a caster minion's reach of you",
                var n => $"{n} enemy minions are within a caster minion's reach of you",
            };
            var stand = minions.AheadOfOurFrontUnits switch
            {
                > 0 and var ahead => $" and you stand {ahead:0} units in front of your own minions",
                null when minions.Ours == 0 => " and none of your own minions is on the screen to take the hits",
                _ => "",
            };
            var reason = $"{reach}{stand}; a good player stands behind their own minions' front";
            _moves.Add(new MoveStep(asked, direction, dx / length, dy / length, 2, reason));
            _lastMoveAt = asked;
            Remember($"stepped back {direction}, out of the enemy minions", asked);
        });
    }

    // --- Away from the action: would a good player be heading to lane? ---

    /// <summary>
    /// Offered while the player is alive and placed on the map, the game
    /// clock is running (before it the player cannot move), and no movement
    /// click came in the last <see cref="JevOptions.MoveEverySeconds"/>:
    /// whether they stand or walk, and whatever the coach's last step was.
    /// Whether they belong somewhere else is the root's call and which lane
    /// the follow-up's; a pick is one step on the ground toward where that
    /// lane's waves meet when the minimap shows both, toward the enemy's
    /// front when it is alone at one of the player's turrets, and otherwise
    /// toward where the lane is played (<see cref="RiftMap.LaningSpot"/>).
    /// The trip is a string of such steps, one a second, each decided afresh.
    /// </summary>
    private Branch? WalkToLane(FrameEnvelope frame, ChampionRow self, Moment moment, double asked)
    {
        if (self.Alive == false || self is not { WorldX: { } x, WorldY: { } y } || frame.GameTime is null)
            return null;
        if (Stepping(frame.VideoTime) || moment.Whereabouts is not { } whereabouts)
            return null;

        var criteria = new ChoiceCriteria();
        foreach (var lane in whereabouts.Lanes)
        {
            var allies = lane.AlliesThere.Count == 0 ? "none" : string.Join(", ", lane.AlliesThere);
            criteria[lane.Lane] = (lane.ScreenDirection is { } direction
                ? $"{lane.Lane} lane: {lane.DistanceUnits:0} units away, {direction} on the screen; allies there: {allies}"
                : $"{lane.Lane} lane: the player is standing in it; allies there: {allies}")
                + DescribeWave(lane.Wave);
        }
        return new Branch("walk_to_lane", CoachQuestions.WalkToLaneOption,
            q => q.Choice("lane", CoachQuestions.Lane, criteria),
            response =>
            {
                if (!response.TryGet<ChoiceAnswer>("lane", out var lane) || !RiftMap.Lanes.Contains(lane!.Choice))
                    return;
                // A step toward where the lane's waves meet when the minimap shows
                // both, toward the enemy's front when it is alone at one of the
                // player's turrets, and otherwise toward where the lane is played --
                // never its nearest point, which from base is only its mouth.
                var facts = whereabouts.Lanes.First(l => l.Lane == lane.Choice).Wave;
                var wave = facts?.MeetAt
                    ?? (facts?.TheirFront is { } front && RiftMap.AtOurTurret(lane.Choice, front) ? front : null);
                var spot = wave is { } at ? RiftMap.At(lane.Choice, at) : RiftMap.LaningSpot(lane.Choice);
                // World y grows north, screen y grows down: flip for the step.
                var (dx, dy) = (spot.X - x, -(spot.Y - y));
                var length = double.Hypot(dx, dy);
                if (length <= RiftMap.LaneHalfWidth)
                    return;   // already where the walk would go: nothing to demonstrate
                var direction = ScreenDirections.Name(dx, dy);
                var clock = moment.GameClock is { } time ? $" at {time}" : "";
                var to = wave is null ? $"{lane.Choice} lane" : $"{lane.Choice} lane's minion wave";
                var where = whereabouts.StoodStillForSeconds >= 1
                    ? $"you have stood still for {whereabouts.StoodStillForSeconds:0.0}s in {whereabouts.Place}{clock}"
                    : $"you are in {whereabouts.Place}{clock}";
                var reason = $"{where}; a good player would be on the way to {to} ({length:0} units {direction})";
                _moves.Add(new MoveStep(asked, direction, dx / length, dy / length, 2, reason)
                {
                    Destination = new Destination($"{lane.Choice} lane", spot.X, spot.Y),
                    DistanceUnits = length,
                });
                _lastMoveAt = asked;
                Remember($"stepped toward {lane.Choice} lane", asked);
            });
    }

    /// <summary>A lane's minimap minions, as an option of the lane follow-up says them.</summary>
    private static string DescribeWave(WaveFacts? wave)
    {
        if (wave is null)
            return "";
        if (wave is { OurMinions: 0, TheirMinions: 0 })
            return "; the minimap shows no minions in it";
        var said = $"; minions on the minimap: {wave.OurMinions} ours, {wave.TheirMinions} theirs";
        if (wave.TheirFrontPlace is { } place)
            said += $", theirs {place}";
        if (wave.MeetAt is { } meet)
            return said + $", meeting {meet:0.00} of the way to the enemy nexus, "
                + (wave.MeetScreenDirection is { } way ? $"{wave.MeetUnitsAway:0} units away, {way} on the screen" : "where the player stands");
        return said + (wave.OurFront is { } ours ? $", ours pushed {ours:0.00} of the way" : $", theirs pushed to {wave.TheirFront:0.00} of the way");
    }

    // --- A wave at the player's turret: go and catch it ---

    /// <summary>
    /// How near an enemy wave the player already is when they are at it: about
    /// a screen's width, inside which the wave is already in view.
    /// </summary>
    private const double AtTheWaveUnits = 1500;

    /// <summary>
    /// Offered while the player is alive and placed, an enemy wave the
    /// minimap shows at one of their own turrets is more than
    /// <see cref="AtTheWaveUnits"/> away, and no movement click came in the
    /// last <see cref="JevOptions.MoveEverySeconds"/>. Whether it is theirs
    /// to catch is the root's call, and which one when more than one lane is
    /// crashing the follow-up's; a pick is one step on the ground toward that
    /// wave's front, and the next is decided afresh.
    /// </summary>
    private Branch? CatchWave(FrameEnvelope frame, ChampionRow self, Moment moment, double asked)
    {
        if (self.Alive == false || self is not { WorldX: { } x, WorldY: { } y })
            return null;
        if (Stepping(frame.VideoTime) || moment.Whereabouts is not { } whereabouts)
            return null;
        var crashing = whereabouts.Lanes
            .Where(l => l.Wave is { TheirFront: { } front, TheirFrontUnitsAway: > AtTheWaveUnits }
                && RiftMap.AtOurTurret(l.Lane, front))
            .ToArray();
        if (crashing.Length == 0)
            return null;

        var criteria = new ChoiceCriteria();
        foreach (var lane in crashing)
        {
            var allies = lane.AlliesThere.Count == 0 ? "none" : string.Join(", ", lane.AlliesThere);
            criteria[lane.Lane] = $"{lane.Lane} lane: the enemy wave is {lane.Wave!.TheirFrontPlace}, "
                + $"{lane.Wave.TheirFrontUnitsAway:0} units away, {lane.Wave.TheirFrontScreenDirection} on the screen; allies there: {allies}";
        }
        return new Branch("catch_wave", CoachQuestions.CatchWaveOption,
            crashing.Length > 1 ? q => q.Choice("tend_lane", CoachQuestions.TendLane, criteria) : null,
            response =>
            {
                var chosen = Picked(response, "tend_lane", crashing.Select(l => l.Lane).ToArray());
                if (crashing.FirstOrDefault(l => l.Lane == chosen) is not { Wave: { TheirFront: { } front } wave } lane)
                    return;
                var spot = RiftMap.At(lane.Lane, front);
                // World y grows north, screen y grows down: flip for the step.
                var (dx, dy) = (spot.X - x, -(spot.Y - y));
                var length = double.Hypot(dx, dy);
                var direction = ScreenDirections.Name(dx, dy);
                var alone = lane.AlliesThere.Count == 0 ? " with none of your team there" : "";
                var reason = $"the enemy wave is {wave.TheirFrontPlace} in {lane.Lane} lane{alone}; "
                    + $"a good player would be on the way to catch it ({length:0} units {direction})";
                _moves.Add(new MoveStep(asked, direction, dx / length, dy / length, 2, reason)
                {
                    Destination = new Destination($"{lane.Lane} lane", spot.X, spot.Y),
                    DistanceUnits = length,
                });
                _lastMoveAt = asked;
                Remember($"stepped toward {lane.Lane} lane's wave at your turret", asked);
            });
    }

    // --- Brush: out of sight ---

    /// <summary>
    /// How far from the player a patch of brush is near: a few seconds' walk,
    /// about half a screen's width. Farther than this, walking to it is a trip,
    /// not stepping into cover.
    /// </summary>
    private const double BrushNearUnits = 1200;

    /// <summary>How many patches near the player, nearest first, are offered.</summary>
    private const int BrushOptions = 3;

    /// <summary>
    /// Offered while the player is alive and placed outside the brush, the
    /// game clock is running, a patch lies within <see cref="BrushNearUnits"/>,
    /// and no movement click came in the last
    /// <see cref="JevOptions.MoveEverySeconds"/>. Whether hiding is worth it
    /// -- out of an enemy laner's sight, breaking a chase, waiting out of the
    /// open -- is the root's call, from <see cref="Moment.Brush"/>, and which
    /// patch the follow-up's; a pick is one step on the ground toward the
    /// nearest point well inside the grass, and the next is decided afresh.
    /// </summary>
    private Branch? HideInBrush(FrameEnvelope frame, ChampionRow self, Moment moment, double asked)
    {
        if (self.Alive == false || frame.GameTime is null || self is not { WorldX: { } x, WorldY: { } y })
            return null;
        if (Stepping(frame.VideoTime) || RiftBrush.At(x, y) is not null)
            return null;
        var near = NearBrushes(frame, self);
        if (near.Length == 0)
            return null;

        var criteria = new ChoiceCriteria();
        foreach (var (fact, _) in near)
            criteria[fact.Name] = DescribeBrush(fact);
        return new Branch("hide_in_brush", CoachQuestions.HideInBrushOption,
            near.Length > 1 ? q => q.Choice("brush", CoachQuestions.WhichBrush, criteria) : null,
            response =>
            {
                var chosen = Picked(response, "brush", near.Select(n => n.Fact.Name).ToArray());
                if (near.FirstOrDefault(n => n.Fact.Name == chosen) is not { Patch: { } patch } brush)
                    return;
                var spot = patch.Inside(x, y);
                // World y grows north, screen y grows down: flip for the step.
                var (dx, dy) = (spot.X - x, -(spot.Y - y));
                var length = double.Hypot(dx, dy);
                if (length < 1)
                    return;
                var direction = ScreenDirections.Name(dx, dy);
                var seen = moment.VisibleEnemies.Count switch
                {
                    0 => "",
                    1 => $" and {moment.VisibleEnemies[0].Champion} can see you out here",
                    _ => $" and {string.Join(", ", moment.VisibleEnemies.Select(e => e.Champion))} can see you out here",
                };
                if (seen.Length == 0 && moment.Whereabouts is { StoodStillForSeconds: >= 2 and var still })
                    seen = $" and you have stood in the open for {still:0.0}s";
                var reason = $"{patch.Name} is {brush.Fact.DistanceUnits:0} units {direction}{seen}; "
                    + "a good player would stand in the brush, where no enemy outside it can see them";
                _moves.Add(new MoveStep(asked, direction, dx / length, dy / length, 2, reason)
                {
                    Destination = new Destination(patch.Name, spot.X, spot.Y),
                    DistanceUnits = length,
                });
                _lastMoveAt = asked;
                Remember($"stepped toward {patch.Name}", asked);
            });
    }

    // --- Said, not yet done: running, falling back, recalling, buying ---

    /// <summary>
    /// Offered while the player is alive and placed with an enemy champion on
    /// the screen. The ghost's hands have no move for it yet, so a pick is a
    /// cue naming it, said no more often than
    /// <see cref="JevOptions.SayEverySeconds"/>.
    /// </summary>
    private Branch? RunAway(FrameEnvelope frame, ChampionRow self, Moment moment, double asked) =>
        self.Alive != false && self is { WorldX: not null, WorldY: not null } && moment.VisibleEnemies.Count > 0
            ? Said(frame, "run_away", CoachQuestions.RunAwayOption, "run away", moment, asked)
            : null;

    /// <summary>
    /// Offered while the player is alive and placed out of their base with an
    /// enemy champion on the screen. Said, not yet clicked, as for
    /// <see cref="RunAway"/>.
    /// </summary>
    private Branch? GoToTurret(FrameEnvelope frame, ChampionRow self, Moment moment, double asked) =>
        self.Alive != false && moment.VisibleEnemies.Count > 0 && moment.Whereabouts is { } where && !AtHome(where.Place)
            ? Said(frame, "go_to_turret", CoachQuestions.GoToTurretOption, "fallen back to a turret", moment, asked)
            : null;

    /// <summary>
    /// Offered while the player is alive and placed out of their base, with
    /// the game clock running. Said, not yet keyed, as for <see cref="RunAway"/>.
    /// </summary>
    private Branch? Recall(FrameEnvelope frame, ChampionRow self, Moment moment, double asked) =>
        self.Alive != false && frame.GameTime is not null && moment.Whereabouts is { } where && !AtHome(where.Place)
            ? Said(frame, "recall", CoachQuestions.RecallOption, "recalled", moment, asked)
            : null;

    /// <summary>
    /// Offered while the player is alive in the fountain, where the shop is.
    /// Said, not yet done, as for <see cref="RunAway"/>.
    /// </summary>
    private Branch? Buy(FrameEnvelope frame, ChampionRow self, Moment moment, double asked) =>
        self.Alive != false && moment.Whereabouts is { Place: RiftMap.FountainPlace }
            ? Said(frame, "buy", CoachQuestions.BuyOption, "bought", moment, asked)
            : null;

    private static bool AtHome(string place) => place is RiftMap.FountainPlace or RiftMap.BasePlace;

    /// <summary>
    /// A branch the ghost's hands have no move for yet: a pick is a cue
    /// naming what a good player would have done, with where the player is
    /// and what they face, and the coach remembers saying it. Not offered for
    /// <see cref="JevOptions.SayEverySeconds"/> after it was last said, so a
    /// moment that goes on being right for it is not said four times a second.
    /// </summary>
    private Branch? Said(FrameEnvelope frame, string option, string criterion, string done, Moment moment, double asked)
    {
        if (_lastSaid.TryGetValue(option, out var at) && frame.VideoTime - at < _options.SayEverySeconds)
            return null;
        return new Branch(option, criterion, null, _ =>
        {
            List<string> facts = [];
            if (moment.Whereabouts is { } where)
                facts.Add($"you are in {where.Place}{(moment.GameClock is { } time ? $" at {time}" : "")}");
            if (moment.Player?.Health is { } health)
                facts.Add($"{health:0%} health");
            facts.Add(moment.VisibleEnemies.Count switch
            {
                0 => "no enemy on the screen",
                _ => string.Join(", ", moment.VisibleEnemies.Select(e => $"{e.Champion} {e.DistanceUnits:0} units {e.ScreenDirection}")),
            });
            _cues.Add(new CoachCue(asked, 2, $"coach would have {done} here: {string.Join("; ", facts)}"));
            _lastSaid[option] = asked;
            Remember($"said {option.Replace('_', ' ')}", asked);
        });
    }

    /// <summary>A patch near the player, as an option of the brush question says it.</summary>
    private static string DescribeBrush(BrushNear brush)
    {
        var said = $"{brush.Name}: "
            + (brush.FaceCheck is { } why ? $"a face-check ({why}), " : "")
            + $"{brush.Kind}, {brush.DistanceUnits:0} units {brush.ScreenDirection ?? "away"}";
        if (brush.NearestEnemyMinionUnits is { } minion)
            said += $", {minion:0} units from the nearest enemy minion";
        said += brush.TowardYourBase ? ", toward your base" : ", away from your base";
        if (brush.AheadOfYourMinionsUnits is < 0 and var behind)
            said += $", {-behind:0} units behind your minions' front";
        if (brush.NearestVisibleEnemyUnits is { } enemy)
            said += $", the nearest enemy champion {enemy:0} units from it";
        if (brush.AlliesInIt is { Count: > 0 } allies)
            said += $", {string.Join(", ", allies)} in it";
        return said;
    }

    /// <summary>
    /// The patches of brush within <see cref="BrushNearUnits"/> of the player,
    /// nearest first, with what lies around each: named "brush 1" and on in
    /// that order, the names the brush question's options and the state share.
    /// The patch the player stands in is not among them.
    /// </summary>
    private (BrushNear Fact, RiftBrush.Patch Patch)[] NearBrushes(FrameEnvelope frame, ChampionRow self)
    {
        if (self is not { WorldX: { } x, WorldY: { } y })
            return [];
        var standingIn = RiftBrush.At(x, y);
        var enemies = Visible(frame, self).Select(e => (X: e.WorldX!.Value, Y: e.WorldY!.Value)).ToArray();
        var minions = (Carried(frame, r => r.Minions) ?? [])
            .Where(m => m.Team != MinionTeam.Blue && m is { WorldX: not null, WorldY: not null })
            .Select(m => (X: m.WorldX!.Value, Y: m.WorldY!.Value))
            .ToArray();
        var allies = frame.Champions
            .Where(c => c.Team == self.Team && c.TrackId != self.TrackId && c.Alive != false && c is { WorldX: not null, WorldY: not null })
            .ToArray();
        var home = double.Hypot(x - RiftMap.Fountain.X, y - RiftMap.Fountain.Y);
        return RiftBrush.Near(x, y, BrushNearUnits)
            .Where(n => n.Patch != standingIn)
            .Take(BrushOptions)
            .Select((n, i) =>
            {
                var patch = n.Patch;
                var (_, cx, cy) = patch.Nearest(x, y);
                double? ahead = patch.Lane is { } lane && OurFront(frame, lane) is { } front
                    ? Math.Round((RiftMap.Along(lane, patch.X, patch.Y).Progress - front) * RiftMap.Length(lane))
                    : null;
                var inIt = allies
                    .Where(a => patch.Nearest(a.WorldX!.Value, a.WorldY!.Value).Distance <= RiftBrush.InBrushUnits)
                    .Select(a => a.Champion ?? $"track {a.TrackId}")
                    .ToArray();
                var cover = TurretCover(patch.X, patch.Y)?.Said;
                var faceCheck = ahead > 0 ? $"{ahead:0} units in front of your minions"
                    : cover is not null ? $"under {cover}"
                    : patch.Place.StartsWith("their jungle") ? "in the enemy's jungle"
                    : null;
                var fact = new BrushNear(
                    $"brush {i + 1}", patch.Name, patch.Place, Math.Round(n.Distance),
                    n.Distance < 1 ? null : ScreenDirections.NameOfWorldOffset(cx - x, cy - y),
                    double.Hypot(patch.X - RiftMap.Fountain.X, patch.Y - RiftMap.Fountain.Y) < home)
                {
                    AheadOfYourMinionsUnits = ahead,
                    UnderTheirTurret = cover,
                    NearestVisibleEnemyUnits = Nearest(patch, enemies),
                    NearestEnemyMinionUnits = Nearest(patch, minions),
                    AlliesInIt = inIt.Length > 0 ? inIt : null,
                    FaceCheck = faceCheck,
                };
                return (fact, patch);
            })
            .ToArray();

        static double? Nearest(RiftBrush.Patch patch, (double X, double Y)[] points) =>
            points.Length == 0 ? null : Math.Round(points.Min(p => patch.Nearest(p.X, p.Y).Distance));
    }

    /// <summary>
    /// The enemy turret covering a spot, as the facts say it ("their bot outer
    /// turret, 520 units from it"), with the turret's own spot; null when none
    /// does. A turret the minimap shows fallen covers nothing; without the
    /// turrets read, every turret is taken to stand.
    /// </summary>
    private (string Said, double X, double Y)? TurretCover(double x, double y)
    {
        Func<RiftMap.TurretSpot, bool?>? standing = _turrets is null ? null
            : spot => _turrets.FirstOrDefault(t => t.Team == MinionTeam.Red && t.Lane == spot.Lane && t.Tier == spot.Tier
                && (spot.Side is null || t.Side == spot.Side))?.Standing;
        return RiftMap.TheirTurretCovering(x, y, standing) is { } cover
            ? ($"{cover.Turret.Name}, {cover.Distance:0} units from it", cover.Turret.X, cover.Turret.Y)
            : null;
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
            _lastMoveAt = Math.Max(_lastMoveAt, stamp);
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

    // --- A skill point waiting: noted for the root to offer ---

    /// <summary>
    /// The HUD's level-up chevrons lighting: a point to spend, and the slots
    /// the game would accept it in, read off the buttons themselves (the
    /// ultimate lights only at 6, 11 and 16, and a full ability never). It is
    /// noted, not asked about: the root offers <c>level_up</c> while it waits
    /// (<see cref="LevelUp"/>). A point announced again with a new set (the
    /// ultimate lighting at 6 under a point still held) is a new point, and an
    /// answer about the old one is dropped; the set the coach already knows,
    /// announced again, changes nothing.
    /// </summary>
    private void OnSkillPoint(GameEvent evt)
    {
        var slots = LitSlots(evt.Slots);
        if (slots.Length == 0)
            return;
        if (_point is { } known && known.Slots.SequenceEqual(slots))
            return;
        _point = (_point?.Since ?? evt.VideoTime, slots, ++_pointId);
        _pointAsked = false;
    }

    /// <summary>
    /// The slots a waiting point lights, off the self row when it carries
    /// them, since the set can change under a held point before the feed
    /// announces it. A new set is a new point.
    /// </summary>
    private void TrackPoint(ChampionRow self)
    {
        if (_point is not { } point || LitSlots(self.Learnable) is not { Length: > 0 } lit || lit.SequenceEqual(point.Slots))
            return;
        _point = (point.Since, lit, ++_pointId);
    }

    /// <summary>The feed's slot strings in the HUD's order, anything unrecognised left out.</summary>
    private static string[] LitSlots(string[]? slots) =>
        slots is null ? [] : AbilityKits.Slots.Where(slots.Contains).ToArray();

    /// <summary>
    /// The chevrons clearing: the point went in. If the coach pressed the
    /// chord for it, that is the coach's placement, counted now that the HUD
    /// shows it done; if it did not, the player spent it themselves and it is
    /// in nobody's count and not asked about again. Either way the point is
    /// gone, so an answer still on its way is about nothing. The HUD's
    /// `held_for` runs from the point's first reading to its spending, so the
    /// reader's lag cancels out of it; it is said as a note, for the log. A
    /// point spent from the death screen is reported on respawn, since the
    /// reader is off while dead.
    /// </summary>
    private void OnSkillSpent(GameEvent evt)
    {
        var held = evt.HeldFor is { } seconds ? $" after {seconds:0.0}s" : "";
        if (_pressedFor.Count > 0)
        {
            foreach (var slot in _pressedFor)
                _pointsPlaced[slot] = _pointsPlaced.GetValueOrDefault(slot) + 1;
            _cues.Add(new CoachCue(evt.VideoTime, 1,
                $"the point went in{held}; the coach's Ctrl+{string.Join(", Ctrl+", _pressedFor)} counted as its placement"));
            _pressedFor.Clear();
        }
        else
            _cues.Add(new CoachCue(evt.VideoTime, 1, $"the player put the point in themselves{held}; it is in nobody's count"));
        _point = null;
    }

    // --- Turrets: state for the facts, the events only logged ---

    /// <summary>
    /// A turret falling or standing again, said in the log. No question is
    /// asked of it: the frames carry the same change as state, which every
    /// question's lanes are told, and the event arrives about five seconds
    /// after the fact.
    /// </summary>
    private void OnTurret(GameEvent evt)
    {
        var whose = evt.Team == MinionTeam.Blue ? "your" : "their";
        var which = evt.Tier == TurretTier.Nexus
            ? $"{(evt.Side is { } side ? side + " " : "")}nexus turret"
            : $"{evt.Lane} {evt.Tier} turret";
        var what = evt.Kind == EventKind.TurretRebuilt ? "stands again" : "fell";
        _cues.Add(new CoachCue(evt.VideoTime, 1, $"{whose} {which} {what}"));
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
        _cues.Add(new CoachCue(videoTime, 1, $"the coach model did not answer: {why}", Failure: true));
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
        MinionFacts? minions = null;
        AttackFacts? attack = null;
        BrushFacts? brush = null;
        if (frame is not null && self is not null)
        {
            whereabouts = Whereabouts(frame, self, now);
            if (self is { WorldX: { } bx, WorldY: { } by })
                brush = new BrushFacts(RiftBrush.At(bx, by)?.Name, NearBrushes(frame, self).Select(n => n.Fact).ToArray());
            minions = MinionsOnScreen(frame, self);
            var attackRange = AbilityKits.AttackRange(champion);
            if (self is { WorldX: { } sx, WorldY: { } sy })
            {
                var cover = TurretCover(sx, sy);
                attack = new AttackFacts(attackRange, NearMinions(frame, self).Select(m => m.Fact).ToArray())
                {
                    YouUnderTheirTurret = cover?.Said,
                    YourMinionsUnderThatTurret = cover is { } c
                        ? (Carried(frame, r => r.Minions) ?? []).Count(m => m.Team == MinionTeam.Blue
                            && m is { WorldX: { } mx, WorldY: { } my } && double.Hypot(mx - c.X, my - c.Y) <= RiftMap.TurretRange)
                        : null,
                };
            }
            // An enemy is told by its distance and direction from the player,
            // so a player with no place on the map is told of none.
            var placed = self is { WorldX: not null, WorldY: not null };
            foreach (var row in (placed ? Visible(frame, self) : []).OrderBy(r => Distance(self, r)))
            {
                var distance = Distance(self, row)!.Value;
                var inRange = AbilityKits.Slots
                    .Where(s => AbilityKits.For(champion, s)?.Range is { } range && distance <= range)
                    .ToArray();
                enemies.Add(new EnemyFacts(
                    row.Champion ?? $"track {row.TrackId}", distance, Direction(self, row)!,
                    Math.Round(now - _visibleSince.GetValueOrDefault(row.TrackId, now), 1),
                    row.Health, row.Level, inRange,
                    _withinReachSince.TryGetValue(row.TrackId, out var since) ? Math.Round(now - since, 1) : null)
                {
                    InAttackRange = attackRange is { } r ? distance <= r : null,
                    UnderTheirTurret = TurretCover(row.WorldX!.Value, row.WorldY!.Value)?.Said,
                });
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
            Minions = minions,
            Attack = attack,
            Brush = brush,
            Coach = _recent.Select(r => new RecentAction(r.Did, Math.Round(now - r.At, 1))).ToArray(),
            Occasion = occasion,
            SkillPoint = SkillPointNow(self, now),
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
        var dots = Carried(frame, r => r.MinionDots);
        var lanes = RiftMap.Lanes.Select(lane =>
        {
            var toward = RiftMap.Toward(lane, x, y);
            var there = placed
                .Where(c => RiftMap.Toward(lane, c.WorldX!.Value, c.WorldY!.Value).Distance <= RiftMap.LaneHalfWidth)
                .Select(c => c.Champion ?? $"track {c.TrackId}")
                .ToArray();
            return new LaneFacts(lane, Math.Round(toward.Distance),
                toward.Distance < 1 ? null : ScreenDirections.NameOfWorldOffset(toward.X - x, toward.Y - y), there)
            {
                // Without the turrets read, a wave is placed by their spots alone.
                Wave = Wave(lane, dots, x, y, _turrets is null ? null : tier => Standing(MinionTeam.Blue, lane, tier)),
                YourTurrets = LaneTurrets(MinionTeam.Blue, lane),
                TheirTurrets = LaneTurrets(MinionTeam.Red, lane),
            };
        }).ToArray();
        var still = _restAt is null ? 0 : Math.Max(0, Math.Round(now - _stillSince, 1));
        return new WhereaboutsFacts(RiftMap.Place(x, y), still, lanes);
    }

    /// <summary>
    /// Whether one side's turret of a tier in a lane stands, off the minimap:
    /// null when it has not been called, or the minimap has not been read for
    /// turrets at all.
    /// </summary>
    private bool? Standing(string team, string lane, string tier) =>
        _turrets?.FirstOrDefault(t => t.Team == team && t.Lane == lane && t.Tier == tier)?.Standing;

    /// <summary>One side's three turrets in a lane, as the facts say them; null when the minimap was not read for turrets.</summary>
    private LaneTurretFacts? LaneTurrets(string team, string lane)
    {
        if (_turrets is null)
            return null;
        string Say(string tier) => Standing(team, lane, tier) switch
        {
            true => "standing",
            false => "fallen",
            null => "not seen",
        };
        return new LaneTurretFacts(Say(TurretTier.Outer), Say(TurretTier.Inner), Say(TurretTier.Inhibitor));
    }

    /// <summary>A caster minion's attack range, in game units: how near an enemy minion is to be in reach of the player.</summary>
    private const double CasterMinionRange = 550;

    /// <summary>
    /// One of the minion arrays, off whichever row carries it. The feed puts
    /// them on the frame's is_self row, which is the camera's and can flap
    /// onto an ally while <see cref="Self"/> holds the player; the minions are
    /// the screen's and the map's either way, and every distance is taken in
    /// world units from the player's own row.
    /// </summary>
    private static T[]? Carried<T>(FrameEnvelope frame, Func<ChampionRow, T[]?> array) =>
        frame.Champions.Select(array).FirstOrDefault(a => a is not null);

    /// <summary>
    /// A lane's minions off the minimap: each dot placed in the lane it stands
    /// in (none in either base, where the lanes meet), and each side's front,
    /// its dot furthest toward the other side. Null when the minimap was not
    /// read for minions, or its dots could not be placed on the map.
    /// </summary>
    private static WaveFacts? Wave(string lane, MinionDot[]? dots, double x, double y, Func<string, bool?>? standing)
    {
        if (dots is null || (dots.Length > 0 && dots.All(d => d.WorldX is null || d.WorldY is null)))
            return null;
        List<double> ours = [], theirs = [];
        foreach (var dot in dots)
        {
            if (dot is not { WorldX: { } dx, WorldY: { } dy } || RiftMap.LaneOf(dx, dy) != lane)
                continue;
            (dot.Team == MinionTeam.Blue ? ours : theirs).Add(RiftMap.Along(lane, dx, dy).Progress);
        }
        double? ourFront = ours.Count > 0 ? Math.Round(ours.Max(), 2) : null;
        double? theirFront = theirs.Count > 0 ? Math.Round(theirs.Min(), 2) : null;
        var facts = new WaveFacts(ours.Count, theirs.Count, ourFront, theirFront, null, null, null);
        if (theirs.Count > 0)
        {
            var (tx, ty) = RiftMap.At(lane, theirs.Min());
            var toTheirs = double.Hypot(tx - x, ty - y);
            facts = facts with
            {
                TheirFrontPlace = RiftMap.EnemyFrontPlace(lane, theirs.Min(), standing),
                TheirFrontUnitsAway = Math.Round(toTheirs),
                TheirFrontScreenDirection = toTheirs < 1 ? null : ScreenDirections.NameOfWorldOffset(tx - x, ty - y),
            };
        }
        if (ourFront is not { } o || theirFront is not { } t)
            return facts;
        var meet = Math.Round((o + t) / 2, 2);
        var (mx, my) = RiftMap.At(lane, meet);
        var away = double.Hypot(mx - x, my - y);
        return facts with
        {
            MeetAt = meet, MeetUnitsAway = Math.Round(away),
            MeetScreenDirection = away < 1 ? null : ScreenDirections.NameOfWorldOffset(mx - x, my - y),
        };
    }

    /// <summary>
    /// The minions on the player's screen, measured from where the player
    /// stands: how many of each side's, how near the enemy's are, and how far
    /// in front of their own foremost minion in the lane the player is. Null
    /// when the bars were not read or the player has no place on the map.
    /// </summary>
    private static MinionFacts? MinionsOnScreen(FrameEnvelope frame, ChampionRow self)
    {
        if (Carried(frame, r => r.Minions) is not { } minions || self is not { WorldX: { } x, WorldY: { } y })
            return null;
        var ours = minions.Where(m => m.Team == MinionTeam.Blue).ToArray();
        var theirs = minions.Where(m => m.Team != MinionTeam.Blue).ToArray();
        var reach = theirs
            .Where(m => m is { WorldX: not null, WorldY: not null })
            .Select(m => double.Hypot(m.WorldX!.Value - x, m.WorldY!.Value - y))
            .ToArray();
        double? ahead = RiftMap.LaneOf(x, y) is { } lane && OurFront(frame, lane) is { } front
            ? Math.Round((RiftMap.Along(lane, x, y).Progress - front) * RiftMap.Length(lane))
            : null;
        return new MinionFacts(ours.Length, theirs.Length,
            reach.Length == 0 ? null : Math.Round(reach.Min()), reach.Count(d => d <= CasterMinionRange), ahead);
    }

    /// <summary>
    /// How far along a lane the player's own foremost minion on the screen
    /// has pushed (<see cref="RiftMap.Along"/>); null when none of theirs is
    /// on the screen in that lane, or the bars were not read.
    /// </summary>
    private static double? OurFront(FrameEnvelope frame, string lane)
    {
        var fronts = (Carried(frame, r => r.Minions) ?? [])
            .Where(m => m.Team == MinionTeam.Blue && m is { WorldX: not null, WorldY: not null })
            .Select(m => RiftMap.Along(lane, m.WorldX!.Value, m.WorldY!.Value))
            .Where(a => a.Distance <= RiftMap.LaneHalfWidth)
            .Select(a => a.Progress)
            .ToArray();
        return fronts.Length > 0 ? fronts.Max() : null;
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
