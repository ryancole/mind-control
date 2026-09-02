using MindControl.Feed;
using MindControl.Policy;

namespace MindControl.Tests;

/// <summary>
/// Merging two policies over one feed. The properties that matter are that
/// nothing is swallowed -- every policy sees every frame and event, and their
/// notes and cues all come out -- and that the cursor rule is the documented
/// one rather than an accident.
/// </summary>
[TestClass]
public sealed class CompositePolicyTests
{
    /// <summary>A policy that records what it was shown and offers what it was told to.</summary>
    private sealed class Spy(GhostCursor? cursor = null) : IPolicy
    {
        public int Frames, Events, Configures, Resyncs;
        public readonly List<GlanceNote> Notes = [];
        public readonly List<CoachCue> Cues = [];

        public void Configure(Meta meta) => Configures++;
        public void Resync(FrameEnvelope? latest) => Resyncs++;

        public IReadOnlyList<GlanceNote> DrainNotes()
        {
            var drained = Notes.ToArray();
            Notes.Clear();
            return drained;
        }

        public IReadOnlyList<CoachCue> DrainCues()
        {
            var drained = Cues.ToArray();
            Cues.Clear();
            return drained;
        }

        public GhostCursor? OnFrame(FrameEnvelope frame) { Frames++; return cursor; }
        public GhostCursor? OnEvent(GameEvent evt) { Events++; return cursor; }
    }

    [TestMethod]
    public void Every_policy_sees_every_frame_and_event()
    {
        var first = new Spy(new GhostCursor(10, 10));
        var second = new Spy();
        var composite = new CompositePolicy(first, second);

        composite.Configure(new Meta { Schema = 1 });
        composite.OnFrame(new FrameEnvelope());
        composite.OnEvent(new GameEvent { Kind = EventKind.Death });
        composite.Resync(null);

        foreach (var spy in new[] { first, second })
        {
            Assert.AreEqual(1, spy.Configures);
            Assert.AreEqual(1, spy.Frames);
            Assert.AreEqual(1, spy.Events);
            Assert.AreEqual(1, spy.Resyncs);
        }
    }

    [TestMethod]
    public void The_first_cursor_offered_wins_and_the_rest_still_run()
    {
        var first = new Spy(new GhostCursor(10, 10));
        var second = new Spy(new GhostCursor(99, 99));
        var composite = new CompositePolicy(first, second);

        Assert.AreEqual(new GhostCursor(10, 10), composite.OnFrame(new FrameEnvelope()));
        Assert.AreEqual(1, second.Frames, "a policy that lost the cursor still saw the frame");
    }

    [TestMethod]
    public void A_policy_with_no_cursor_does_not_veto_a_later_one()
    {
        var composite = new CompositePolicy(new Spy(), new Spy(new GhostCursor(7, 7)));
        Assert.AreEqual(new GhostCursor(7, 7), composite.OnFrame(new FrameEnvelope()));
    }

    [TestMethod]
    public void Notes_and_cues_from_every_policy_come_out()
    {
        var first = new Spy();
        var second = new Spy();
        first.Notes.Add(new GlanceNote(1, 1, 1, 2, "look here"));
        second.Notes.Add(new GlanceNote(2, 2, 2, 1, "and here"));
        first.Cues.Add(new CoachCue(1, 1, "cast Q"));
        second.Cues.Add(new CoachCue(2, 3, "took a bolt"));
        var composite = new CompositePolicy(first, second);

        Assert.HasCount(2, composite.DrainNotes());
        Assert.HasCount(2, composite.DrainCues());
        Assert.IsEmpty(composite.DrainNotes(), "draining empties the sources");
    }

    [TestMethod]
    public void No_policies_is_silence_not_a_crash()
    {
        var composite = new CompositePolicy();
        Assert.IsNull(composite.OnFrame(new FrameEnvelope()));
        Assert.IsEmpty(composite.DrainNotes());
        Assert.IsEmpty(composite.DrainCues());
    }
}
