using MindControl.Feed;

namespace MindControl.Policy;

/// <summary>
/// The decision layer: (state, event) → a coaching cue. Implementations must be
/// pure of I/O — no clocks, no sockets, no ports — so a recorded timeline
/// replayed through the feed exercises them exactly. Internal state derived from
/// the frames is fine; that state must be rebuildable from a /state snapshot via
/// <see cref="Resync"/>. A policy only ever observes and advises: its output is
/// where to look and why, never input to the game.
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

public interface IPolicy
{
    /// <summary>Called once with the run's capability header before any frame or event.</summary>
    void Configure(Meta meta);

    /// <summary>Explanations of decisions made since the last drain.</summary>
    IReadOnlyList<GlanceNote> DrainNotes();

    /// <summary>Coaching said since the last drain that moves no cursor.</summary>
    IReadOnlyList<CoachCue> DrainCues() => [];

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
