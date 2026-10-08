using MindControl.Feed;

namespace MindControl.Policy;

/// <summary>
/// The decision layer: (state, event) → a coaching cue. Implementations must be
/// pure of I/O of their own — no clocks, no sockets, no ports — so a recorded
/// timeline replayed through the feed exercises them exactly. The one I/O a
/// policy may do is put a question to the coach model, and that goes through
/// an injected client a test can script (see <see cref="JevPolicy"/>).
/// Internal state derived from the frames is fine; that state must be
/// rebuildable from a /state snapshot via <see cref="Resync"/>. A policy only
/// ever observes and advises: its output is what a good player would have
/// done and why, never input to the game.
/// </summary>
/// <remarks>
/// Coaching that is said and not done: what the player's own cast came to, or
/// what a bolt at them came to. Those are facts about a moment that has
/// already passed, so there is nothing for the ghost's hands to do about them,
/// and the cue carries only the words. <see cref="Failure"/> marks a cue that
/// is not coaching at all but the coach saying it could not coach (the model
/// did not answer), so the console can tell the two apart.
/// </remarks>
public sealed record CoachCue(double VideoTime, int Priority, string Reason, bool Failure = false);

/// <summary>
/// A key the coach would have pressed at this moment, and why. The keyboard
/// half of the demonstration: the ghost's hand on the keyboard. <see cref="Key"/>
/// is the keycap as the player knows it ("Q", "W", "D"), not a HID code; the
/// recording maps it. <see cref="WithControl"/> is Ctrl held around it, which
/// is how a point goes into an ability rather than the ability being cast;
/// the recording writes the chord. Like a cue it carries no position: the
/// ability is aimed by the mouse, and where the coach would have aimed is not
/// yet demonstrated, so the press says only <em>that</em> and <em>when</em>.
/// </summary>
public sealed record KeyPress(double VideoTime, string Key, int Priority, string Reason)
{
    /// <summary>Ctrl held while the key is tapped: the game's level-up chord.</summary>
    public bool WithControl { get; init; }

    /// <summary>The keys as the player would name them: "Q", or "Ctrl+Q".</summary>
    public string Chord => WithControl ? $"Ctrl+{Key}" : Key;

    /// <summary>The line as the player reads it, wherever it is shown.</summary>
    public string Sentence => $"coach would have pressed {Chord} here: {Reason}";
}

/// <summary>
/// A step the coach would have taken at this moment, and why. The movement
/// half of the demonstration: where a key press is the ghost's hand on the
/// keyboard, a step is its right-click on the ground. <see cref="Direction"/>
/// is the way as the player would say it ("left", "up-right"), and
/// (<see cref="Dx"/>, <see cref="Dy"/>) the same as a unit vector in their
/// screen space, y down; the recording turns it into a click a fixed distance
/// from the player's model, which sits at one place on their screen because
/// the camera is locked. A step with a <see cref="Destination"/> is one leg
/// of a trip somewhere farther (a lane, a wave, a patch of brush): a longer
/// click on the ground, aimed that way, or on the place itself once it is
/// nearer than that, and the next leg is asked a second later. A step with a
/// <see cref="Target"/> is an attack: the same right-click, on the enemy
/// minion or champion itself, which the game reads as an order to attack it.
/// A step that is an <see cref="AttackMove"/> is the same walk ordered as an
/// attack-move instead, which stops to attack an enemy that comes into range
/// on the way.
/// </summary>
public sealed record MoveStep(double VideoTime, string Direction, double Dx, double Dy, int Priority, string Reason)
{
    /// <summary>Where the coach is heading, when the step is a leg of a trip; null for a sidestep.</summary>
    public Destination? Destination { get; init; }

    /// <summary>
    /// How far the <see cref="Destination"/> is from the player's model in
    /// game units when the step is taken; null for a sidestep, or when not
    /// measured. A leg shorter than a walk's click lands on the place itself
    /// rather than past it.
    /// </summary>
    public double? DistanceUnits { get; init; }

    /// <summary>
    /// Where on the map the player's model stood when the step was taken, in
    /// game units; null when not given. With it a walk's click is moved off
    /// ground no one can walk on (<see cref="RiftWalls"/>), which would stop
    /// the champion at its edge.
    /// </summary>
    public (double X, double Y)? From { get; init; }

