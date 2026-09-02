using MindControl.Feed;

namespace MindControl.Policy;

/// <summary>
/// Several policies watching the same feed, their output merged.
///
/// <para>It exists because attention and execution are different questions
/// asked of the same frames -- where should you be looking, and how did what
/// you just did turn out -- and a player wants both. Keeping them as separate
/// policies keeps each one's reasoning readable and testable on its own, which
/// a single class answering both questions would not be.</para>
///
/// <para><b>The one place this is not yet a real design.</b> Notes and cues
/// merge cleanly, because they are lists and a log can hold both. Cursors do
/// not: two policies wanting attention in two places is a conflict, and there
/// is nothing here to arbitrate it. The first non-null wins, in the order the
/// policies were given. That is honest for the pair this was built for --
/// <see cref="ExecutionPolicy"/> never returns a cursor at all, so the
/// question never arises -- and it is the wrong answer for two policies that
/// both want to point somewhere. Add arbitration before adding a second one
/// that does; a `Priority` on the cursor is the obvious shape, and it should
/// wait until something needs it.</para>
/// </summary>
public sealed class CompositePolicy(params IPolicy[] policies) : IPolicy
{
    public void Configure(Meta meta)
    {
        foreach (var policy in policies)
            policy.Configure(meta);
    }

    public IReadOnlyList<GlanceNote> DrainNotes()
    {
        List<GlanceNote>? merged = null;
        foreach (var policy in policies)
        {
            var notes = policy.DrainNotes();
            if (notes.Count == 0)
                continue;
            (merged ??= []).AddRange(notes);
        }
        return merged is null ? [] : merged;
    }

    public IReadOnlyList<CoachCue> DrainCues()
    {
        List<CoachCue>? merged = null;
        foreach (var policy in policies)
        {
            var cues = policy.DrainCues();
            if (cues.Count == 0)
                continue;
            (merged ??= []).AddRange(cues);
        }
        return merged is null ? [] : merged;
    }

    public void Resync(FrameEnvelope? latest)
    {
        foreach (var policy in policies)
            policy.Resync(latest);
    }

    /// <summary>Every policy sees the frame -- they keep their own state -- and
    /// the first cursor offered is the one taken. See the class remarks.</summary>
    public GhostCursor? OnFrame(FrameEnvelope frame)
    {
        GhostCursor? chosen = null;
        foreach (var policy in policies)
        {
            // Called first, kept second. `chosen ??= policy.OnFrame(...)`
            // reads the same but short-circuits, so every policy after the
            // one that offered a cursor would never see the frame at all --
            // and a policy that never sees a frame never coaches.
            var offered = policy.OnFrame(frame);
            chosen ??= offered;
        }
        return chosen;
    }

    public GhostCursor? OnEvent(GameEvent evt)
    {
        GhostCursor? chosen = null;
        foreach (var policy in policies)
        {
            var offered = policy.OnEvent(evt);
            chosen ??= offered;
        }
        return chosen;
    }
}
