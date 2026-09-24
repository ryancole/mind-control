using MindControl.Feed;

namespace MindControl.Policy;

public sealed record ExecutionOptions
{
    /// <summary>
    /// How many of the player's most recent shots that were seen at a target,
    /// and of the bolts that landed on them, the running counts are over. Ten
    /// seen shots is about a fight: the fixture's human Ezreal had 14 shots
    /// seen at a target in nine minutes of lane and 21 in the seven minutes of
    /// fighting after it, so a window this size holds the last fight or two
    /// and has forgotten the lane by the end of the first.
    /// </summary>
    public int WindowSize { get; init; } = 10;

    /// <summary>
    /// How many shots must have been seen at a target before anything is said
    /// about aim. Half a window. Below it the count is a couple of shots,
    /// and one of those being a stray (see <see cref="MinWideShots"/>) is as
    /// likely as not.
    /// </summary>
    public int MinAimedShots { get; init; } = 5;

    /// <summary>
    /// How many of the seen shots in the window must have gone wide before
    /// aim is a tendency worth saying. One is not: about 7% of the bolts
    /// spectral-sight credits to a cast are still not the player's shot (the
    /// stray floor left after its origin gate), so a lone wide shot in a
    /// window of ten is exactly what a stray looks like. The fixture's lane
    /// phase has one wide shot in nine minutes, and it stays silent.
    /// </summary>
    public int MinWideShots { get; init; } = 2;

    /// <summary>
    /// Movement across a bolt's line below this is standing still. The
    /// fixture's 22 hits split cleanly: nineteen moved 0–17px across the
    /// line and three moved 40–74px, with nothing between. 20px sits in that
    /// gap.
    /// </summary>
    public double StillPx { get; init; } = 20;

    /// <summary>
    /// Two hits whose arrivals fall within this many seconds are one fall of
    /// the player's health read twice. The outcome comes from the printed
    /// health falling in a window around each arrival, so two bolts landing
    /// together share the window and both get credited: the fixture has four
    /// such groups (0.04, 0.22, 0.04 and 0.0/0.26s apart), each carrying the
    /// same damage. The cost is one pair of distinct hits 0.26s apart (207
    /// then 40 damage) that gets folded too; the widest pair of distinct hits
    /// otherwise is 0.51s apart, just outside.
    /// </summary>
    public double SameFallSeconds { get; init; } = 0.5;
}

