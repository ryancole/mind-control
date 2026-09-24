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
/// ever observes and advises: its output is where to look and why, never
/// input to the game.
/// </summary>
/// <remarks>
/// One attention decision, explained. Coaching is explanation-driven: the trace
/// and the log carry these so a human can audit *why* the ghost moved.
/// </remarks>
public sealed record GlanceNote(double VideoTime, ushort X, ushort Y, int Priority, string Reason);

/// <summary>
/// Coaching with nowhere to look. A glance is attention -- somewhere on the map
/// worth a look, so it carries a position and moves the ghost. A cue is
/// execution: what the player's own cast came to, or what a bolt at them came
/// to. Those are facts about a moment that has already passed and about the
/// player's own screen, so there is no place on the map to point at, and
/// pretending otherwise would send the ghost somewhere a good player would not
/// have looked.
/// </summary>
public sealed record CoachCue(double VideoTime, int Priority, string Reason);

/// <summary>
/// A key the coach would have pressed at this moment, and why. The keyboard
/// half of the demonstration: where a glance moves the ghost's cursor, a key
/// press is the ghost's hand on the keyboard. <see cref="Key"/> is the keycap
/// as the player knows it ("Q", "W", "D"), not a HID code; the recording maps
/// it. Like a cue it carries no position: the ability is aimed by the mouse,
/// and where the coach would have aimed is not yet demonstrated (the cursor
/// belongs to attention), so the press says only <em>that</em> and <em>when</em>.
/// </summary>
public sealed record KeyPress(double VideoTime, string Key, int Priority, string Reason)
{
    /// <summary>The line as the player reads it, wherever it is shown.</summary>
    public string Sentence => $"coach would have pressed {Key} here: {Reason}";
}

/// <summary>
/// A step the coach would have taken at this moment, and why. The movement
/// half of the demonstration: where a key press is the ghost's hand on the
/// keyboard, a step is its right-click on the ground. <see cref="Direction"/>
/// is the way as the player would say it ("left", "up-right"), and
/// (<see cref="Dx"/>, <see cref="Dy"/>) the same as a unit vector in their
/// screen space, y down; the recording turns it into a click a fixed distance
/// from the player's model, which sits at one place on their screen because
/// the camera is locked. It is an action and not somewhere to look, so unlike
/// a glance it does not compete for the cursor.
/// </summary>
public sealed record MoveStep(double VideoTime, string Direction, double Dx, double Dy, int Priority, string Reason)
{
    /// <summary>The line as the player reads it, wherever it is shown.</summary>
    public string Sentence => $"coach would have stepped {Direction} here: {Reason}";
}

public interface IPolicy
{
    /// <summary>Called once with the run's capability header before any frame or event.</summary>
    void Configure(Meta meta);

    /// <summary>Explanations of decisions made since the last drain.</summary>
    IReadOnlyList<GlanceNote> DrainNotes();

    /// <summary>Coaching said since the last drain that moves no cursor.</summary>
    IReadOnlyList<CoachCue> DrainCues() => [];

    /// <summary>Keys the coach would have pressed since the last drain.</summary>
    IReadOnlyList<KeyPress> DrainKeys() => [];

    /// <summary>Steps the coach would have taken since the last drain.</summary>
    IReadOnlyList<MoveStep> DrainMoves() => [];

    /// <summary>A fresh baseline after a gap, reconnect, or pause. Forget everything incremental.</summary>
    void Resync(FrameEnvelope? latest);

    /// <summary>Where attention should sit after this frame, or null to leave it be.</summary>
    GhostCursor? OnFrame(FrameEnvelope frame);

    /// <summary>Where attention should snap for this event, or null if it warrants no look.</summary>
    GhostCursor? OnEvent(GameEvent evt);
}

/// <summary>Placeholder while the plumbing is proven out. Watches, never acts.</summary>
public sealed class NoOpPolicy : IPolicy
{
    public void Configure(Meta meta) { }

    public IReadOnlyList<GlanceNote> DrainNotes() => [];

    public void Resync(FrameEnvelope? latest) { }

    public GhostCursor? OnFrame(FrameEnvelope frame) => null;

    public GhostCursor? OnEvent(GameEvent evt) => null;
}
