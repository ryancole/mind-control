using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Jev;
using MindControl.Feed;

namespace MindControl.Policy;

public sealed record JevOptions
{
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
    /// How long, in video seconds, the recall channel takes. For this long
    /// after the coach pressed it, with no enemy champion on the screen, the
    /// root offers nothing that would move the player and break it, and not
    /// the recall again.
    /// </summary>
    public double RecallChannelSeconds { get; init; } = 8;

    /// <summary>
    /// The least gold, read off the HUD, that buys something worth a trip to
    /// the shop: 300 is the price of the cheapest basic components (boots,
    /// cloth armor, a rejuvenation bead), below which only potions and wards
    /// are left. While the player's gold is this or more the fountain keeps
    /// offering the buy; below it the offer stops. Set high rather than low:
    /// offering a buy the player cannot afford is the costly mistake, while
    /// holding one back only waits for the next reading.
    /// </summary>
    public int BuyFloorGold { get; init; } = 300;

    /// <summary>
    /// How long, in video seconds, after the coach put in every point it
    /// counts waiting, before it offers to show the point again while the HUD
    /// still lights it. The coach's chord never reaches the game it watches,
    /// so a point the player leaves unspent stays lit; for as long as it does,
    /// the coach shows it again at this pace, not four times a second.
    /// </summary>
    public double PointAgainEverySeconds { get; init; } = 5;

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
/// player (the game's majority of is_self rows, with the pipeline's identity
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

    // Perception: the corner of the map the player plays from, off the last
    // fountain they or an ally were seen standing in, and whether one has been
    // seen yet. Until one is, the map is taken from the blue side. It outlives
    // a resync, since a gap is almost always the same game, and is forgotten
    // at a new game (NewGame), which starts with the whole team in its
    // fountain to say the side again.
    private MapSide _side = MapSide.Blue;
    private bool _sideSeen;

    /// <summary>The map as seen from the side the player plays.</summary>
    private RiftMap Map => RiftMap.From(_side);

    // Perception: the skill point the HUD shows waiting, when one is -- since
    // when the feed has shown it, which slots it lights, and a number that
    // tells an answer whether it is still the point that was asked about --
    // the levels the self row read while it waited (the first, and the
    // highest since: every level gained under a lit chevron is one more point
    // stacked, which the HUD does not show), and the chords the coach has
    // pressed for them that the HUD has not yet shown gone in, one per point,
    // with when the last was pressed. The first level a point was asked about
    // at, since which the coach has been watching, and where the coach put
    // each point: those are the coach's own placements, counted when the HUD
    // shows the point spent, a lower bound on the ability's points and never
    // a reading of the HUD's rank pips, which the feed does not carry.
    private (double Since, string[] Slots, int Id)? _point;
    private int _pointId;
    private bool _pointAsked;
    private int? _pointFirstLevel;
    private int? _pointTopLevel;
    private readonly List<string> _pressedFor = [];
    private double _lastChordAt = double.NegativeInfinity;
    private int? _firstLevelAsked;
    private readonly Dictionary<string, int> _pointsPlaced = [];

    // Perception: the farm. The creep score the HUD last printed, and the
    // enemy minions that died on the screen in the last minute, by when their
    // bar was last seen and whether the player's score rose for it.
    private int? _cs;
    private readonly List<(double At, bool Taken)> _farm = [];

    // The roots of the last few seconds, for what a missed last hit is put
    // down to: whether attack_minion was offered (or what closed it) and what was
    // picked. A pick is filled in when its answer lands.
    private sealed class RootSeen(double at, string? attackGate)
    {
        public double At { get; } = at;
        public string? AttackGate { get; } = attackGate;
        public string? Pick { get; set; }
    }
    private readonly List<RootSeen> _roots = [];

    // What the coach said since the last drain.
    private readonly List<CoachCue> _cues = [];
    private readonly List<KeyPress> _keys = [];
    private readonly List<MoveStep> _moves = [];

    // Questions in flight. Answers land here from the client's thread and are
    // applied on the reactor's, in Settle.
    private readonly ConcurrentQueue<Action> _arrivals = new();
    private int _generation;
    private bool _deciding;

    // Activity since the health log last drained it (DrainActivity): frames
    // seen and how many had no self in them, whose seat the coach took the
    // latest one to be, roots with nothing to offer, roots asked, and each
    // answer by its pick ("carry_on", a pick below DecideAt as "weak").
    private int _framesSeen, _framesWithoutSelf, _nothingToOffer, _rootsAsked;
    private string? _seatSeen;
    private readonly SortedDictionary<string, int> _answers = new(StringComparer.Ordinal);
    private double _lastDecideAt = double.NegativeInfinity;

    // Last hits since the health log last drained them, and each miss by what
    // it was put down to; and the totals for this game, which outlive a
    // resync as the votes do and go with them at a new game.
    private int _takenSeen, _missedSeen, _takenTotal, _missedTotal;
    private readonly SortedDictionary<string, int> _missedWhy = new(StringComparer.Ordinal);

    // What the coach last did, by kind, for the pace of the next.
    private double _lastMoveAt = double.NegativeInfinity;
    private double _recallUntil = double.NegativeInfinity;
    private double _lastAttackAt = double.NegativeInfinity;
    private readonly Dictionary<string, double> _lastSaid = [];
    private bool _boughtThisVisit;

    // The buy's gold gate. Whether the feed reads gold at all; the last
    // reading, and whether it covered the floor. A frame without a reading
    // keeps both: an unread box is unknown, never zero.
    private bool _hasGold;
    private int? _gold;
    private bool? _goldCovers;

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

    /// <summary>
    /// Each question as it is sent and as its answer is applied (see
    /// <see cref="Thought"/>), and each root that had nothing to offer: the
    /// tree the coach walked, for the brain view. Raised on the caller's
    /// thread, from <see cref="OnFrame"/> and <see cref="OnEvent"/>. It
    /// decides nothing.
    /// </summary>
    public event Action<Thought>? Thinking;

    /// <summary>
    /// Each enemy minion that died on the player's screen low enough to be a
    /// last hit, taken or missed, with what a miss is put down to: the
    /// measure of the farming, for the audit. It decides nothing.
    /// </summary>
    public event Action<FarmOutcome>? Farmed;

    private int _thoughtId;
    private double _lastIdleAt = double.NegativeInfinity;