/// <summary>
/// The player's own execution: a run of shots going wide, and the bolts that
/// found them standing still. Everything else it sees, it keeps to itself.
///
/// <para><b>What the fixture is.</b> The whole of <c>Recording 2026-08-30
/// 200315</c> (a human Ezreal against bots, video 142–1121s, lane until about
/// 700s and fights after), exported by spectral-sight's gated build of
/// 2026-09-02 as <c>data/coach-full-20260902-222718.jsonl</c>. The gate
/// matters: before it, most bolts credited to a cast were not the player's
/// shot at all (enemy bolts arriving, allied bolts passing, effects near the
/// model), and 7 of the 8 wide shots it reported on 150–700s were strays.
/// After it, a credited bolt is one whose line traces back through the
/// player's model. That leaves 224 casts of which 86 were seen leaving the
/// model (Q 46 of 121, W 32 of 65, E 5 of 28, R 3 of 10), 35 seen with an
/// enemy in front of them, 24 of those near and 11 wide. In lane: 108 casts,
/// 37 seen, 14 at a target, 1 wide. In the fights: 116 casts, 49 seen, 21 at a
/// target, 10 wide. About a third of casts are seen; the other two thirds
/// are silence, and that silence errs toward under-counting shots thrown,
/// never toward inventing one. See <c>spectral-sight/docs/aim-bolt-findings.md</c>.</para>
///
/// <para><b>What is said.</b></para>
/// <list type="bullet">
/// <item><b>A run of shots going wide</b>, on the shot that made it one: how
/// far and on which side that one passed, and the running count over the
/// shots that were seen ("3 of the last 10 shots that were seen went wide").
/// A single wide shot is never said. Two things make a lone verdict noise: a
/// stray floor of about 7% on credited bolts, so one wide shot in a window of
/// ten is what a stray looks like; and the hit radius upstream (130px), whose
/// justification was measured on the strays and is now nothing. So hit and
/// miss are a tendency over many shots and never a score for one, and the
/// policy waits for <see cref="ExecutionOptions.MinAimedShots"/> seen shots
/// holding <see cref="ExecutionOptions.MinWideShots"/> wide ones. On the
/// fixture that is silence through the lane (the one wide shot at 482s is
/// 1 of 10) and ten cues through the fights, the first at 797s. The count is
/// over every slot together: the habit is the player's, not the button's,
/// and per slot there would rarely be enough seen shots to say anything
/// (E was seen at a target three times in the game, R once). Near shots are
/// silence: a coach does not say "nice" every time. A bolt with nobody in
/// front of it is farming, and a cast with no bolt is a shot the stage did
/// not see, not a fact about the player; neither enters the count.</item>
/// <item><b>A bolt that hit while they were standing still</b>, with the
/// damage and the running count ("3 of the last 5 that landed found you
/// still"). Of the fixture's 54 threats, 22 were hits, 13 dodged and 19
/// unread; folding the falls read twice leaves 16 landings, and 14 of them
/// found the player still. A hit while moving is silence: a bolt gives about
/// 0.3s of warning, the edge of reaction, so being in motion is all a player
/// can bring to the moment and they brought it. A dodge and an unread outcome
/// are silence: nothing to change, or nothing known. It is a "bolt" and not a
/// "skillshot" because a threat is a candidate that launched near an enemy
/// plate, and a ranged auto-attack qualifies as readily as a Q; the 12-damage
/// hit at 218s is likely one. Until spectral-sight's Phase 5 names the
/// ability, the copy does not claim the bolt was dodgeable, only that they
/// were not moving.</item>
/// <item><b>Nothing for an <c>ability</c> event.</b> "Cast Q, 5s cooldown" is
/// what the player just did with their own hand, and every skillshot cast is
/// said again a second later by the <c>skillshot</c> event with the same
/// <c>at</c>, which carries everything this one does and more. Summoner spells
/// (D/F) are the player's own HUD too.</item>
/// </list>
///
/// <para><b>Things the copy must not overstate.</b> The denominator of an
/// aim count is the shots that were <em>seen</em>, and the copy says so; "of
/// N casts" would be wrong by a factor of three. A skillshot's hit/miss is
/// geometric -- did the bolt's line pass within the stage's hit radius of the
/// target's model, a radius nothing has justified -- and not read off the
/// target's health, which on this footage falls in half of all windows
/// regardless. So a wide shot is said as where the bolt <em>passed</em>,
/// never as "you missed", and <c>fall</c> is not used in copy at all (it rode
/// along on 15 of the 24 near shots and 5 of the 11 wide ones, which is
/// corroboration and not a verdict). The side the shot passed on comes from
/// <c>lead</c>, whose sign is unvalidated upstream. Strays are not filtered
/// here and cannot be: the event carries the bolt's launch, speed, heading,
/// miss and flight but not its position relative to the player, so the
/// trace-back test lives upstream and only there. And a bolt at the player is
/// never "reacted to late": the only response measurable at 0.3s of warning
/// is whether they were already moving across its line, which is
/// <c>moved_across</c> and is the number that is said.</para>
///
/// <para>Fair play is not in question here, which is worth stating since
/// every other policy in this repo has to argue it. All of it comes from the
/// player's own HUD and their own screen -- their cooldowns, their printed
/// health, their camera, and enemies drawn on the view in front of them. None
/// of it is fog information, and none of it is anything the player could not
/// have seen themselves.</para>
///
/// <para>It produces cues only, never a cursor: none of this is somewhere to
/// look. See <see cref="CoachCue"/>. The running counts are the only state,
/// and they reset on <see cref="Resync"/>: a gap may be a pause or a new game,
/// and a count that straddles two games is wrong in the way that matters.</para>
/// </summary>
public sealed class ExecutionPolicy(ExecutionOptions? options = null) : IPolicy
{
    private readonly ExecutionOptions _options = options ?? new ExecutionOptions();
    private readonly List<CoachCue> _cues = [];

