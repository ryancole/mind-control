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

    /// <summary>
    /// Asked while an enemy minion with a readable bar, or a visible enemy
    /// champion, is within or nearly within the player's basic-attack range:
    /// should they right-click to attack something now? What is in reach is
    /// in `attack`, `minions` and `visible_enemies`; when an attack is worth
    /// it is decided here.
    /// </summary>
    public const string Attack =
        "Enemies are within or nearly within reach of the player's basic attack: `attack.enemy_minions_near` lists "
        + "the enemy minions near them with how much of each one's health bar is left (`health`, 0 to 1), how fast "
        + "it has been falling (`falling_per_second`, 0 when it holds, absent when not yet known) and how soon it is "
        + "empty at that rate (`seconds_to_empty`), and whether the attack reaches it (`in_attack_range`; false "
        + "means a step outside it); `visible_enemies` the enemy champions with their own `in_attack_range` and, "
        + "when one stands in an enemy turret's range, `under_their_turret`; `attack.you_under_their_turret` the "
        + "enemy turret whose range the player stands in, if any, and `your_minions_under_that_turret` how many of "
        + "the player's minions stand there too; and `minions.theirs_within_caster_range` how many enemy minions "
        + "are within 550 units of the player. Would a good player right-click to attack something right now? "
        + "Gold comes only from the killing blow on a minion, so a good player's basic attacks in lane are mostly "
        + "last hits; attacking a champion draws every nearby enemy minion's attacks onto the attacker; and a "
        + "turret shoots an enemy champion who attacks a champion inside its range, and shoots whoever stands in "
        + "its range with no minions of their own there to shoot first. "
        + "Go by these rules in order; the first that applies decides. "
        + "First: `coach` has an entry that begins \"attacked\" with `seconds_ago` under 1.5: no, whatever else "
        + "holds, since that right-click is still being carried out (the champion keeps attacking its target and "
        + "a basic attack takes over a second to come round) and ordering it again only repeats the click. "
        + "Second: `attack.you_under_their_turret` is set and `your_minions_under_that_turret` is 0: no, the "
        + "turret has nobody else to shoot. "
        + "Third: an enemy minion in `attack.enemy_minions_near` has `health` of about 0.35 or less, or has more "
        + "left but a `seconds_to_empty` under about 1 (the attack takes about half a second to wind up and land, "
        + "and the minions' own fight finishes the rest): yes, it is a last hit. This holds whatever its "
        + "`in_attack_range` says, because the right-click walks the champion the last step and attacks, and "
        + "whoever else is on the screen, an enemy champion in range included. "
        + "Fourth: the player's own health is low and the enemy champion's is not: no. "
        + "Fifth: the only enemy champion worth attacking has `under_their_turret` set, or "
        + "`attack.you_under_their_turret` is set: no to the champion, since the turret would shoot the player "
        + "for it. "
        + "Sixth: an enemy champion has `in_attack_range` true, health no higher than the player's, and at most "
        + "one enemy minion is within 550 units of the player: yes, a trade the player wins. "
        + "Seventh: `visible_enemies` is empty: yes, attack the wave to clear it, since there is nobody to trade "
        + "with and nobody to push the wave away from. "
        + "Otherwise no: a minion with more bar left than a last hit and no quick fall is not yet worth an "
        + "attack while an enemy champion is on the screen, because hitting it early only pushes the wave toward "
        + "the enemy's turret and hands the last hit to the player's own minions.";

    public const string AttackTarget =
        "Which would a good player right-click? Each option is an enemy minion, with how much of its health bar is "
        + "left, how fast it is falling, how far it is and whether the attack reaches it, or an enemy champion, "
        + "with the same and whether they stand under one of their turrets. A minion one basic attack finishes "
        + "comes first, since a last hit is gold that is gone the moment it dies to anything else: the one whose "
        + "bar will be empty soonest, within range before one the player would have to walk to. Then an enemy "
        + "champion in range whose trade is the player's and who does not stand under their turret; never one "
        + "who does. Then, with no enemy champion on the screen, the lowest minion in range, to clear the wave.";

    /// <summary>
    /// Asked while the player stands outside the brush with a patch near
    /// them: should they walk into it? Where each patch is, and what makes it
    /// safe or not, is in `brush`; when brush is worth standing in is decided
    /// here.
    /// </summary>
    public const string Hide =
        "The player stands outside the brush with brush near them: `brush.near` lists the patches within a few "
        + "seconds' walk, nearest first, each with how far and which way it is, where it lies (`place`), how far "
        + "along its lane it lies in front of the player's own foremost minion (`ahead_of_your_minions_units`, "
        + "negative is behind it), the enemy turret covering it (`under_their_turret`), how far the nearest enemy "
        + "champion and enemy minion on the screen stand from it, the allies in it, whether it lies toward the "
        + "player's own base (`toward_your_base`), and `face_check`, set when walking into it would be blind: in "
        + "front of their minions, under an enemy turret, or in the enemy's jungle. Would a good player walk into the brush right now? Brush hides "
        + "whoever stands in it from every enemy outside it, while they still see out: an enemy cannot target, poke "
        + "or track a champion they cannot see, and does not know where they will come from. So a good player "
        + "stands in brush whenever it costs them nothing, and treats the open ground beside a patch as wasted "
        + "cover. But brush hides enemies too, and an enemy the player cannot see may be standing in any patch. "
        + "Go by these rules in order; the first that applies decides. "
        + "First: `coach` has an entry that begins \"walked into\" with `seconds_ago` under 6: no, whatever else "
        + "holds, since that order is still being carried out and one click is the whole demonstration. "
        + "Second: every patch in `brush.near` has `face_check` set: no, since walking blind into brush where an "
        + "enemy may wait is how a player is caught. "
        + "Third: the player's own health is about 0.35 or less and an enemy champion is on the screen: yes, into "
        + "a patch with `toward_your_base` true, which breaks the enemy's sight of them and any chase. "
        + "Fourth: an enemy champion in `visible_enemies` has `distance_units` under about 600: no, the two are "
        + "already trading blows and the fight decides where the player stands, not the brush. "
        + "Fifth: `whereabouts.place` names a lane, an enemy champion is on the screen, and a patch of that lane "
        + "without `face_check` has `nearest_enemy_minion_units` under about 700, or no enemy minion is on the "
        + "screen: yes. Standing in the lane's brush beside the wave hides them from the enemy laner, who cannot "
        + "poke what they cannot see, while they still last-hit from its edge. "
        + "Sixth: `whereabouts.place` names a lane, `whereabouts.stood_still_for_seconds` is 2 or more, and a patch "
        + "of that lane is without `face_check`: yes. A laner standing still in their "
        + "lane, for the first wave or for the next, waits in the lane's brush rather than out in the open, so the "
        + "enemy laner arrives not knowing where they are. "
        + "Otherwise no: standing still in the river or a jungle is not waiting but idling, which is not answered by "
        + "hiding; a player walking somewhere with a purpose (to lane, to a wave, to a fight) does not detour for "
        + "brush; and one farming a lane with no enemy champion on the screen has nobody to hide from.";

    public const string WhichBrush =
        "Which patch would a good player walk into? Each option gives the patch's distance, direction, where it "
        + "lies and what makes it safe or not. Go by these rules in order. Never an option that says it is a "
        + "face-check, whatever else it offers. Retreating on low health "
        + "with an enemy on the screen: the one toward their own base. In a lane with an enemy champion on the "
        + "screen: the lane's patch whose nearest enemy minion is closest to it, since that is the one they can "
        + "still last-hit from; whether it lies toward their base does not matter here. Otherwise the nearest.";

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
