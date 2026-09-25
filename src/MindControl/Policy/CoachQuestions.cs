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

    /// <summary>
    /// Asked while the player stands on one spot: should they be walking to a
    /// lane instead? The still time, the place and the lanes are in
    /// `whereabouts`; what counts as idling is decided here, in words.
    /// </summary>
    public const string Walk =
        "The player has been standing on one spot; `whereabouts` says where, for how long, and how far "
        + "each lane is. Would a good player in this seat be walking to a lane right now instead? Yes when "
        + "they are idling in the fountain or their own base and nothing keeps them there. At the start of "
        + "the game a good player spends the first half minute or so buying and is on the way to lane by "
        + "about 0:45, well before the first minions meet around 1:30: standing in the fountain before then, "
        + "with the allies still in base too, is shopping, not idling, and after it is. After a recall or a "
        + "respawn later in the game they buy in a few seconds and walk straight back out. Yes when "
        + "they stand in the jungle or river with no enemy on the screen and nothing to do there. No when "
        + "they are already in a lane: standing in lane, waiting for the minions or holding ground against a "
        + "visible enemy, is laning, not idling. No when an enemy is on the screen near them, because then "
        + "the moment is about that enemy. No when the still time is only a couple of seconds: a pause is not "
        + "idling. The coach having stepped toward a lane a few seconds ago while the player still stands is "
        + "a reason to step again, not to stop: the ghost keeps walking until the player does.";

    public const string Lane =
        "Which lane would a good player in this seat be walking to? Each option gives its distance and "
        + "direction from where the player stands and which allies are already in it. Go by the champion's "
        + "role first (a marksman or a support belongs in bot lane, a mid champion in mid, a top champion in "
        + "top), then by the allies: the lane their partner is in, and not a lane that already has the allies "
        + "it needs. Distance decides only between lanes that are otherwise equally theirs.";

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

    /// <summary>
    /// Asked when the player reaches a new level: is the point to be spent
    /// now? The level and which buttons have been seen cast are in the
    /// state; whether there is ever a reason to hold a point is decided
    /// here, in words.
    /// </summary>
    public const string Spend =
        "The player has just reached a new level; `occasion` says which, and `abilities` says which "
        + "buttons have been seen cast this game (one never seen cast may have no point in it yet). "
        + "Would a good player spend the new ability point right now? Yes: putting a point in takes no "
        + "time and can be done in a fight, in lane or while dead, and an unspent point is power left on "
        + "the table, so a good player spends it the moment the level comes. The one point a good player "
        + "ever holds is the first, at level one, in case the game opens with an invade; every point "
        + "after that is spent at once, whoever is on the screen.";

    public const string Slot =
        "Which ability does this champion's usual skill order put the point into at this level? Each "
        + "option says what the ability is, its place in the champion's usual order (the one usually "
        + "maxed first, second or last, or the ultimate), whether it has been seen cast this game, and "
        + "how many points the coach has put in it since the level-up it first saw "
        + "(`coach_watching_since_level`; the points placed before that, the level-one point among "
        + "them, are in no option's count). Three rules, the first outranking the second and the second "
        + "the third. First: whenever the ultimate is offered (levels six, eleven and sixteen), it takes "
        + "the point, ahead of every other ability, however far along the usual order is. Second: at "
        + "levels two and three the point goes to a basic ability never seen cast this game, whatever "
        + "the usual order, so that each basic ability has a point by level three. Third, from level "
        + "four on: the ability usually maxed first, for as long as it is offered (one the coach has "
        + "filled is not offered); once it is gone, the one usually maxed second; the one usually maxed "
        + "last takes no further point until both are gone. Where no order is on file, go by what the "
        + "abilities are and what this champion's players usually max.";

    public const string ShotRemark =
        "The player has just thrown a skillshot that was seen leaving their champion with an enemy in "
        + "front of it; `occasion` says how far the bolt passed from that enemy and on which side, and "
        + "lists the recent shots that were seen at a target. Would a coach say something about their aim "
        + "now? One wide shot is not a pattern: about one in fourteen bolts credited to the player is "
        + "really someone else's, so a single wide shot among near ones is what a stray looks like, and a "
        + "couple of shots is too few to judge by. A run of wide shots among the recent seen ones is "
        + "worth a word, on the shot that made it a run. A near shot needs no praise.";
}
