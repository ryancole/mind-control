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
/// and the cue carries only the words.
/// </remarks>
public sealed record CoachCue(double VideoTime, int Priority, string Reason);

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
/// the camera is locked. A step with a <see cref="Destination"/> is a walk
/// across the map rather than a sidestep: the ground in view is too small a
/// canvas for a trip to lane, so the recording turns it into one right-click
/// on the minimap at that point, the order a player gives to go somewhere
/// far, and the direction then only says which way that is.
/// </summary>
public sealed record MoveStep(double VideoTime, string Direction, double Dx, double Dy, int Priority, string Reason)
{
    /// <summary>Where the coach is going, when the move is a walk across the map; null for a sidestep on the ground.</summary>
    public Destination? Destination { get; init; }

    /// <summary>The line as the player reads it, wherever it is shown.</summary>
    public string Sentence => Destination is { } to
        ? $"coach would have walked {Direction} to {to.Name} here: {Reason}"
        : $"coach would have stepped {Direction} here: {Reason}";
}

/// <summary>A place on the map, in game units, named as the player knows it ("bot lane").</summary>
public sealed record Destination(string Name, double X, double Y);

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

    /// <summary>A fresh baseline after a gap, reconnect, or pause. Forget everything incremental.</summary>
    void Resync(FrameEnvelope? latest);

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