    /// <summary>What the coach attacks, when the click is on an enemy; null for a move.</summary>
    public AttackTarget? Target { get; init; }

    /// <summary>
    /// Where the <see cref="Target"/> was seen on the player's screen, in
    /// spectral-sight's world-view pixels (relative to the meta's
    /// <c>world_view</c>; see <see cref="GhostRecording.FeedLayout"/>); null when only its place on the
    /// map is known, as for a champion. With it the click lands on the
    /// target as the video shows it, not where its distance projects to.
    /// </summary>
    public (double X, double Y)? ViewPx { get; init; }

    /// <summary>
    /// Whether the step is ordered as an attack-move (the attack-move key,
    /// then a left-click on the ground) rather than a right-click: the
    /// champion walks there but stops to attack an enemy that comes into
    /// range on the way. For walks toward the fight -- up a lane, to a wave --
    /// never for a step away from it, which must not stop to shoot.
    /// </summary>
    public bool AttackMove { get; init; }

    /// <summary>The line as the player reads it, wherever it is shown.</summary>
    public string Sentence => (Destination, Target) switch
    {
        ({ } to, _) when AttackMove => $"coach would have attack-moved {Direction} toward {to.Name} here: {Reason}",
        ({ } to, _) => $"coach would have stepped {Direction} toward {to.Name} here: {Reason}",
        (_, { } target) => $"coach would have attacked {target.Name} {Direction} here: {Reason}",
        _ => $"coach would have stepped {Direction} here: {Reason}",
    };
}

/// <summary>A place on the map, in game units, named as the player knows it ("bot lane").</summary>
public sealed record Destination(string Name, double X, double Y);

/// <summary>
/// An enemy the coach right-clicks to attack, named as the player would say
/// it ("Karma", "the enemy minion at 20% health"), and how far from the
/// player's model it stands in game units: with the step's direction, where
/// on the screen the click lands.
/// </summary>
public sealed record AttackTarget(string Name, double DistanceUnits);

public interface IPolicy
{
    /// <summary>Called once with the run's capability header before any frame or event.</summary>
    void Configure(Meta meta);

    /// <summary>Coaching said since the last drain: words only, nothing for the hands.</summary>
    IReadOnlyList<CoachCue> DrainCues() => [];

    /// <summary>Keys the coach would have pressed since the last drain.</summary>
    IReadOnlyList<KeyPress> DrainKeys() => [];

    /// <summary>Steps the coach would have taken since the last drain.</summary>
    IReadOnlyList<MoveStep> DrainMoves() => [];

    /// <summary>
    /// What the coach did since the last drain, as one line for the health
    /// log (who it took the player to be, what it asked and what came back),
    /// so a silent stretch says whether nothing was asked or every answer
    /// was no order; null when there is nothing to say.
    /// </summary>
    string? DrainActivity() => null;

    /// <summary>A fresh baseline after a gap, reconnect, or pause. Forget everything incremental.</summary>
    void Resync(FrameEnvelope? latest);

    /// <summary>
    /// A new match began in the same run (<see cref="EventKind.NewGame"/>, or
    /// the envelope's game went up). Forget everything about the last one,
    /// including what <see cref="Resync"/> keeps because a gap is usually the
    /// same game: who the player is, which side they play from, the farm's
    /// totals. The reactor follows it with a <see cref="Resync"/> on the new
    /// match's first frame, as after any gap.
    /// </summary>
    void NewGame(int game) => Resync(null);

    /// <summary>A frame of game state; anything the coach decides is collected by the drains.</summary>
    void OnFrame(FrameEnvelope frame);

    /// <summary>An event from the feed; anything the coach decides is collected by the drains.</summary>
    void OnEvent(GameEvent evt);
}

/// <summary>Placeholder while the plumbing is proven out. Watches, never acts.</summary>
public sealed class NoOpPolicy : IPolicy
{
    public void Configure(Meta meta) { }

    public void Resync(FrameEnvelope? latest) { }

    public void OnFrame(FrameEnvelope frame) { }

    public void OnEvent(GameEvent evt) { }
}
