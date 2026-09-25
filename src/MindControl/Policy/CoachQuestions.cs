using Jev;

namespace MindControl.Policy;

/// <summary>
/// The questions the coach model is asked, and the rubric each carries. This
/// is the coaching: what used to be a threshold in code is a sentence here,
/// and tuning the coach means editing these. The model answers with a number
/// or an option, never with words, so the copy the player reads is still
/// assembled in <see cref="JevPolicy"/> from the facts.
/// </summary>
public static class CoachQuestions
{
    /// <summary>One yes/no per button that is up: would a good player press it now?</summary>
    public static Questions Press(IEnumerable<string> slots)
    {
        var questions = new Questions();
        foreach (var slot in slots)
            questions.Noul($"press_{slot}",
                $"Would a good player in this seat press {slot} right now, throwing it at one of the "
                + "visible enemies? Weigh the target's distance against the ability's range and what the "
                + "ability is; how long they have stood inside range (an enemy passing through the edge of "
                + "range is not yet a target, one who has sat there a couple of seconds is); whether the "
                + "ability is really up (the HUD prints whole seconds, so a countdown that ended a fraction "
                + "of a second ago may have a fraction left); whether the resource bar can pay for it; and "
                + "whether the coach already pressed it moments ago without the player following. A good "
                + "player does not throw an ability into nothing and does not spam it.",
                yes: $"{slot} is up, a visible enemy has stood inside its range long enough, and a good player would throw it now",
                no: "hold it: nobody is worth throwing at yet, it is not really up, it cannot be paid for, or it was pressed moments ago");
        return questions;
    }

    public const string BoltRemark =
        "A bolt has just come at the player; `occasion` says where from, what came of it, how far they "
        + "moved across its line between first seeing it and its arrival, and how much warning there was. "
        + "Is this a moment a coach would point out to them? A bolt that hit while they stood still is. "
        + "A bolt they dodged is not, and neither is one that hit while they were already moving across "
        + "its line, however hard it hit: a bolt gives about a third of a second of warning, and being in "
        + "motion is all a player can bring to that. An outcome that could not be read is nothing known. "
        + "When `previous_landing_seconds_ago` is under half a second and the damage is the same, this is "
        + "the same hit read twice and has already been said: answer no.";

    public const string BoltStep =
        "Would a good player have stepped out of this bolt's way, where this player did not? A sidestep "
        + "across a bolt's line is what a good player does whenever one is coming, so the answer is yes "
        + "whenever the bolt hit them while they stood still, whatever the warning: the step demonstrates "
        + "the habit, and is not a claim that this bolt could have been reacted to. No when they dodged it, "
        + "no when they were already moving across its line (they had the habit), no when nothing is known "
        + "about the outcome, and no when `previous_landing_seconds_ago` is under half a second with the "
        + "same damage: that is the same hit read twice, and the coach already stepped for it.";

    public const string BoltSide =
        "Which way would a good player have stepped? Both options are across the bolt's line and clear "
        + "it by the same margin; each is described by whether it goes toward the player's own base and "
        + "toward or away from the nearest visible enemy. With nothing else to go on, step toward safety.";

    public const string ShotRemark =
        "The player has just thrown a skillshot that was seen leaving their champion with an enemy in "
        + "front of it; `occasion` says how far the bolt passed from that enemy and on which side, and "
        + "lists the recent shots that were seen at a target. Would a coach say something about their aim "
        + "now? One wide shot is not a pattern: about one in fourteen bolts credited to the player is "
        + "really someone else's, so a single wide shot among near ones is what a stray looks like, and a "
        + "couple of shots is too few to judge by. A run of wide shots among the recent seen ones is "
        + "worth a word, on the shot that made it a run. A near shot needs no praise.";
}