    /// <summary>The last shots seen at a target, every slot together: true where the bolt went wide.</summary>
    private readonly Queue<bool> _aimed = new();

    /// <summary>The last bolts that landed: true where the player was still.</summary>
    private readonly Queue<bool> _landed = new();

    private double? _lastLanding;

    public void Configure(Meta meta)
    {
        // A false flag means the stage did not run, not that nothing happened.
        // Saying so once beats a silent policy that looks broken. Abilities
        // alone do not count: this policy has nothing to say from them.
        if (!meta.HasThreats && !meta.HasSkillshots)
            _cues.Add(new CoachCue(0, 1,
                "execution coaching is off: this feed carries no threat or "
                + "skillshot stage (spectral-sight needs a --coach run)"));
    }

    public IReadOnlyList<GlanceNote> DrainNotes() => [];

    public IReadOnlyList<CoachCue> DrainCues()
    {
        if (_cues.Count == 0)
            return [];
        var drained = _cues.ToArray();
        _cues.Clear();
        return drained;
    }

    public void Resync(FrameEnvelope? latest)
    {
        _cues.Clear();
        _aimed.Clear();
        _landed.Clear();
        _lastLanding = null;
    }

    public GhostCursor? OnFrame(FrameEnvelope frame) => null;

    public GhostCursor? OnEvent(GameEvent evt)
    {
        switch (evt.Kind)
        {
            case EventKind.Skillshot:
                OnSkillshot(evt);
                break;
            case EventKind.Threat:
                OnThreat(evt);
                break;
        }
        return null;
    }

    private void OnSkillshot(GameEvent evt)
    {
        // No `miss` means nothing to judge: the stage saw no bolt leave the
        // player's model (two thirds of casts), or the bolt flew with no
        // enemy on screen in front of it.
        if (evt.Miss is not { } miss || evt.Slot is not { } slot)
            return;
        var wide = evt.Outcome == "missed";
        var window = Push(_aimed, wide);
        if (!wide)
            return;

        // One wide shot is a stray as often as not; a count that is mostly
        // one shot is the same thing with a denominator. Wait for the run.
        var wideCount = window.Count(w => w);
        if (window.Count < _options.MinAimedShots || wideCount < _options.MinWideShots)
            return;

        var side = evt.Lead switch
        {
            > 0 => "ahead of them",
            < 0 => "behind them",
            _ => "wide of them",
        };
        _cues.Add(new CoachCue(evt.VideoTime, 2,
            $"{slot} passed {miss:0}px {side}; "
            + $"{wideCount} of the last {window.Count} shots that were seen went wide"));
    }

    private void OnThreat(GameEvent evt)
    {
        if (evt.Outcome != "hit")
            return;
        var landing = evt.Arrival ?? evt.VideoTime;
        if (_lastLanding is { } previous && Math.Abs(landing - previous) <= _options.SameFallSeconds)
            return;   // the same fall of the health bar, credited to a second bolt
        _lastLanding = landing;

        // No motion measurement means nothing can be said about the response,
        // and a hit that cannot be judged does not go in the count either.
        if (evt.MovedAcross is not { } across)
            return;
        var still = across < _options.StillPx;
        var window = Push(_landed, still);
        if (!still)
            return;

        var damage = evt.Damage is { } d ? $" for {d}" : "";
        var stillCount = window.Count(s => s);
        var tally = window.Count > 1
            ? $"; {stillCount} of the last {window.Count} that landed found you still"
            : "";
        _cues.Add(new CoachCue(evt.VideoTime, 3, $"a bolt hit you{damage} while you were standing still{tally}"));
    }

    private Queue<bool> Push(Queue<bool> window, bool value)
    {
        window.Enqueue(value);
        while (window.Count > _options.WindowSize)
            window.Dequeue();
        return window;
    }
}
