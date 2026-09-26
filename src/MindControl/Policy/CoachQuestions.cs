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
        + "each lane is. Would a good player in this seat be walking to a lane right now instead? A good "
        + "player is always near the action: first at the minion wave, where the gold is farmed, and "
        + "otherwise where their team is; standing anywhere else is gold and experience left on the table. "
        + "From about 1:05 the minions march down every lane, a new wave each half minute, so a lane always "
        + "has a wave to farm. When a lane carries a `wave`, that is the minions the minimap shows in it: how "
        + "many of each side's, how far each side's front has pushed (0 is the player's own nexus, 1 the "
        + "enemy's, the middle of the lane about 0.5) and where the two waves meet, and how far that is from "
        + "the player; an empty one is a lane the minimap shows no minions in. When the lanes carry no `wave`, "
        + "the minions were not read, so read a lane as its wave. Yes when "
        + "they are idling in the fountain or their own base and nothing keeps them there. At the start of "
        + "the game a good player spends the first half minute or so buying and is on the way to lane by "
        + "about 0:45, well before the first minions meet around 1:30: standing in the fountain before then, "
        + "with the allies still in base too, is shopping, not idling, and after it is. After a recall or a "
        + "respawn later in the game they buy in a few seconds and walk straight back out. Yes when "
        + "they stand in the jungle or river with no enemy on the screen and nothing to do there. No when "
        + "they are already in a lane: standing in lane, waiting for the minions or holding ground against a "
        + "visible enemy, is laning, not idling. The one exception is a lane whose `wave` meets far up it from "
        + "where they stand, thousands of units off with no enemy on the screen: that is waiting at the "
        + "wrong end of the lane, and yes. No when an enemy is on the screen near them, because then "
        + "the moment is about that enemy. No when the still time is only a couple of seconds: a pause is not "
        + "idling. A good player moves with the broadest order that gets them there: one right-click on the "
        + "minimap sends the champion the whole way to the lane, and that is the walk the coach demonstrates, "
        + "never a string of short steps toward it. So one order is the whole demonstration: when "
        + "`whereabouts.coach_sent_them_to` names a lane, the coach has already sent them there from this very "
        + "spot, and ordering the walk again would only repeat that click; answer no.";

    public const string Lane =
        "Which lane would a good player in this seat be walking to? Each option gives its distance and "
        + "direction from where the player stands and which allies are already in it. A good player goes "
        + "where the action is, the minion wave first and their team second: the wave they are there to "
        + "farm for gold is the one in their own lane, so go by the champion's role first (a marksman or a "
        + "support belongs in bot lane, a mid champion in mid, a top champion in top), then by the allies: "
        + "the lane their partner or their team is in, and not a lane that already has the allies it needs. "
        + "Where an option says where its minions meet, that is where the walk goes; it does not change which "
        + "lane is theirs. Distance decides only between lanes that are otherwise equally theirs.";

    /// <summary>
    /// Asked while an enemy wave is at one of the player's turrets and the
    /// player is not at it: should they be on their way to it? Where each
    /// lane's minions are is in `whereabouts`; whose wave it is to catch is
    /// decided here.
    /// </summary>
    public const string Tend =
        "An enemy minion wave is at one of the player's own turrets and the player is not there; each lane's "
        + "`wave` in `whereabouts` says how many minions of each side the minimap shows in it, where the enemy's "
        + "front is (`their_front_place`, by the player's turrets), how far that is from the player, and which "
        + "allies are in the lane; `your_turrets` says which of the player's turrets in it still stand. A wave "
        + "past a fallen turret has nothing in its way until the next one in, which `their_front_place` names, "
        + "and every turret nearer the base is costlier to lose. Would a good player in this seat be on their way to that wave right now? A "
        + "wave left to crash into a turret is gold and experience lost for good and the turret worn down, so a "
        + "good player keeps their own lane's wave from being left alone: the lane their role belongs in (a "
        + "marksman or a support in bot, a mid champion in mid, a top champion in top). Yes when the wave is in "
        + "their own lane and no ally is there to take it, and nothing on the screen holds them: they are "
        + "roaming, in the jungle or river, in another lane, or dawdling in base after buying. No when an ally "
        + "is already in that lane to catch it; no when it is not their lane and their own needs them; no when "
        + "an enemy champion is close on the screen and the moment is a fight with them; no when the wave is "
        + "too far to reach before the turret kills it and their own lane has its own wave; and "
        + "no when the coach walked them toward that lane moments ago (`coach`): one minimap click is the "
        + "whole demonstration.";

    public const string TendLane =
        "Which lane's wave would a good player in this seat go to? Each option is a lane whose enemy wave is at "
        + "one of the player's turrets, with how far it is and which allies are there. Their own lane by role "
        + "comes first, then the one nobody is in, and distance decides only between lanes otherwise equal.";

    /// <summary>
    /// Asked while the player stands in a lane with enemy minions on their
    /// screen: should they step back behind their own? The counts and
    /// distances are in `minions`; where a laner stands is decided here.
    /// </summary>
    public const string Back =
        "The player is in a lane with enemy minions on the screen; `minions` says how many of each side's "
        + "are on the screen (a floor: bars in a clump hide each other), how far away the nearest enemy "
        + "minion is, how many enemy minions are within 550 units of the player (a caster minion's attack "
        + "range), and how far along the lane the player stands in front of their own foremost minion, toward "
        + "the enemy (negative is behind it; absent when none of their minions is on the screen). Would a good "
        + "player step back toward their own side of the lane right now? Minions attack an enemy champion "
        + "standing among them, so every enemy minion in reach is damage taken for nothing. A good player "
        + "stands behind the front of their own minions, where the enemy wave hits those minions and not "
        + "them, and steps forward only to last-hit and back out again. Yes when they stand in front of their "
        + "own minions with enemy minions in reach, or among enemy minions with none of their own on the "
        + "screen to take the hits. No when they are behind their own front, or no enemy minion is in reach. "
        + "No when an enemy champion is close on the screen and the moment is a fight with them: the fight "
        + "decides where they stand, not the minions. No when the coach stepped them back a moment ago "
        + "(`coach`) and they have not walked back in since.";

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
    /// Asked when the player's HUD shows a skill point waiting, and again
    /// while it goes on waiting: is the point to be spent now? The level,
    /// how long the point has waited and which buttons have been seen cast
    /// are in the state; whether there is ever a reason to hold a point, and
    /// for how long, is decided here, in words.
    /// </summary>
    public const string Spend =
        "The player has an ability point waiting; `occasion` says at what level, how long it has waited "
        + "(`held_for_seconds`, zero when it has just come), and `abilities` says which buttons have been "
        + "seen cast this game (one never seen cast may have no point in it yet). Would a good player "
        + "spend the point right now? Yes: putting a point in takes no time and can be done in a fight, "
        + "in lane or while dead, and an unspent point is power left on the table, so a good player "
        + "spends it the moment the level comes. The one point a good player ever holds is the first, "
        + "at level one, in case the game opens with an invade: they hold it while the game clock has "
        + "not yet started or is in its first half minute and nobody is on the screen, and spend it the "
        + "moment an enemy shows up, they set off for lane, or the clock passes about a minute. Every "
        + "point after that is spent at once, whoever is on the screen.";

    public const string Slot =
        "Which ability does this champion's usual skill order put the point into at this level? Each "
        + "option says what the ability is, its place in the champion's usual order (the one usually "
        + "maxed first, second or last, or the ultimate), whether it has been seen cast this game, and "
        + "how many points the coach has put in it since the point it first saw "
        + "(`coach_watching_since_level`; the points placed before that are in no option's count). The "
        + "options are the buttons the HUD lights for this point, and only those: a full ability is not "
        + "lit, nor is the ultimate at a level that does not take it. Three rules, the first outranking the second and the second "
        + "the third. First: whenever the ultimate is offered (levels six, eleven and sixteen), it takes "
        + "the point, ahead of every other ability, however far along the usual order is. Second: at "
        + "levels two and three the point goes to a basic ability never seen cast this game, whatever "
        + "the usual order, so that each basic ability has a point by level three. Third, from level "
        + "four on: the ability usually maxed first, for as long as it is offered (a full one is not); "
        + "once it is gone, the one usually maxed second; the one usually maxed "
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
