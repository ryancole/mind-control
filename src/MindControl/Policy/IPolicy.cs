using MindControl.Device;
using MindControl.Feed;

namespace MindControl.Policy;

/// <summary>
/// The decision layer: (state, event) → intents. Implementations must be pure
/// of I/O — no clocks, no sockets, no ports — so a recorded timeline replayed
/// through the feed exercises them exactly. Internal state derived from the
/// frames is fine; that state must be rebuildable from a /state snapshot via
/// <see cref="Resync"/>.
/// </summary>
public interface IPolicy
{
    /// <summary>Called once with the run's capability header before any frame or event.</summary>
    void Configure(Meta meta);

    /// <summary>A fresh baseline after a gap, reconnect, or panic. Forget everything incremental.</summary>
    void Resync(FrameEnvelope? latest);

    IReadOnlyList<Intent> OnFrame(FrameEnvelope frame);

    IReadOnlyList<Intent> OnEvent(GameEvent evt);
}

/// <summary>Placeholder while the plumbing is proven out. Watches, never acts.</summary>
public sealed class NoOpPolicy : IPolicy
{
    public void Configure(Meta meta) { }

    public void Resync(FrameEnvelope? latest) { }

    public IReadOnlyList<Intent> OnFrame(FrameEnvelope frame) => [];

    public IReadOnlyList<Intent> OnEvent(GameEvent evt) => [];
}
