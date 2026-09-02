using MindControl.Feed;

namespace MindControl.Policy;

/// <summary>
/// The player's own execution, read back to them: which button they pressed,
/// where the bolt went, and what came at them.
///
/// <para><b>This is a readout, not coaching yet.</b> It states what the vision
/// layer measured, in the plainest words that do not overstate it, so the three
/// new signals are visible end to end while the question of what is actually
/// worth saying to a player is still open. Nobody needs to be told "your Q
/// launched" forty times a game; a later policy decides which of these are
/// worth a word, and when silence is better. Until then this deliberately
/// says everything, so what arrives can be judged.</para>
///
/// <para>Fair play is not in question here, which is worth stating since every
/// other policy in this repo has to argue it. All three signals come from the
/// player's own HUD and their own screen -- their cooldowns, their printed
/// health, their camera, and enemies drawn on the view in front of them. None
/// of it is fog information, and none of it is anything the player could not
/// have seen themselves.</para>
///
/// <para>It produces cues only, never a cursor: none of this is somewhere to
/// look. See <see cref="CoachCue"/>.</para>
/// </summary>
public sealed class ExecutionPolicy : IPolicy
{
    private readonly List<CoachCue> _cues = [];
    private bool _abilities, _threats, _skillshots;

    public void Configure(Meta meta)
    {
        // A false flag means the stage did not run, not that nothing happened.
        // Saying so once beats a silent policy that looks broken.
        _abilities = meta.HasAbilities;
        _threats = meta.HasThreats;
        _skillshots = meta.HasSkillshots;
        if (!_abilities && !_threats && !_skillshots)
            _cues.Add(new CoachCue(0, 1,
                "execution coaching is off: this feed carries no ability, threat "
                + "or skillshot stages (spectral-sight needs a --coach run)"));
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

    /// <summary>Nothing to rebuild: every cue is a moment, and a moment that
    /// happened while the feed was in doubt is one this policy never saw.</summary>
    public void Resync(FrameEnvelope? latest) => _cues.Clear();

    public GhostCursor? OnFrame(FrameEnvelope frame) => null;

    public GhostCursor? OnEvent(GameEvent evt)
    {
        var line = Describe(evt);
        if (line is not null)
            _cues.Add(new CoachCue(evt.VideoTime, Priority(evt), line));
        return null;
    }

    /// <summary>
    /// A bolt that hit the player outranks one they dodged, and both outrank a
    /// cast going out. Priorities are the dashboard's three colours and nothing
    /// more subtle than that until there is a reason for more.
    /// </summary>
    private static int Priority(GameEvent evt) => evt.Kind switch
    {
        EventKind.Threat when evt.Outcome == "hit" => 3,
        EventKind.Threat => 2,
        EventKind.Skillshot when evt.Outcome == "missed" => 2,
        _ => 1,
    };

    private static string? Describe(GameEvent evt) => evt.Kind switch
    {
        EventKind.Ability => Ability(evt),
        EventKind.Threat => Threat(evt),
        EventKind.Skillshot => Skillshot(evt),
        _ => null,
    };

    private static string Ability(GameEvent evt)
    {
        var countdown = evt.Countdown is { } seconds ? $", {seconds}s cooldown" : "";
        return $"cast {evt.Slot}{countdown}";
    }

    private static string Threat(GameEvent evt)
    {
        var what = evt.Outcome switch
        {
            "hit" => evt.Damage is { } damage ? $"took {damage} from a bolt" : "took a bolt",
            "dodged" => "a bolt missed you",
            _ => "a bolt reached you, outcome unread",
        };
        var closest = $" (passed {evt.Closest:0}px)";
        // The response, which is the only part a player can change: a bolt
        // gives about a quarter of a second of warning, so this is not "you
        // reacted late" -- it is whether they were already out of the line.
        var moved = evt.MovedAcross is { } across
            ? across < 20
                ? "; you were not moving across it"
                : $"; you had moved {across:0}px across it"
            : "";
        return what + closest + moved;
    }

    private static string Skillshot(GameEvent evt)
    {
        if (evt.Launched is null)
            return $"{evt.Slot} cast, no bolt seen leaving you";
        if (evt.Miss is not { } miss)
            return $"{evt.Slot} fired with no enemy in front of it";
        var side = evt.Lead is { } lead && Math.Abs(lead) >= 1
            ? lead > 0 ? " (ahead of them)" : " (behind them)"
            : "";
        return evt.Outcome switch
        {
            "hit" => $"{evt.Slot} hit, {miss:0}px off centre",
            "missed" => $"{evt.Slot} missed by {miss:0}px{side}",
            _ => $"{evt.Slot} fired, outcome unread",
        };
    }
}
