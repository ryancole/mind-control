using Jev;

namespace MindControl.Policy;

/// <summary>
/// One question the coach put to the model, as the brain view draws it: the
/// tree it asked (for the root, every branch it considered, the ones the
/// moment offered and, for the rest, what closed them), then what came back
/// and what the ghost's hands did with it. A question is told twice under
/// one <see cref="Id"/>: once when it is sent (<see cref="Phase"/>
/// "asked", with the state the model was shown) and once when its answer is
/// in ("answered"). A root with nothing to offer is told once, as "idle".
/// It is a view of what the policy already decided, never an input to it.
/// </summary>
public sealed record Thought
{
    public int Id { get; init; }

    /// <summary>"asked", "answered", or "idle" (the root had nothing to offer, so nothing was asked).</summary>
    public string Phase { get; init; } = "";

    /// <summary>"decide" for the root, "bolt" or "shot" for an event.</summary>
    public string Occasion { get; init; } = "";

    /// <summary>The video time of the moment asked about.</summary>
    public double VideoTime { get; init; }

    /// <summary>
    /// The root's branches, in the rubric's order with <c>carry_on</c> first:
    /// every one the coach considered, offered or not. Null for an event.
    /// </summary>
    public IReadOnlyList<ThoughtBranch>? Branches { get; init; }

    /// <summary>Why the root asked only some of its branches, when a mode narrowed it ("channelling a recall").</summary>
    public string? Mode { get; init; }

    /// <summary>The questions as sent, root first. Set when asked.</summary>
    public IReadOnlyList<ThoughtQuestion>? Questions { get; init; }

    /// <summary>What the model was shown. Set when asked.</summary>
    public Moment? State { get; init; }

    /// <summary>The probability below which a root pick is no order.</summary>
    public double? DecideAt { get; init; }

    /// <summary>The probability at or above which an event's yes/no is a yes.</summary>
    public double? YesAt { get; init; }

    // --- Answered ---

    public long? ElapsedMs { get; init; }
    public string? Model { get; init; }
    public string? Error { get; init; }

    /// <summary>The answers by question, exactly as the model gave them.</summary>
    public IReadOnlyDictionary<string, ThoughtAnswer>? Answers { get; init; }

    /// <summary>
    /// What came of it: "acted" (a key, a step or a cue came of it),
    /// "carry_on", "weak" (a pick below <see cref="DecideAt"/>), "nothing" (a
    /// pick the branch found nothing to do for, or an event that was no
    /// remark), "unreadable", "no answer", or "stale" (answered after a
    /// resync, and dropped).
    /// </summary>
    public string? Verdict { get; init; }

    /// <summary>What the ghost's hands and voice did with it.</summary>
    public IReadOnlyList<ThoughtDeed>? Did { get; init; }
}

/// <summary>
/// One branch of the root. <see cref="Gate"/> is null when the moment offered
/// it, or what closed it: the measured reason the option was not put to the
/// model at all.
/// </summary>
public sealed record ThoughtBranch(string Option, string? Gate)
{
    /// <summary>The follow-up question the branch added to the request, when it added one.</summary>
    public string? FollowUp { get; init; }

    /// <summary>
    /// The branch's one candidate when it had only one and so asked no
    /// follow-up (a single button up, a single target in reach).
    /// </summary>
    public string? Only { get; init; }
}

/// <summary>A question as sent: its id, kind, rubric, and a choice's options with what each says.</summary>
public sealed record ThoughtQuestion(string Id, string Kind, string? Instructions, IReadOnlyList<ThoughtOption>? Options);

public sealed record ThoughtOption(string Name, string? Says);

/// <summary>An answer: a choice's pick and every option's probability, or a yes/no's probability of yes.</summary>
public sealed record ThoughtAnswer(string? Choice, IReadOnlyDictionary<string, double>? Probabilities, double? Yes)
{
    public static ThoughtAnswer? From(Answer answer) => answer switch
    {
        ChoiceAnswer choice => new(choice.Choice, choice.Probabilities, null),
        NoulAnswer noul => new(null, null, noul.Noul),
        _ => null,
    };
}

/// <summary>
/// Something the coach did with an answer. <see cref="Hand"/> is which of
/// the ghost's hands it took: "keyboard", "move" (a right-click on the
/// ground), "attack" (a right-click on an enemy), "attack_move" (A and a
/// left-click), or "voice" (a cue, words only).
/// </summary>
public sealed record ThoughtDeed(string Hand, string Said)
{
    /// <summary>The keys, for a key press ("Q", "Ctrl+Q").</summary>
    public string? Key { get; init; }

    /// <summary>The way, for a step ("up-left").</summary>
    public string? Direction { get; init; }

    /// <summary>Where a walk goes, or what an attack is on.</summary>
    public string? Toward { get; init; }
}
