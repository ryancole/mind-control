using MindControl.Feed;

namespace MindControl.Policy;

public sealed record ExecutionOptions
{
    /// <summary>
    /// How many of the player's most recent scored shots (per slot) and landed
    /// bolts the running count is over. Ten is a lane phase: the fixture's
    /// human Ezreal put nine Qs at a target in four and a half minutes, so a
    /// window this size holds the last few minutes of habit and forgets the
    /// game's first fight by its third.
    /// </summary>
    public int WindowSize { get; init; } = 10;

    /// <summary>
    /// Movement across a bolt's line below this is standing still. The
    /// fixture's nineteen threats split cleanly: twelve moved 0–17px across
    /// the line and seven moved 32–74px, with nothing between. 20px sits in
    /// that gap.
    /// </summary>
    public double StillPx { get; init; } = 20;

    /// <summary>
    /// Two hits whose arrivals fall within this many seconds are one fall of
    /// the player's health read twice. The outcome comes from the printed
    /// health falling in a window around each arrival, so two bolts landing
    /// together share the window and both get credited: the fixture has two
    /// such pairs, 0.04s and 0.22s apart, each pair carrying the same damage.
    /// </summary>
    public double SameFallSeconds { get; init; } = 0.5;
}

/// <summary>
/// The player's own execution: the shots they put wide, and the bolts that
/// found them standing still. Everything else it sees, it keeps to itself.
///
/// <para><b>What is said, and what the fixture said about it.</b> Measured on
/// 200–470s of <c>Recording 2026-08-30 200315</c> (a human Ezreal against
/// bots): 63 <c>ability</c>, 59 <c>skillshot</c> and 19 <c>threat</c> events in
/// four and a half minutes. A readout of every one of them ran to a line every
/// two seconds, every cast said twice (once as the button press, again a second
/// later as the bolt), and two thirds of the skillshot lines were "no enemy in
/// front of it". This policy says seven things over the same stretch:</para>
/// <list type="bullet">
/// <item><b>A shot that went wide</b>, with how far and on which side, and the
/// running count for that slot ("2 of your last 6 Qs at a target went wide").
/// Of the 59 skillshots, 19 launched nothing (a blink, a self-buff, or a bolt
/// the vision layer lost), 24 flew with no enemy on screen in front of them,
/// and 16 were scored; of those 13 passed within the hit radius and 3 went
/// wide, at 208, 274 and 331px. The wide ones are the only ones that carry
/// something to change. Hits are silence: a coach does not say "nice" every
/// time. The 43 unscored casts are silence too -- a bolt with nobody in front
/// of it is farming, and a cast with no bolt is a detection gap, not a fact
/// about the player.</item>
/// <item><b>A bolt that hit while they were standing still</b>, with the
/// damage and the running count ("3 of the last 5 that landed found you
/// still"). Of the 19 threats, 8 were hits -- 6 falls, two of them read twice
/// -- 4 dodged and 7 unread; 4 of the 6 falls found the player still. A hit
/// while moving is silence: a bolt gives about 0.3s of warning, the edge of
/// reaction, so being in motion is all a player can bring to the moment
/// and they brought it. A dodge and an unread outcome are silence: nothing to
/// change, or nothing known. It is a "bolt" and not a "skillshot" because a
/// threat is a candidate that launched near an enemy plate, and a ranged
/// auto-attack qualifies as readily as a Q; the 12-damage hit in the fixture
/// is likely one. Until spectral-sight's Phase 5 names the ability, the copy
/// does not claim the bolt was dodgeable, only that they were not moving.</item>
/// <item><b>Nothing for an <c>ability</c> event.</b> "Cast Q, 5s cooldown" is
/// what the player just did with their own hand, and every skillshot cast is
/// said again a second later by the <c>skillshot</c> event with the same
/// <c>at</c>, which carries everything this one does and more. Summoner spells
/// (D/F) are the player's own HUD too.</item>
/// </list>
///
/// <para><b>Two things the copy must not overstate.</b> A skillshot's
/// hit/miss is geometric -- did the bolt's line pass within the stage's hit
/// radius of the target's model, a radius that is the least settled number
/// upstream -- and not read off the target's health, which on this footage
/// falls in half of all windows regardless. So a wide shot is said as where
/// the bolt <em>passed</em>, never as "you missed", and <c>fall</c> is not
/// used in copy at all (it rode along on 11 of 13 near shots and 1 of 3 wide
/// ones, which is corroboration and not a verdict). The side the shot passed
/// on comes from <c>lead</c>, whose sign is unvalidated upstream for want of
/// misses to check it against. And a bolt at the player is never "reacted to
/// late": the only response measurable at 0.3s of warning is whether they were
/// already moving across its line, which is <c>moved_across</c> and is the
/// number that is said.</para>
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

    /// <summary>Per slot, the last scored shots: true where the bolt went wide.</summary>
    private readonly Dictionary<string, Queue<bool>> _shots = [];

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
        _shots.Clear();
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
        // No `miss` means nothing to judge: the cast launched no bolt, or the
        // bolt flew with no enemy on screen in front of it.
        if (evt.Miss is not { } miss || evt.Slot is not { } slot)
            return;
        var wide = evt.Outcome == "missed";
        var window = Push(Shots(slot), wide);
        if (!wide)
            return;

        var side = evt.Lead switch
        {
            > 0 => "ahead of them",
            < 0 => "behind them",
            _ => "wide of them",
        };
        var wideCount = window.Count(w => w);
        var tally = window.Count > 1
            ? $"; {wideCount} of your last {window.Count} {slot}s at a target went wide"
            : "";
        _cues.Add(new CoachCue(evt.VideoTime, 2, $"{slot} passed {miss:0}px {side}{tally}"));
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

    private Queue<bool> Shots(string slot)
    {
        if (!_shots.TryGetValue(slot, out var window))
            _shots[slot] = window = new Queue<bool>();
        return window;
    }

    private Queue<bool> Push(Queue<bool> window, bool value)
    {
        window.Enqueue(value);
        while (window.Count > _options.WindowSize)
            window.Dequeue();
        return window;
    }
}