    public void Configure(Meta meta)
    {
        // A false flag means the stage did not run, not that nothing happened.
        // Without it the questions that need it are never asked, so say why,
        // once, rather than be a silent coach that looks broken.
        _hasGold = meta.HasGold;
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
        _boughtThisVisit = false;
        _gold = null;
        _goldCovers = null;
        _recallUntil = double.NegativeInfinity;

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
        _cs = null;
        _farm.Clear();
        _roots.Clear();
        ForgetPoint();
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
                TrackGold(self);
                // A point the baseline shows waiting is state the feed will
                // not announce again; it is asked about from the frames.
                TrackPoint(latest, self);
            }
        }
        // _selfVotes survive: identity outlives a gap. A new game is another
        // matter (NewGame).
    }

    public void NewGame(int game)
    {
        Resync(null);
        // Who the player is, the side they play from and the farm's totals
        // outlive a gap but not a match: next game's player may be on
        // another champion, on the other side, with none of this one's last
        // hits. Track ids never recur across games, but the maps keyed by
        // them were emptied by the resync all the same.
        _selfVotes.Clear();
        _selfName = null;
        _lastCorrection = null;
        _side = MapSide.Blue;
        _sideSeen = false;
        _takenSeen = _missedSeen = _takenTotal = _missedTotal = 0;
        _missedWhy.Clear();
        _lastIdleAt = double.NegativeInfinity;
        _cues.Add(new CoachCue(_frame?.VideoTime ?? 0, 1, $"game {game} began: the last one is forgotten"));
    }

    public void OnFrame(FrameEnvelope frame)
    {
        Settle();
        _frame = frame;
        VoteSelf(frame);
        TrackVisibility(frame);
        TrackSide(frame);
        if (Carried(frame, r => r.Turrets) is { } turrets)
            _turrets = turrets;
        _minionTracks.Update(frame.VideoTime, Carried(frame, r => r.Minions));
        // The HUD's score, on whichever row carries it: it is the player's own.
        if (frame.Champions.Select(r => r.Cs).FirstOrDefault(cs => cs is not null) is { } cs)
            _cs = cs;
        _framesSeen++;
        var seat = Self();
        _seatSeen = seat is null ? null : seat.Champion ?? $"unnamed track {seat.TrackId}";
        if (seat is null)
            _framesWithoutSelf++;
        if (seat is { } self)
        {
            if (self.Resource is { } resource)
                _resource = resource;
            if (self.Health is { } health)
                _health = health;
            TrackGold(self);
            TrackReach(frame, self);
            TrackStillness(frame, self);
            TrackPoint(frame, self);
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
            case EventKind.LastHit:
            case EventKind.MissedCs:
                OnFarmed(evt);
                break;
        }
        Settle();
    }

    public string? DrainActivity()
    {
        if (_framesSeen == 0)
            return null;
        var seat = _framesWithoutSelf == _framesSeen ? $"no player row in {_framesSeen} frames"
            : _framesWithoutSelf > 0 ? $"player {_seatSeen ?? "lost"}, missing from {_framesWithoutSelf} of {_framesSeen} frames"
            : $"player {_seatSeen}";
        var answers = _answers.Count == 0 ? "" : $" ({string.Join(", ", _answers.Select(a => $"{a.Key} {a.Value}"))})";
        var nothing = _nothingToOffer > 0 ? $", nothing to offer {_nothingToOffer}" : "";
        var line = $"{seat}; asked {_rootsAsked}{answers}{nothing}";
        if (_takenSeen + _missedSeen > 0)
        {
            var why = _missedWhy.Count == 0 ? "" : $" ({string.Join(", ", _missedWhy.Select(w => $"{w.Key} {w.Value}"))})";
            line += $"; farm: {_takenSeen} taken, {_missedSeen} missed{why}";
        }
        if (_takenTotal + _missedTotal > 0)
            line += $"; last hits {_takenTotal} of {_takenTotal + _missedTotal} this game";
        _framesSeen = _framesWithoutSelf = _nothingToOffer = _rootsAsked = 0;
        _takenSeen = _missedSeen = 0;
        _answers.Clear();
        _missedWhy.Clear();
        return line;
    }

    private void Tally(string answer) => _answers[answer] = _answers.GetValueOrDefault(answer) + 1;

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
        string Option, string Criterion, Action<Questions>? FollowUp, Action<SystemOneResponse> Act)
    {
        /// <summary>What closed the branch, when the moment did not offer it; null when it is offered.</summary>
        public string? Gate { get; init; }

        /// <summary>The branch's one candidate, when it had only one and so asks no follow-up; for the brain view.</summary>
        public string? Only { get; init; }
    }

    /// <summary>A branch the moment does not offer, and why: nothing is asked of it, and the brain view shows the gate.</summary>
    private static Branch Closed(string option, string why) => new(option, "", null, _ => { }) { Gate = why };

    /// <summary>What keeps a player from any branch that moves or aims: being dead, or off the map.</summary>
    private static string? Unplaced(ChampionRow self) =>
        self.Alive == false ? "dead" : self is { WorldX: null } or { WorldY: null } ? "not placed on the map" : null;

    /// <summary>The gate of a movement branch while the last movement click is less than a second old.</summary>
    private string SteppedAgo(double now) => $"stepped {now - _lastMoveAt:0.0}s ago; a step a second";

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
        // Channelling a recall: only what does not move the player, until an
        // enemy shows up or the channel is done.
        var recalling = Recalling(frame, moment);
        var considered = new[]
        {
            LevelUp(self, moment, asked),
            RunAway(frame, self, moment, asked),
            UseAbility(frame, self, moment, asked),
            AttackMinion(frame, self, asked),
            AttackChampion(frame, self, asked),
            StepBack(frame, self, moment, asked),
            HideInBrush(frame, self, moment, asked),
            Recall(frame, self, moment, asked),
            Buy(frame, self, moment, asked),
            WalkToLane(frame, self, moment, asked),
        };
        if (recalling)
            considered = considered
                .Select(b => b.Gate is null && b.Option != "level_up" ? Closed(b.Option, "channelling a recall") : b)
                .ToArray();
        var branches = considered.Where(b => b.Gate is null).ToArray();
        var root = new RootSeen(asked, considered.First(b => b.Option == "attack_minion").Gate);
        _roots.RemoveAll(r => asked - r.At > RootsKeptSeconds);
        _roots.Add(root);
        if (branches.Length == 0)
        {
            root.Pick = "nothing to offer";
            _nothingToOffer++;
            // Nothing is asked, so nothing paces this: the brain view hears
            // of it no more often than a root would be asked.
            if (Thinking is not null && asked - _lastIdleAt >= _options.AskEverySeconds)
            {
                _lastIdleAt = asked;
                Thinking(new Thought
                {
                    Id = ++_thoughtId, Phase = "idle", Occasion = "decide", VideoTime = asked,
                    Branches = Considered(considered, new Dictionary<string, string>()), Mode = recalling ? "channelling a recall" : null,
                    State = moment, DecideAt = _options.DecideAt,
                });
            }
            return;
        }

        var criteria = new ChoiceCriteria { [CarryOn] = CoachQuestions.CarryOnOption };
        foreach (var branch in branches)
            criteria[branch.Option] = branch.Criterion;
        var questions = new Questions().Choice("decide", CoachQuestions.Decide, criteria);
        // Which follow-up each branch added, for the brain view's tree.
        Dictionary<string, string> followUps = [];
        foreach (var branch in branches)
        {
            var before = questions.Keys.ToHashSet();
            branch.FollowUp?.Invoke(questions);
            if (questions.Keys.FirstOrDefault(k => !before.Contains(k)) is { } added)
                followUps[branch.Option] = added;
        }

        _deciding = true;
        _lastDecideAt = asked;
        _rootsAsked++;
        var thought = new Thought
        {
            Occasion = "decide", VideoTime = asked, Branches = Considered(considered, followUps),
            Mode = recalling ? "channelling a recall" : null, DecideAt = _options.DecideAt,
        };
        Ask("decide", moment, questions, asked, NowRequest, released: () => _deciding = false, thought, answered: response =>
        {
            if (!response.TryGet<ChoiceAnswer>("decide", out var decide))
            {
                Tally("unreadable");
                root.Pick = "unreadable";
                return "unreadable";
            }
            if (decide!.Choice == CarryOn)
            {
                Tally(CarryOn);
                root.Pick = CarryOn;
                return CarryOn;
            }
            if (decide.Probabilities.GetValueOrDefault(decide.Choice) < _options.DecideAt)
            {
                Tally($"{decide.Choice} (weak)");
                root.Pick = $"{decide.Choice} (weak)";
                return "weak";
            }
            Tally(decide.Choice);
            root.Pick = decide.Choice;
            branches.FirstOrDefault(b => b.Option == decide.Choice)?.Act(response);
            return null;
        });
    }

    /// <summary>The root's branches as the brain view draws them, carry_on first.</summary>
    private static ThoughtBranch[] Considered(IEnumerable<Branch> considered, IReadOnlyDictionary<string, string> followUps) =>
        [
            new ThoughtBranch(CarryOn, null),
            .. considered.Select(b => new ThoughtBranch(b.Option, b.Gate)
            {
                FollowUp = followUps.GetValueOrDefault(b.Option), Only = b.Gate is null ? b.Only : null,
            }),
        ];

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
    /// The buttons a press could go to at <paramref name="now"/>: each one the
    /// HUD has shown come back, and each basic one never seen cast this game,
    /// which has no cooldown running that anything saw (the ultimate only from
    /// level six, when it can hold a point). A button seen cast whose
    /// countdown could not be read is not one of them: it was just thrown.
    /// </summary>
    private string[] ButtonsUp(double now, int? level) =>
        AbilityKits.Slots.Union(_casts.Keys)
            .Where(slot => _casts.TryGetValue(slot, out var cast)
                ? cast.Countdown is { } countdown && now >= cast.At + countdown
                : slot != "R" || level >= 6)
            .Order()
            .ToArray();

    /// <summary>
    /// Offered while the player is alive and placed, a button is up (the HUD
    /// has shown it come back, or it has never been seen cast), and something
    /// is there to throw it at: an enemy champion on the screen, or an enemy
    /// minion within the button's reach, since a good player farms with
    /// abilities too. A pick is one key press; with more than one button up,
    /// which is the follow-up's call.
    /// </summary>
    private Branch UseAbility(FrameEnvelope frame, ChampionRow self, Moment moment, double asked)
    {
        const string option = "use_ability";
        if (Unplaced(self) is { } unplaced)
            return Closed(option, unplaced);
        var slots = ButtonsUp(frame.VideoTime, self.Level);
        if (slots.Length == 0)
            return Closed(option, "no button known to be up");
        var minionsNear = NearMinions(frame, self).Length > 0;
        if (moment.VisibleEnemies.Count == 0
            && !moment.Abilities.Any(a => slots.Contains(a.Slot) && (a.EnemyMinionsInRange > 0 || a.Range is null && minionsNear)))
            return Closed(option, "no enemy on the screen and no enemy minion in reach");

        var criteria = new ChoiceCriteria();
        foreach (var slot in slots)
        {
            var facts = moment.Abilities.First(a => a.Slot == slot);
            var inside = moment.VisibleEnemies.Where(e => e.InRangeOf.Contains(slot)).Select(e => e.Champion).ToArray();
            criteria[slot] = $"{slot}: {facts.Kind ?? "what it is is not on file"}, "
                + (facts.Status == "up" ? "up" : "never seen cast this game, so up if it holds a point") + ", "
                + (facts.Range is { } range ? $"reaching {range:0} units" : "its range not on file")
                + (inside.Length > 0 ? $"; inside its range: {string.Join(", ", inside)}" : "; no visible enemy inside its range")
                + (facts.EnemyMinionsInRange is { } count
                    ? $"; enemy minions inside its range: {count}, the lowest at {facts.LowestEnemyMinionInRange:0%} health"
                    : "");
        }
        return new Branch(option, CoachQuestions.UseAbilityOption(slots),
            slots.Length > 1 ? q => q.Choice("ability", CoachQuestions.Ability, criteria) : null,
            response =>
            {
                if (Picked(response, "ability", slots) is not { } slot)
                    return;
                var facts = moment.Abilities.First(a => a.Slot == slot);
                var up = facts.Status == "up" ? $"{slot} up" : $"{slot} never seen on cooldown";
                var nearest = moment.VisibleEnemies.FirstOrDefault();
                var reason = nearest is not null && facts.Range is { } range && nearest.DistanceUnits <= range
                    ? $"{nearest.Champion} has been in {slot} range ({nearest.DistanceUnits:0} units)"
                      + (nearest.WithinReachForSeconds is { } held ? $" for {held:0.0}s" : "")
                      + $" with {up}"
                    : facts.EnemyMinionsInRange is { } count
                    ? $"{(count == 1 ? "an enemy minion" : $"{count} enemy minions")} in {slot} range, the lowest at "
                      + $"{facts.LowestEnemyMinionInRange:0%} health, with {up}"
                    : nearest is not null
                    ? $"{nearest.Champion} is {nearest.DistanceUnits:0} units away with {up}"
                    : $"enemy minions are in reach with {up}";
                _keys.Add(new KeyPress(asked, slot, 2, reason));
                Remember($"pressed {slot}", asked);
            }) { Only = slots.Length == 1 ? slots[0] : null };
    }

    /// <summary>
    /// How many enemy minions whose bars were read stand within
    /// <paramref name="range"/> of the player, and the lowest bar among them;
    /// null when none do.
    /// </summary>
    private static (int Count, double Lowest)? EnemyMinionsWithin(FrameEnvelope frame, ChampionRow self, double range)
    {
        if (Carried(frame, r => r.Minions) is not { } minions || self is not { WorldX: { } x, WorldY: { } y })
            return null;
        var inside = minions
            .Where(m => m.Team != MinionTeam.Blue && m.Health is not null
                && m is { WorldX: { } mx, WorldY: { } my } && double.Hypot(mx - x, my - y) <= range)
            .ToArray();
        return inside.Length == 0 ? null : (inside.Length, Math.Round(inside.Min(m => m.Health!.Value), 2));
    }

    // --- A skill point waiting: which ability it goes into ---

    /// <summary>
    /// Offered while the HUD shows a skill point waiting, alive or dead (a
    /// point goes in from the death screen too): at once for each point the
    /// coach has not pressed the chord for yet (a level gained while one
    /// waits stacks another under the same chevrons), and then again every
    /// <see cref="JevOptions.PointAgainEverySeconds"/> while the chevrons stay
    /// lit, since the coach's chord never reaches the game it watches and the
    /// point is still there to spend. A press shown again stands in for the
    /// last one, so the count stays one pick per point. Whether to spend it now, or hold it -- the first
    /// point of a game, against an invade -- is the root's call, from
    /// <see cref="Moment.SkillPoint"/>; which ability takes it is the
    /// follow-up's, a choice among exactly the slots the HUD lights, each
    /// said with what it is, its place in the champion's usual skill order,
    /// whether it has been seen cast, and how many points the coach itself
    /// has put in it since it began watching. A pick is the level-up chord,
    /// Ctrl and the slot. An answer that lands after the point was spent, or
    /// announced anew with another set, is about nothing and is dropped.
    /// </summary>
    private Branch LevelUp(ChampionRow self, Moment moment, double asked)
    {
        const string option = "level_up";
        if (_point is not { } point || moment.SkillPoint is not { } facts)
            return Closed(option, "no skill point waiting");
        if (_pressedFor.Count >= facts.Waiting && asked - _lastChordAt < _options.PointAgainEverySeconds)
            return Closed(option, $"pressed the chord {asked - _lastChordAt:0.0}s ago; again every {_options.PointAgainEverySeconds:0}s while lit");
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
        return new Branch(option, CoachQuestions.LevelUpOption,
            slots.Length > 1 ? q => q.Choice("slot", CoachQuestions.Slot, criteria) : null,
            response =>
            {
                if (_point?.Id != point.Id)
                    return;   // spent, or announced anew with another set, since it was asked
                if (Picked(response, "slot", slots) is not { } slot)
                    return;
                var note = AbilityKits.For(self.Champion, slot) is { } known ? $" ({known.Kind})" : "";
                var reason = facts.Waiting > 1
                    ? $"you have {facts.Waiting} points waiting at {since}{clock}; a good player would put one in {slot}{note} now"
                    : again
                    ? $"you have held the point from {since} for {facts.HeldForSeconds:0.0}s{clock}; a good player would put it in {slot}{note} by now"
                    : $"you reached {since}{clock}; a good player would put the point in {slot}{note}";
                _keys.Add(new KeyPress(asked, slot, 2, reason) { WithControl = true });
                if (_pressedFor.Count < facts.Waiting)
                    _pressedFor.Add(slot);
                else
                    _pressedFor[^1] = slot;   // shown again: the same point, still unspent
                _lastChordAt = asked;
                Remember($"put the point in {slot}", asked);
            }) { Only = slots.Length == 1 ? slots[0] : null };
    }

    /// <summary>The skill point the HUD shows waiting, as the state tells it; null when none is.</summary>
    private SkillPointFacts? SkillPointNow(ChampionRow? self, double now)
    {
        if (_point is not { } point)
            return null;
        var level = self?.Level;
        _firstLevelAsked ??= level;
        return new SkillPointFacts(level, point.Slots, point.Slots.Contains("R"), _firstLevelAsked,
            Math.Max(0, Math.Round(now - point.Since, 1)), PointsWaiting);
    }

    /// <summary>
    /// How many points wait under the lit chevrons: the one that lit them, and
    /// one more for every level the self row has read above the first it read
    /// while they stayed lit. The highest level counts, so a row that flaps a
    /// level down and back adds nothing.
    /// </summary>
    private int PointsWaiting =>
        _pointFirstLevel is { } first && _pointTopLevel is { } top ? 1 + Math.Max(0, top - first) : 1;

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

    /// <summary>A target a basic attack could be ordered at: who, where, and the sentence a pick of it says.</summary>
    private sealed record Strikeable(AttackTarget Target, double X, double Y, string Reason)
    {
        /// <summary>Where it was seen on the screen, in world-view pixels; null for a champion, placed only on the minimap.</summary>
        public (double X, double Y)? ViewPx { get; init; }
    }

    /// <summary>What keeps the player from any basic attack: being dead or unplaced, or the last attack still being carried out.</summary>
    private string? AttackGate(FrameEnvelope frame, ChampionRow self) =>
        Unplaced(self)
        ?? (frame.VideoTime - _lastAttackAt < _options.AttackEverySeconds
            ? $"attacked {frame.VideoTime - _lastAttackAt:0.0}s ago; still carrying it out"
            : null);

    /// <summary>
    /// Offered while the player is alive and placed with an enemy minion
    /// whose bar was read within (or a step or two beyond) their
    /// basic-attack range. Not offered for
    /// <see cref="JevOptions.AttackEverySeconds"/> after the coach's last
    /// attack, which the champion is still carrying out. Whether hitting a
    /// minion is worth it -- a last hit, setting one up, clearing the wave --
    /// is the root's call, and which minion the follow-up's; a pick is one
    /// right-click on it.
    /// </summary>
    private Branch AttackMinion(FrameEnvelope frame, ChampionRow self, double asked)
    {
        const string option = "attack_minion";
        if (AttackGate(frame, self) is { } gate)
            return Closed(option, gate);

        var targets = new Dictionary<string, Strikeable>();
        var criteria = new ChoiceCriteria();
        foreach (var (minion, mx, my, seen) in NearMinions(frame, self))
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
            targets[minion.Name] = new(new AttackTarget(name, minion.DistanceUnits), mx, my,
                $"it is {where} with {minion.Health:0%} of its bar left, {InReach(minion.InAttackRange)}; a good player would attack it now")
            {
                ViewPx = (seen.X, seen.Y),
            };
        }
        if (targets.Count == 0)
            return Closed(option, "no enemy minion in reach of a basic attack");
        return Strike(option, CoachQuestions.AttackMinionOption, "minion", CoachQuestions.WhichMinion, targets, criteria, self, asked);
    }

    /// <summary>
    /// Offered while the player is alive and placed with a visible enemy
    /// champion within (or a step or two beyond) their basic-attack range,
    /// and not for <see cref="JevOptions.AttackEverySeconds"/> after the
    /// coach's last attack. Whether the trade is the player's is the root's
    /// call, and which champion the follow-up's; a pick is one right-click
    /// on them.
    /// </summary>
    private Branch AttackChampion(FrameEnvelope frame, ChampionRow self, double asked)
    {
        const string option = "attack_champion";
        if (AttackGate(frame, self) is { } gate)
            return Closed(option, gate);
        var range = AbilityKits.AttackRange(self.Champion);
        var reach = (range ?? ReachWithoutARange) + ApproachUnits;

        var targets = new Dictionary<string, Strikeable>();
        var criteria = new ChoiceCriteria();
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
            targets[champion] = new(new AttackTarget(champion, distance), row.WorldX!.Value, row.WorldY!.Value,
                $"{champion} is {distance:0} units {Direction(self, row)} with {health}, {InReach(inRange)}; a good player would attack them now");
        }
        if (targets.Count == 0)
            return Closed(option, "no enemy champion in reach of a basic attack");
        return Strike(option, CoachQuestions.AttackChampionOption, "champion", CoachQuestions.WhichChampion, targets, criteria, self, asked);
    }

    /// <summary>
    /// An attack branch over <paramref name="targets"/>: the follow-up
    /// <paramref name="question"/> when there is more than one, and a pick is
    /// one right-click on the chosen target, the order that has the champion
    /// attack it.
    /// </summary>
    private Branch Strike(
        string option, string criterion, string question, string rubric,
        Dictionary<string, Strikeable> targets, ChoiceCriteria criteria, ChampionRow self, double asked)
    {
        var (x, y) = (self.WorldX!.Value, self.WorldY!.Value);
        return new Branch(option, criterion,
            targets.Count > 1 ? q => q.Choice(question, rubric, criteria) : null,
            response =>
            {
                if (Picked(response, question, targets.Keys) is not { } chosen)
                    return;
                var target = targets[chosen];
                // World y grows north, screen y grows down: flip for the click.
                var (dx, dy) = (target.X - x, -(target.Y - y));
                var length = double.Hypot(dx, dy);
                var (ux, uy) = length < 1 ? (0.0, 0.0) : (dx / length, dy / length);
                var direction = length < 1 ? "where you stand" : ScreenDirections.Name(dx, dy);
                _moves.Add(new MoveStep(asked, direction, ux, uy, 2, target.Reason) { Target = target.Target, ViewPx = target.ViewPx });
                _lastAttackAt = asked;
                Remember($"attacked {target.Target.Name}", asked);
            }) { Only = targets.Count == 1 ? targets.Keys.First() : null };
    }

    private static string InReach(bool? inRange) => inRange switch
    {
        true => "inside your attack range",
        false => "a step outside your attack range",
        null => "your attack range is not on file",
    };

    /// <summary>
    /// The enemy minions on the player's screen near enough to attack, or a
    /// step or two beyond, whose bars could be read, lowest bar first, with
    /// where each stands in world units. Named "minion 1" and on in that
    /// order: the names the attack follow-up's options and the state share.
    /// </summary>
    private (MinionTarget Fact, double X, double Y, Minion Seen)[] NearMinions(FrameEnvelope frame, ChampionRow self)
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
                    p.Minion.WorldX!.Value, p.Minion.WorldY!.Value, p.Minion);
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
    private Branch StepBack(FrameEnvelope frame, ChampionRow self, Moment moment, double asked)
    {
        const string option = "step_back";
        if (Unplaced(self) is { } unplaced)
            return Closed(option, unplaced);
        var (x, y) = (self.WorldX!.Value, self.WorldY!.Value);
        if (RiftMap.LaneOf(x, y) is not { } lane)
            return Closed(option, "not in a lane");
        if (Stepping(frame.VideoTime))
            return Closed(option, SteppedAgo(frame.VideoTime));
        if (moment.Minions is not { NearestTheirsUnits: { } nearest } minions)
            return Closed(option, "no enemy minion near");

        return new Branch(option, CoachQuestions.StepBackOption, null, _ =>
        {
            var (_, progress) = Map.Along(lane, x, y);
            var behind = Map.At(lane, progress - BackStepUnits / RiftMap.Length(lane));
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

    // --- The default: on the way to a lane's wave ---

    /// <summary>
    /// How near an enemy wave the player already is when they are at it: about
    /// a screen's width, inside which the wave is already in view.
    /// </summary>
    private const double AtTheWaveUnits = 1500;

    /// <summary>
    /// The player's default: offered while they are alive and placed on the
    /// map, the game clock is running (before it the player cannot move), no
    /// movement click came in the last <see cref="JevOptions.MoveEverySeconds"/>,
    /// and they are not already at or past a lane's safe spot -- unless an
    /// enemy wave the minimap shows at one of their turrets is more than
    /// <see cref="AtTheWaveUnits"/> away, a wave left to crash with nobody
    /// catching it. Whether they stand or walk, and whatever the coach's last
    /// step was. Whether something else comes first is the root's call and
    /// which lane the follow-up's; a pick is one step on the ground toward
    /// the farthest spot up that lane the player can walk to safely
    /// (<see cref="RiftMap.WalkTo"/>), which stops short of an enemy wave at
    /// their turret. The trip is a string of such steps, one a second, each
    /// decided afresh.
    /// </summary>
    private Branch WalkToLane(FrameEnvelope frame, ChampionRow self, Moment moment, double asked)
    {
        const string option = "walk_to_lane";
        if (Unplaced(self) is { } unplaced)
            return Closed(option, unplaced);
        var (x, y) = (self.WorldX!.Value, self.WorldY!.Value);
        if (frame.GameTime is null)
            return Closed(option, "the game clock is not running");
        if (Stepping(frame.VideoTime))
            return Closed(option, SteppedAgo(frame.VideoTime));
        if (moment.Whereabouts is not { } whereabouts)
            return Closed(option, "whereabouts not known");
        // At or past a lane's safe spot is laning, not idling: a pick would
        // be no step at all, asked again the next moment -- unless a wave is
        // crashing into a turret of theirs somewhere else.
        var crashing = whereabouts.Lanes.Where(l => Crashing(l)).Select(l => l.Lane).ToHashSet();
        if (crashing.Count == 0 && whereabouts.Lanes.FirstOrDefault(l => l.YouAre is "at it" or "past it") is { } there)
            return Closed(option, $"already {(there.YouAre == "at it" ? "at" : "past")} {there.WalkTo} in {there.Lane} lane");

        var criteria = new ChoiceCriteria();
        foreach (var lane in whereabouts.Lanes)
        {
            var allies = lane.AlliesThere.Count == 0 ? "none" : string.Join(", ", lane.AlliesThere);
            criteria[lane.Lane] = (lane.ScreenDirection is { } direction
                ? $"{lane.Lane} lane: {lane.DistanceUnits:0} units away, {direction} on the screen; allies there: {allies}"
                : $"{lane.Lane} lane: the player is standing in it; allies there: {allies}")
                + DescribeWave(lane.Wave) + DescribeWalkTo(lane)
                + (crashing.Contains(lane.Lane)
                    ? $"; its enemy wave is at a turret of the player's, {lane.Wave!.TheirFrontUnitsAway:0} units from them, with nobody there to catch it but whoever walks there"
                    : "");
        }
        return new Branch(option, CoachQuestions.WalkToLaneOption,
            q => q.Choice("lane", CoachQuestions.Lane, criteria),
            response =>
            {
                if (!response.TryGet<ChoiceAnswer>("lane", out var lane) || !RiftMap.Lanes.Contains(lane!.Choice))
                    return;
                // A step toward the farthest spot up the lane that is safe: the
                // farthest turret standing, or behind the minions pushed beyond it --
                // never the lane's nearest point, which from base is only its mouth.
                var facts = whereabouts.Lanes.First(l => l.Lane == lane.Choice);
                var (progress, name) = WalkTo(lane.Choice, facts.Wave);
                var spot = Map.At(lane.Choice, progress);
                // World y grows north, screen y grows down: flip for the step.
                var (dx, dy) = (spot.X - x, -(spot.Y - y));
                var length = double.Hypot(dx, dy);
                if (length <= RiftMap.LaneHalfWidth)
                    return;
                var direction = ScreenDirections.Name(dx, dy);
                var clock = moment.GameClock is { } time ? $" at {time}" : "";
                var where = whereabouts.StoodStillForSeconds >= 1
                    ? $"you have stood still for {whereabouts.StoodStillForSeconds:0.0}s in {whereabouts.Place}{clock}"
                    : $"you are in {whereabouts.Place}{clock}";
                var place = Map.Place(spot.X, spot.Y);
                var catching = crashing.Contains(lane.Choice);
                var reason = catching
                    ? $"the enemy wave is {facts.Wave!.TheirFrontPlace} in {lane.Choice} lane"
                      + (facts.AlliesThere.Count == 0 ? " with none of your team there" : "")
                      + $"; a good player would be on the way to catch it ({AtPlace(name, place)}, {length:0} units {direction})"
                    : $"{where}; a good player would be on the way up {lane.Choice} lane ({AtPlace(name, place)}, {length:0} units {direction})";
                _moves.Add(new MoveStep(asked, direction, dx / length, dy / length, 2, reason)
                {
                    Destination = new Destination(place, spot.X, spot.Y),
                    DistanceUnits = length,
                    From = (x, y),
                    AttackMove = true,
                });
                _lastMoveAt = asked;
                Remember(catching ? $"stepped toward {lane.Choice} lane's wave at your turret" : $"stepped toward {lane.Choice} lane", asked);
            });
    }

    /// <summary>
    /// Whether a lane's enemy wave is one left to crash: the minimap shows its
    /// front at one of the player's turrets, farther than
    /// <see cref="AtTheWaveUnits"/> from them.
    /// </summary>
    private bool Crashing(LaneFacts lane) =>
        lane.Wave is { TheirFront: { } front, TheirFrontUnitsAway: > AtTheWaveUnits } && Map.AtOurTurret(lane.Lane, front);

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
            return said + $", meeting in {wave.MeetPlace} ({meet:0.00} of the way to the enemy nexus), "
                + (wave.MeetScreenDirection is { } way ? $"{wave.MeetUnitsAway:0} units away, {way} on the screen" : "where the player stands");
        return said + (wave.OurFront is { } ours ? $", ours pushed {ours:0.00} of the way" : $", theirs pushed to {wave.TheirFront:0.00} of the way");
    }

    /// <summary>
    /// A spot named with where on the map it is: "behind your minions in bot
    /// lane, at your outer turret", or the place alone when it already says
    /// the name ("bot lane, at your outer turret" for "your outer turret").
    /// </summary>
    private static string AtPlace(string name, string? place) =>
        place is null ? name : place.EndsWith(name) ? place : $"{name} in {place}";

    /// <summary>A lane's safe spot to walk to, as an option of the lane follow-up says it.</summary>
    private static string DescribeWalkTo(LaneFacts lane)
    {
        if (lane.WalkTo is not { } walkTo)
            return "";
        var spot = AtPlace(walkTo, lane.WalkToPlace);
        var said = lane.WalkToScreenDirection is { } way
            ? $"; the farthest it is safe to walk: {spot}, {lane.WalkToUnitsAway:0} units away, {way} on the screen"
            : $"; the farthest it is safe to walk: {spot}, where the player stands";
        return lane.YouAre is { } you ? $"{said}; the player is {you}" : said;
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
    private Branch HideInBrush(FrameEnvelope frame, ChampionRow self, Moment moment, double asked)
    {
        const string option = "hide_in_brush";
        if (Unplaced(self) is { } unplaced)
            return Closed(option, unplaced);
        var (x, y) = (self.WorldX!.Value, self.WorldY!.Value);
        if (frame.GameTime is null)
            return Closed(option, "the game clock is not running");
        if (Stepping(frame.VideoTime))
            return Closed(option, SteppedAgo(frame.VideoTime));
        if (RiftBrush.At(x, y) is not null)
            return Closed(option, "already in the brush");
        var near = NearBrushes(frame, self);
        if (near.Length == 0)
            return Closed(option, "no brush near");

        var criteria = new ChoiceCriteria();
        foreach (var (fact, _) in near)
            criteria[fact.Name] = DescribeBrush(fact);
        return new Branch(option, CoachQuestions.HideInBrushOption,
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
                var reason = $"{patch.NameFrom(_side)} is {brush.Fact.DistanceUnits:0} units {direction}{seen}; "
                    + "a good player would stand in the brush, where no enemy outside it can see them";
                _moves.Add(new MoveStep(asked, direction, dx / length, dy / length, 2, reason)
                {
                    Destination = new Destination(patch.NameFrom(_side), spot.X, spot.Y),
                    DistanceUnits = length,
                    From = (x, y),
                });
                _lastMoveAt = asked;
                Remember($"stepped toward {patch.NameFrom(_side)}", asked);
            }) { Only = near.Length == 1 ? near[0].Fact.Name : null };
    }

    // --- Out ahead with enemies near: back to cover ---

    /// <summary>
    /// How near a refuge the player already is when they are at it: inside
    /// this, there is nothing to run back to.
    /// </summary>
    private const double AtARefugeUnits = 400;

    /// <summary>
    /// How far short of one of their own turrets, toward their base, a run
    /// back to it aims: under the turret, not a click on the structure.
    /// </summary>
    private const double BehindATurretUnits = 250;

    /// <summary>
    /// Offered while the player is alive and placed with an enemy champion on
    /// the screen, a refuge to run back to (<see cref="Cover"/>), no
    /// movement click in the last <see cref="JevOptions.MoveEverySeconds"/>,
    /// and the player not already under one of their own turrets.
    /// Whether they stand out ahead of their minions and their team is the
    /// root's call, from <see cref="Moment.Cover"/>, and which refuge the
    /// follow-up's; a pick is one step on the ground toward it, and the next
    /// is decided afresh.
    /// </summary>
    private Branch RunAway(FrameEnvelope frame, ChampionRow self, Moment moment, double asked)
    {
        const string option = "run_away";
        if (Unplaced(self) is { } unplaced)
            return Closed(option, unplaced);
        var (x, y) = (self.WorldX!.Value, self.WorldY!.Value);
        if (moment.VisibleEnemies.Count == 0)
            return Closed(option, "no enemy on the screen");
        if (Stepping(frame.VideoTime))
            return Closed(option, SteppedAgo(frame.VideoTime));
        var (by, refuges) = Cover(frame, self);
        // Under their own turret is the cover a retreat ends at: from there,
        // any step "back" is to a worse refuge, or no step at all.
        if (RiftMap.Turrets.FirstOrDefault(t => t.Owner == _side && by.Contains(t.NameFrom(_side))) is { } under)
            return Closed(option, $"already under {under.NameFrom(_side)}");
        if (refuges.Length == 0)
            return Closed(option, "no refuge to run back to");

        var criteria = new ChoiceCriteria();
        foreach (var (fact, _, _) in refuges)
            criteria[fact.Name] = DescribeRefuge(fact);
        return new Branch(option, CoachQuestions.RunAwayOption,
            refuges.Length > 1 ? q => q.Choice("refuge", CoachQuestions.WhichRefuge, criteria) : null,
            response =>
            {
                var chosen = Picked(response, "refuge", refuges.Select(r => r.Fact.Name).ToArray());
                if (refuges.FirstOrDefault(r => r.Fact.Name == chosen) is not { Fact: { } refuge } pick)
                    return;
                // World y grows north, screen y grows down: flip for the step.
                var (dx, dy) = (pick.X - x, -(pick.Y - y));
                var length = double.Hypot(dx, dy);
                if (length < 1)
                    return;
                var direction = ScreenDirections.Name(dx, dy);
                var nearest = moment.VisibleEnemies[0];
                var ahead = moment.Minions?.AheadOfOurFrontUnits is > 0 and var units
                    ? $" and you stand {units:0} units in front of your minions"
                    : "";
                var reason = $"{nearest.Champion} is {nearest.DistanceUnits:0} units {nearest.ScreenDirection}{ahead}; "
                    + $"a good player would run back to {AtPlace(refuge.Name, refuge.Place)} ({length:0} units {direction})";
                _moves.Add(new MoveStep(asked, direction, dx / length, dy / length, 2, reason)
                {
                    Destination = new Destination(AtPlace(refuge.Name, refuge.Place), pick.X, pick.Y),
                    DistanceUnits = length,
                    From = (x, y),
                });
                _lastMoveAt = asked;
                Remember($"ran back toward {refuge.Name}", asked);
            }) { Only = refuges.Length == 1 ? refuges[0].Fact.Name : null };
    }

    /// <summary>A refuge, as an option of the refuge follow-up says it.</summary>
    private static string DescribeRefuge(Refuge refuge)
    {
        var said = $"{refuge.Name}: in {refuge.Place}, {refuge.DistanceUnits:0} units {refuge.ScreenDirection ?? "away"}"
            + (refuge.TowardYourBase ? ", toward your base" : ", away from your base");
        if (refuge.NearestEnemyUnits is { } enemy)
            said += $", the nearest enemy champion {enemy:0} units from it";
        return said;
    }

    /// <summary>
    /// The cover around the player: the nearest of their own turrets still
    /// standing, the nearest ally the minimap places, and their own minions in
    /// the lane they stand in. Those the player is already by (in the
    /// turret's range, <see cref="AtARefugeUnits"/> of an ally, or behind
    /// their own foremost minion) are
    /// named in <c>By</c>; the rest are refuges to run back to, nearest first,
    /// each with the spot a step aims at: under the turret, the ally, or just
    /// behind the foremost minion when the player stands in front of it.
    /// </summary>
    private (IReadOnlyList<string> By, (Refuge Fact, double X, double Y)[] Away) Cover(FrameEnvelope frame, ChampionRow self)
    {
        if (self is not { WorldX: { } x, WorldY: { } y })
            return ([], []);
        var enemies = Visible(frame, self).Select(e => (X: e.WorldX!.Value, Y: e.WorldY!.Value)).ToArray();
        List<(string Name, string Kind, double X, double Y)> spots = [];
        List<string> by = [];

        var home = Map.Fountain;
        if (RiftMap.Turrets
                .Where(t => t.Owner == _side && OurTurretStanding(t) != false)
                .MinBy(t => double.Hypot(t.X - x, t.Y - y)) is { } turret)
        {
            // Anywhere in its range is under it: the turret shoots whoever
            // chases them there, so a step to the spot behind it is no retreat.
            if (double.Hypot(turret.X - x, turret.Y - y) <= RiftMap.TurretRange)
                by.Add(turret.NameFrom(_side));
            else
            {
                var toHome = double.Hypot(home.X - turret.X, home.Y - turret.Y);
                var (tx, ty) = toHome < 1 ? (turret.X, turret.Y)
                    : (turret.X + (home.X - turret.X) / toHome * BehindATurretUnits,
                       turret.Y + (home.Y - turret.Y) / toHome * BehindATurretUnits);
                spots.Add((turret.NameFrom(_side), "turret", tx, ty));
            }
        }
        if (frame.Champions
                .Where(c => c.Team == self.Team && c.TrackId != self.TrackId && c.Alive != false
                    && c is { WorldX: not null, WorldY: not null })
                .MinBy(c => double.Hypot(c.WorldX!.Value - x, c.WorldY!.Value - y)) is { } ally)
            spots.Add((ally.Champion ?? $"track {ally.TrackId}", "ally", ally.WorldX!.Value, ally.WorldY!.Value));
        if (RiftMap.LaneOf(x, y) is { } lane && OurFront(frame, lane) is { } front)
        {
            if (Map.Along(lane, x, y).Progress > front)
            {
                var (mx, my) = Map.At(lane, Math.Max(0, front - RiftMap.WalkBehindUnits / RiftMap.Length(lane)));
                spots.Add(("behind your minions", "minions", mx, my));
            }
            else
                by.Add("your minions");
        }

        var fromHome = double.Hypot(x - home.X, y - home.Y);
        var measured = spots.Select(s => (s, Distance: double.Hypot(s.X - x, s.Y - y))).ToArray();
        by.InsertRange(by.IndexOf("your minions") is >= 0 and var mine ? mine : by.Count,
            measured.Where(p => p.Distance <= AtARefugeUnits).Select(p => p.s.Name));
        var away = measured
            .Where(p => p.Distance > AtARefugeUnits)
            .OrderBy(p => p.Distance)
            .Select(p => (new Refuge(p.s.Name, p.s.Kind, Math.Round(p.Distance),
                    ScreenDirections.NameOfWorldOffset(p.s.X - x, p.s.Y - y),
                    double.Hypot(p.s.X - home.X, p.s.Y - home.Y) < fromHome)
                {
                    Place = Map.Place(p.s.X, p.s.Y),
                    NearestEnemyUnits = enemies.Length == 0 ? null
                        : Math.Round(enemies.Min(e => double.Hypot(e.X - p.s.X, e.Y - p.s.Y))),
                }, p.s.X, p.s.Y))
            .ToArray();
        return (by, away);
    }

    /// <summary>
    /// Whether one of the player's own turrets stands, off the minimap: null
    /// when it has not been called, or the minimap has not been read for
    /// turrets at all.
    /// </summary>
    private bool? OurTurretStanding(RiftMap.TurretSpot spot) =>
        _turrets?.FirstOrDefault(t => t.Team == MinionTeam.Blue && t.Lane == spot.Lane && t.Tier == spot.Tier
            && (spot.NexusSide is null || t.Side == spot.NexusSide))?.Standing;

    /// <summary>
    /// The cover around the player as the state tells it (<see cref="Cover"/>),
    /// with how many allies the minimap places nearer the nearest visible
    /// enemy champion than the player is: 0 is the player out in front of
    /// their team, null with no enemy champion on the screen.
    /// </summary>
    private CoverFacts? CoverNow(FrameEnvelope frame, ChampionRow self)
    {
        if (self is not { WorldX: { } x, WorldY: { } y })
            return null;
        int? nearer = null;
        if (Visible(frame, self).MinBy(e => double.Hypot(e.WorldX!.Value - x, e.WorldY!.Value - y)) is { } enemy)
        {
            var (ex, ey) = (enemy.WorldX!.Value, enemy.WorldY!.Value);
            var yours = double.Hypot(ex - x, ey - y);
            nearer = frame.Champions.Count(c => c.Team == self.Team && c.TrackId != self.TrackId && c.Alive != false
                && c is { WorldX: { } ax, WorldY: { } ay } && double.Hypot(ex - ax, ey - ay) < yours);
        }
        var (by, away) = Cover(frame, self);
        return new CoverFacts(nearer, by, away.Select(r => r.Fact).ToArray());
    }

    // --- Home: the recall ---

    /// <summary>
    /// Offered while the player is alive and placed out of their base, with
    /// the game clock running, and not already channelling one the coach
    /// pressed. Whether it is time to go home -- low health, no mana, nothing
    /// to be safe by -- is the root's call, from <see cref="Moment.Player"/>
    /// and <see cref="Moment.Cover"/>; a pick is the recall key, and for the
    /// channel's length the root offers nothing that would move the player.
    /// </summary>
    private Branch Recall(FrameEnvelope frame, ChampionRow self, Moment moment, double asked)
    {
        const string option = "recall";
        if (self.Alive == false)
            return Closed(option, "dead");
        if (frame.GameTime is null)
            return Closed(option, "the game clock is not running");
        if (moment.Whereabouts is not { } where)
            return Closed(option, "whereabouts not known");
        if (AtHome(where.Place))
            return Closed(option, $"already in {where.Place}");
        if (frame.VideoTime < _recallUntil)
            return Closed(option, "already recalling");
        return new Branch(option, CoachQuestions.RecallOption, null, _ =>
        {
            List<string> why = [];
            if (moment.Player?.Health is { } health)
                why.Add($"{health:0%} health");
            if (moment.Player?.Mana is { } mana)
                why.Add($"{mana:0%} mana");
            if (moment.Cover is { YouAreBy.Count: 0 })
                why.Add("no turret or minions of yours by you");
            var clock = moment.GameClock is { } time ? $" at {time}" : "";
            var with = why.Count > 0 ? $" with {string.Join(", ", why)}" : "";
            var reason = $"you are in {where.Place}{clock}{with}; a good player would recall";
            _keys.Add(new KeyPress(asked, "B", 2, reason));
            _recallUntil = asked + _options.RecallChannelSeconds;
            Remember("pressed B to recall", asked);
        });
    }

    /// <summary>
    /// Whether the coach's recall is still channelling: pressed less than
    /// <see cref="JevOptions.RecallChannelSeconds"/> ago, with no enemy
    /// champion on the screen and the player not yet home.
    /// </summary>
    private bool Recalling(FrameEnvelope frame, Moment moment) =>
        frame.VideoTime < _recallUntil && moment.VisibleEnemies.Count == 0
        && moment.Whereabouts is { } where && !AtHome(where.Place);

    // --- Said, not yet done: buying ---

    /// <summary>
    /// Offered while the player is alive in the fountain, where the shop is.
    /// With gold in the feed, for as long as it covers
    /// <see cref="JevOptions.BuyFloorGold"/>, paced by <see cref="Said"/>: a
    /// purchase shows as a drop on its frame, so the offer stops once the
    /// rest buys nothing and comes back if the gold climbs over again. A frame
    /// without a reading holds the last decision, and before the first one
    /// the buy is not offered. Without gold, at most once a visit: after one
    /// shopping trip there is no telling whether the rest buys anything, and
    /// asking again only says "buy" to a player who cannot afford it; leaving
    /// the fountain, or dying, opens it for the next visit. The ghost's hands
    /// have no move for it yet, so a pick is a cue naming it.
    /// </summary>
    private Branch Buy(FrameEnvelope frame, ChampionRow self, Moment moment, double asked)
    {
        const string option = "buy";
        if (self.Alive == false)
        {
            _boughtThisVisit = false;
            return Closed(option, "dead");
        }
        if (moment.Whereabouts is not { Place: RiftMap.FountainPlace })
        {
            _boughtThisVisit = false;
            return Closed(option, "not in the fountain");
        }
        if (_hasGold)
        {
            return _goldCovers switch
            {
                null => Closed(option, "gold not read yet"),
                false => Closed(option, $"{_gold} gold buys nothing; the floor is {_options.BuyFloorGold}"),
                true => Said(frame, option, CoachQuestions.BuyOption, "bought", moment, asked),
            };
        }
        if (_boughtThisVisit)
            return Closed(option, "already said this visit; no gold to tell what else is affordable");
        var branch = Said(frame, option, CoachQuestions.BuyOption, "bought", moment, asked);
        return branch.Gate is not null ? branch : branch with
        {
            Act = answer =>
            {
                branch.Act(answer);
                _boughtThisVisit = true;
            },
        };
    }

    /// <summary>
    /// Takes the frame's gold reading, when there is one, for the buy's gate.
    /// No reading changes nothing: absence is not zero.
    /// </summary>
    private void TrackGold(ChampionRow self)
    {
        if (self.Gold is not { } gold)
            return;
        _gold = gold;
        _goldCovers = gold >= _options.BuyFloorGold;
    }

    private static bool AtHome(string place) => place is RiftMap.FountainPlace or RiftMap.BasePlace;

    /// <summary>
    /// A branch the ghost's hands have no move for yet: a pick is a cue
    /// naming what a good player would have done, with where the player is
    /// and what they face, and the coach remembers saying it. Not offered for
    /// <see cref="JevOptions.SayEverySeconds"/> after it was last said, so a
    /// moment that goes on being right for it is not said four times a second.
    /// </summary>
    private Branch Said(FrameEnvelope frame, string option, string criterion, string done, Moment moment, double asked)
    {
        if (_lastSaid.TryGetValue(option, out var at) && frame.VideoTime - at < _options.SayEverySeconds)
            return Closed(option, $"said {frame.VideoTime - at:0.0}s ago; once every {_options.SayEverySeconds:0}s");
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
        var home = double.Hypot(x - Map.Fountain.X, y - Map.Fountain.Y);
        return RiftBrush.Near(x, y, BrushNearUnits)
            .Where(n => n.Patch != standingIn)
            .Take(BrushOptions)
            .Select((n, i) =>
            {
                var patch = n.Patch;
                var (_, cx, cy) = patch.Nearest(x, y);
                double? ahead = patch.Lane is { } lane && OurFront(frame, lane) is { } front
                    ? Math.Round((Map.Along(lane, patch.X, patch.Y).Progress - front) * RiftMap.Length(lane))
                    : null;
                var inIt = allies
                    .Where(a => patch.Nearest(a.WorldX!.Value, a.WorldY!.Value).Distance <= RiftBrush.InBrushUnits)
                    .Select(a => a.Champion ?? $"track {a.TrackId}")
                    .ToArray();
                var cover = TurretCover(patch.X, patch.Y)?.Said;
                var faceCheck = ahead > 0 ? $"{ahead:0} units in front of your minions"
                    : cover is not null ? $"under {cover}"
                    : patch.InEnemyJungle(_side) ? "in the enemy's jungle"
                    : null;
                var fact = new BrushNear(
                    $"brush {i + 1}", patch.NameFrom(_side), patch.PlaceFrom(_side), Math.Round(n.Distance),
                    n.Distance < 1 ? null : ScreenDirections.NameOfWorldOffset(cx - x, cy - y),
                    double.Hypot(patch.X - Map.Fountain.X, patch.Y - Map.Fountain.Y) < home)
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
                && (spot.NexusSide is null || t.Side == spot.NexusSide))?.Standing;
        return Map.TheirTurretCovering(x, y, standing) is { } cover
            ? ($"{cover.Turret.NameFrom(_side)}, {cover.Distance:0} units from it", cover.Turret.X, cover.Turret.Y)
            : null;
    }

    /// <summary>
    /// The player's own cast, named to a button off the HUD: the one fact that
    /// tells us a slot exists and when it comes back. A cast whose countdown
    /// could not be read leaves the slot unknown until the next one that can,
    /// but still seen cast: a button just thrown is not one never thrown.
    /// </summary>
    private void OnAbility(GameEvent evt)
    {
        if (evt.Slot is not { } slot || evt.At is not { } at)
            return;
        _casts[slot] = (at, evt.Countdown);
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
        Ask("bolt", moment, questions, evt.VideoTime, OccasionRequest, released: null,
            new Thought { YesAt = _options.YesAt }, answered: response =>
        {
            if (response.TryGet<NoulAnswer>("remark", out var remark) && remark!.IsYes(_options.YesAt))
            {
                _cues.Add(new CoachCue(evt.VideoTime, 3, sentence));
                Remember("remarked on a bolt", evt.VideoTime);
            }
            if (sides is null
                || !response.TryGet<NoulAnswer>("step", out var step) || !step!.IsYes(_options.YesAt)
                || !response.TryGet<ChoiceAnswer>("side", out var side) || !sides.TryGetValue(side!.Choice, out var way))
                return null;
            _moves.Add(new MoveStep(stamp, side.Choice, way.Dx, way.Dy, 3, sentence));
            _lastMoveAt = Math.Max(_lastMoveAt, stamp);
            Remember($"stepped {side.Choice}", stamp);
            return null;
        });
    }

    /// <summary>
    /// A side of the bolt's line, described by what the code can measure
    /// about it: toward home or not, and toward or away from the nearest
    /// enemy on the screen. Which of the two to take is the model's call.
    /// </summary>
    private string DescribeSide((double Dx, double Dy) step, ChampionRow? self, IReadOnlyList<EnemyFacts> enemies)
    {
        var home = Map.TowardBase(step.Dx, step.Dy) switch
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
            evt.VideoTime, OccasionRequest, released: null, new Thought { YesAt = _options.YesAt }, answered: response =>
            {
                if (!response.TryGet<NoulAnswer>("remark", out var remark) || !remark!.IsYes(_options.YesAt))
                    return null;
                _cues.Add(new CoachCue(evt.VideoTime, 2, sentence));
                Remember("remarked on aim", evt.VideoTime);
                return null;
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
    /// announces it. A new set is a new point. Lit chevrons with no point
    /// known are one the feed will not announce (the baseline after a gap, or
    /// an announcement missed), taken up from the row. While a point waits,
    /// the levels the row reads count the points stacked under it.
    /// </summary>
    private void TrackPoint(FrameEnvelope frame, ChampionRow self)
    {
        var lit = LitSlots(self.Learnable);
        if (_point is not { } point)
        {
            if (lit.Length == 0)
                return;
            _point = (frame.VideoTime, lit, ++_pointId);
            _pointAsked = false;
        }
        else if (lit.Length > 0 && !lit.SequenceEqual(point.Slots))
            _point = (point.Since, lit, ++_pointId);
        if (self.Level is { } level)
        {
            _pointFirstLevel ??= level;
            _pointTopLevel = Math.Max(_pointTopLevel ?? level, level);
        }
    }

    /// <summary>No point waiting: the chevrons cleared, or a gap may be a new game.</summary>
    private void ForgetPoint()
    {
        _point = null;
        _pointFirstLevel = _pointTopLevel = null;
        _pressedFor.Clear();
        _lastChordAt = double.NegativeInfinity;
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
                $"{(_pressedFor.Count > 1 ? "the points went" : "the point went")} in{held}; the coach's Ctrl+{string.Join(", Ctrl+", _pressedFor)} counted as its placement"));
        }
        else
            _cues.Add(new CoachCue(evt.VideoTime, 1, $"the player put the point in themselves{held}; it is in nobody's count"));
        ForgetPoint();
    }

    // --- Turrets: state for the facts, the events only logged ---

    /// <summary>
    /// Which side the player plays from, off any of their team standing alive
    /// in a fountain: nobody stands in the enemy's and lives. Said in the log
    /// the first time it is seen and whenever it changes, since everything
    /// the coach says about the map hangs on it.
    /// </summary>
    private void TrackSide(FrameEnvelope frame)
    {
        var seen = frame.Champions
            .Where(c => c.Team == MinionTeam.Blue && c.Alive != false)
            .Select(c => c is { WorldX: { } x, WorldY: { } y } ? RiftMap.FountainOf(x, y) : null)
            .FirstOrDefault(side => side is not null);
        if (seen is not { } side || (_sideSeen && side == _side))
            return;
        _side = side;
        _sideSeen = true;
        _cues.Add(new CoachCue(frame.VideoTime, 1, $"you play from the {side.ToString().ToLowerInvariant()} side"));
    }

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

    // --- The farm: last hits taken and missed ---

    /// <summary>How far back, in video seconds, the roots are kept: past a last hit's window and the event's lag behind it.</summary>
    private const double RootsKeptSeconds = 5;

    /// <summary>
    /// How long before a minion's bar was last seen the roots count toward
    /// what its last hit is put down to: the wind-up and a step or two.
    /// </summary>
    private const double LastHitWindowSeconds = 1.5;

    /// <summary>How far back, in video seconds, the last hits are counted for the state.</summary>
    private const double FarmWindowSeconds = 60;

    /// <summary>
    /// An enemy minion died on the player's screen low enough to be a last
    /// hit, and their score rose for it or did not. Counted for the state and
    /// the log, and put down to what the coach did in the moments before its
    /// bar was last seen (<see cref="LastHitWindowSeconds"/>), for the audit.
    /// The event comes about a second and a half after the death, and the
    /// coach's orders never reach the game it watches, so this measures the
    /// player; what it is put down to measures the coach.
    /// </summary>
    private void OnFarmed(GameEvent evt)
    {
        var at = evt.At ?? evt.VideoTime;
        var taken = evt.Kind == EventKind.LastHit;
        _farm.Add((at, taken));
        var why = PutDownTo(at);
        if (taken)
        {
            _takenSeen++;
            _takenTotal++;
        }
        else
        {
            _missedSeen++;
            _missedTotal++;
            _missedWhy[why] = _missedWhy.GetValueOrDefault(why) + 1;
        }
        Farmed?.Invoke(new FarmOutcome(evt.VideoTime, at, evt.Kind, evt.Health, why));
    }

    /// <summary>
    /// What the coach did about a last hit whose bar was last seen at
    /// <paramref name="at"/>: ordered an attack; offered one that lost to
    /// another pick (the most common); or offered none, and the gate that
    /// most often closed it, its numbers left out so like gates count
    /// together. "not asked" when no root came in the window.
    /// </summary>
    private string PutDownTo(double at)
    {
        var window = _roots.Where(r => r.At >= at - LastHitWindowSeconds && r.At <= at).ToArray();
        if (window.Length == 0)
            return "not asked";
        if (window.Any(r => r.Pick == "attack_minion"))
            return "coach attacked";
        var offered = window.Where(r => r.AttackGate is null).ToArray();
        if (offered.Length > 0)
            return $"attack offered, picked {MostCommon(offered.Select(r => r.Pick ?? "no answer"))}";
        return $"attack closed: {MostCommon(window.Select(r => Regex.Replace(r.AttackGate!, @"\d+(\.\d+)?", "#")))}";

        static string MostCommon(IEnumerable<string> said) =>
            said.GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
    }

    /// <summary>
    /// The farm as the state tells it: the HUD's score, a rate once the clock
    /// is past 1:30, and the last minute's last hits by whether they were
    /// taken. Null when the score has not been read and nothing has died.
    /// </summary>
    private FarmingFacts? FarmingNow(int? gameTime, double now)
    {
        _farm.RemoveAll(f => now - f.At > FarmWindowSeconds);
        if (_cs is null && _farm.Count == 0)
            return null;
        var lastMiss = _farm.Where(f => !f.Taken).Select(f => (double?)f.At).LastOrDefault();
        return new FarmingFacts(
            _cs,
            _cs is { } cs && gameTime is { } seconds && seconds >= 90 ? Math.Round(cs * 60.0 / seconds, 1) : null,
            _farm.Count(f => f.Taken),
            _farm.Count(f => !f.Taken),
            lastMiss is { } miss ? Math.Round(Math.Max(0, now - miss), 1) : null);
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
    /// <remarks>
    /// <paramref name="thought"/> is what the brain view is told of the
    /// question beyond the question itself (the root's branches); it is told
    /// when the question is sent and again, with the answers and what came of
    /// them, when it is applied. <paramref name="answered"/> returns the
    /// verdict when the answer came to no order ("carry_on", "weak"), or null
    /// to let what it produced say it.
    /// </remarks>
    private void Ask(string occasion, Moment moment, Questions questions, double videoTime,
        RequestOptions request, Action? released, Thought thought, Func<SystemOneResponse, string?> answered)
    {
        var generation = _generation;
        thought = thought with { Id = ++_thoughtId, Occasion = occasion, VideoTime = videoTime };
        Thinking?.Invoke(thought with { Phase = "asked", Questions = Asked(questions), State = moment });
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
                var told = thought with
                {
                    Phase = "answered", ElapsedMs = elapsed, Model = response?.Model, Error = error,
                    Answers = response?.Answers
                        .Select(a => (a.Key, Answer: ThoughtAnswer.From(a.Value)))
                        .Where(a => a.Answer is not null)
                        .ToDictionary(a => a.Key, a => a.Answer!),
                };
                if (generation != _generation)
                {
                    Thinking?.Invoke(told with { Verdict = "stale" });
                    return;
                }
                if (response is null)
                {
                    if (occasion == "decide")
                        Tally("no answer");
                    Fail(videoTime, error ?? "no answer");
                    Thinking?.Invoke(told with { Verdict = "no answer" });
                    return;
                }
                _failing = false;
                var (keys, moves, cues) = (_keys.Count, _moves.Count, _cues.Count);
                string? verdict;
                try
                {
                    verdict = answered(response);
                }
                catch (Exception e)
                {
                    Fail(videoTime, $"the answer could not be read: {e.Message}");
                    verdict = "unreadable";
                }
                if (Thinking is null)
                    return;
                ThoughtDeed[] did =
                [
                    .. _keys.Skip(keys).Select(k => new ThoughtDeed("keyboard", k.Sentence) { Key = k.Chord }),
                    .. _moves.Skip(moves).Select(m => new ThoughtDeed(
                            m.Target is not null ? "attack" : m.AttackMove ? "attack_move" : "move", m.Sentence)
                        {
                            Direction = m.Direction, Toward = m.Destination?.Name ?? m.Target?.Name,
                        }),
                    .. _cues.Skip(cues).Where(c => !c.Failure).Select(c => new ThoughtDeed("voice", c.Reason)),
                ];
                Thinking(told with
                {
                    Verdict = verdict ?? (did.Length > 0 ? "acted" : "nothing"),
                    Did = did.Length > 0 ? did : null,
                });
            });
        }
    }

    /// <summary>The questions as the brain view shows them: each one's kind, rubric and options.</summary>
    private static ThoughtQuestion[] Asked(Questions questions) =>
        questions.Select(q => new ThoughtQuestion(q.Key, q.Value.Type, q.Value.Instructions as string,
                q.Value is ChoiceQuestion choice
                    ? choice.Criteria.Select(c => new ThoughtOption(c.Key, c.Value as string)).ToArray()
                    : null))
            .ToArray();

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
            SlotFacts facts;
            if (_casts.TryGetValue(slot, out var cast) && cast.Countdown is { } countdown)
            {
                var ready = cast.At + countdown;
                facts = now >= ready
                    ? new SlotFacts(slot, note?.Kind, note?.Range, "up", Math.Round(now - ready, 1), null, null)
                    : new SlotFacts(slot, note?.Kind, note?.Range, "cooldown", null, Math.Round(ready - now, 1), null);
            }
            else
                facts = new SlotFacts(slot, note?.Kind, note?.Range, "unknown", null, null,
                    _casts.ContainsKey(slot)
                        ? "the last cast's cooldown could not be read"
                        : "never seen cast this game, so no cooldown is running that the coach saw: up if it holds a point");
            if (frame is not null && self is not null && note?.Range is { } reach
                && EnemyMinionsWithin(frame, self, reach) is { } inside)
                facts = facts with { EnemyMinionsInRange = inside.Count, LowestEnemyMinionInRange = inside.Lowest };
            abilities.Add(facts);
        }

        List<EnemyFacts> enemies = [];
        List<AllyFacts> allies = [];
        WhereaboutsFacts? whereabouts = null;
        MinionFacts? minions = null;
        AttackFacts? attack = null;
        BrushFacts? brush = null;
        CoverFacts? coverFacts = null;
        if (frame is not null && self is not null)
        {
            whereabouts = Whereabouts(frame, self, now);
            if (self is { WorldX: { } bx, WorldY: { } by })
                brush = new BrushFacts(RiftBrush.At(bx, by)?.NameFrom(_side), NearBrushes(frame, self).Select(n => n.Fact).ToArray());
            minions = MinionsOnScreen(frame, self);
            coverFacts = CoverNow(frame, self);
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
                    Place = Map.Place(row.WorldX!.Value, row.WorldY!.Value),
                    InAttackRange = attackRange is { } r ? distance <= r : null,
                    UnderTheirTurret = TurretCover(row.WorldX!.Value, row.WorldY!.Value)?.Said,
                });
            }
            foreach (var row in frame.Champions.Where(c => c.Team == self.Team && c.TrackId != self.TrackId))
                allies.Add(new AllyFacts(row.Champion ?? $"track {row.TrackId}", row.Alive, Distance(self, row))
                {
                    Place = row is { Alive: not false, WorldX: { } ax, WorldY: { } ay } ? Map.Place(ax, ay) : null,
                });
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
            Cover = coverFacts,
            Coach = _recent.Select(r => new RecentAction(r.Did, Math.Round(now - r.At, 1))).ToArray(),
            Occasion = occasion,
            Setting = Moment.SettingFrom(_side),
            SkillPoint = SkillPointNow(self, now),
            Farming = FarmingNow(frame?.GameTime, now),
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
            // Without the turrets read, a wave is placed by their spots alone.
            var wave = Wave(lane, dots, x, y, _turrets is null ? null : tier => Standing(MinionTeam.Blue, lane, tier));
            var (progress, name) = WalkTo(lane, wave);
            var (sx, sy) = Map.At(lane, progress);
            var away = double.Hypot(sx - x, sy - y);
            return new LaneFacts(lane, Math.Round(toward.Distance),
                toward.Distance < 1 ? null : ScreenDirections.NameOfWorldOffset(toward.X - x, toward.Y - y), there)
            {
                Wave = wave,
                YourTurrets = LaneTurrets(MinionTeam.Blue, lane),
                TheirTurrets = LaneTurrets(MinionTeam.Red, lane),
                WalkTo = name,
                WalkToPlace = Map.Place(sx, sy),
                WalkToUnitsAway = Math.Round(away),
                WalkToScreenDirection = away < 1 ? null : ScreenDirections.NameOfWorldOffset(sx - x, sy - y),
                YouAre = YouAre(lane, x, y, progress),
            };
        }).ToArray();
        var still = _restAt is null ? 0 : Math.Max(0, Math.Round(now - _stillSince, 1));
        return new WhereaboutsFacts(Map.Place(x, y), still, lanes);
    }

    /// <summary>
    /// The farthest spot up a lane the player can walk to safely
    /// (<see cref="RiftMap.WalkTo"/>), off its wave and their turrets there.
    /// </summary>
    private (double Progress, string Name) WalkTo(string lane, WaveFacts? wave) =>
        Map.WalkTo(lane, _turrets is null ? null : tier => Standing(MinionTeam.Blue, lane, tier),
            wave?.OurFront, wave?.TheirFront);

    /// <summary>
    /// Where a player stands against a lane's <see cref="WalkTo"/> spot,
    /// along the lane: short of it, at it (within
    /// <see cref="RiftMap.LaneHalfWidth"/>), or past it. Only for the lane
    /// they stand in, or from their own base, which is behind every lane.
    /// </summary>
    private string? YouAre(string lane, double x, double y, double spot)
    {
        var place = Map.Place(x, y);
        if (RiftMap.LaneOf(x, y) != lane && place != RiftMap.FountainPlace && place != RiftMap.BasePlace)
            return null;
        var gap = (spot - Map.Along(lane, x, y).Progress) * RiftMap.Length(lane);
        return gap > RiftMap.LaneHalfWidth ? "short of it" : gap < -RiftMap.LaneHalfWidth ? "past it" : "at it";
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
    private WaveFacts? Wave(string lane, MinionDot[]? dots, double x, double y, Func<string, bool?>? standing)
    {
        if (dots is null || (dots.Length > 0 && dots.All(d => d.WorldX is null || d.WorldY is null)))
            return null;
        List<double> ours = [], theirs = [];
        foreach (var dot in dots)
        {
            if (dot is not { WorldX: { } dx, WorldY: { } dy } || RiftMap.LaneOf(dx, dy) != lane)
                continue;
            (dot.Team == MinionTeam.Blue ? ours : theirs).Add(Map.Along(lane, dx, dy).Progress);
        }
        double? ourFront = ours.Count > 0 ? Math.Round(ours.Max(), 2) : null;
        double? theirFront = theirs.Count > 0 ? Math.Round(theirs.Min(), 2) : null;
        var facts = new WaveFacts(ours.Count, theirs.Count, ourFront, theirFront, null, null, null);
        if (theirs.Count > 0)
        {
            var (tx, ty) = Map.At(lane, theirs.Min());
            var toTheirs = double.Hypot(tx - x, ty - y);
            facts = facts with
            {
                TheirFrontPlace = Map.EnemyFrontPlace(lane, theirs.Min(), standing),
                TheirFrontUnitsAway = Math.Round(toTheirs),
                TheirFrontScreenDirection = toTheirs < 1 ? null : ScreenDirections.NameOfWorldOffset(tx - x, ty - y),
            };
        }
        if (ourFront is not { } o || theirFront is not { } t)
            return facts;
        var meet = Math.Round((o + t) / 2, 2);
        var (mx, my) = Map.At(lane, meet);
        var away = double.Hypot(mx - x, my - y);
        return facts with
        {
            MeetAt = meet, MeetPlace = Map.Place(mx, my), MeetUnitsAway = Math.Round(away),
            MeetScreenDirection = away < 1 ? null : ScreenDirections.NameOfWorldOffset(mx - x, my - y),
        };
    }

    /// <summary>
    /// The minions on the player's screen, measured from where the player
    /// stands: how many of each side's, how near the enemy's are, and how far
    /// in front of their own foremost minion in the lane the player is. Null
    /// when the bars were not read or the player has no place on the map.
    /// </summary>
    private MinionFacts? MinionsOnScreen(FrameEnvelope frame, ChampionRow self)
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
            ? Math.Round((Map.Along(lane, x, y).Progress - front) * RiftMap.Length(lane))
            : null;
        return new MinionFacts(ours.Length, theirs.Length,
            reach.Length == 0 ? null : Math.Round(reach.Min()), reach.Count(d => d <= CasterMinionRange), ahead);
    }

    /// <summary>
    /// How far along a lane the player's own foremost minion on the screen
    /// has pushed (<see cref="RiftMap.Along"/>); null when none of theirs is
    /// on the screen in that lane, or the bars were not read.
    /// </summary>
    private double? OurFront(FrameEnvelope frame, string lane)
    {
        var fronts = (Carried(frame, r => r.Minions) ?? [])
            .Where(m => m.Team == MinionTeam.Blue && m is { WorldX: not null, WorldY: not null })
            .Select(m => Map.Along(lane, m.WorldX!.Value, m.WorldY!.Value))
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

    /// <summary>
    /// Who the player is: the game's cumulative majority of is_self rows,
    /// since a single frame's flag can still land on someone else. A new
    /// majority must strictly overtake the incumbent, so ties never flap.
    /// The count starts again at a new game (<see cref="NewGame"/>), where the
    /// player may be on another champion.
    /// </summary>
    private void VoteSelf(FrameEnvelope frame)
    {
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

    /// <summary>
    /// The player's row in the current frame: the one carrying their name
    /// (the majority of the rows the feed flagged), or else the
    /// flagged row when it carries no name at all -- the player's own seat on
    /// a frame whose name the reader has not (yet) put to it. Without that
    /// fallback a name seen once and then lost leaves no self in any frame,
    /// and the coach says nothing for as long as it stays lost.
    /// </summary>
    private ChampionRow? Self()
    {
        if (_frame is not { } frame)
            return null;
        var flagged = frame.Champions.FirstOrDefault(c => c.IsSelf);
        return _selfName is { } name
            ? frame.Champions.FirstOrDefault(c => c.Champion == name) ?? (flagged is { Champion: null } ? flagged : null)
            : flagged;
    }
}
