namespace MindControl.Policy;

/// <summary>
/// What is known about a champion's buttons, per slot: what the ability is,
/// where it sits in the champion's usual skill order, and how far it
/// reaches, in world units. Reference facts handed to the
/// coach model alongside the moment, never a gate: a champion not listed is
/// still coached, with the model working from the slot letter and its own
/// knowledge of the game, and a listed one is coached with the range on the
/// table so the distance to a target is a fact and not a guess, and the
/// order on the table so a level's point goes where the champion's players
/// put it rather than to a coin flip between two abilities with room.
/// </summary>
public static class AbilityKits
{
    /// <summary>
    /// <paramref name="UsuallyMaxed"/> is the ability's place in the
    /// champion's usual skill order -- "first", "second" or "last" among the
    /// basic abilities -- and null for the ultimate, which has a place of
    /// its own (levels 6, 11 and 16), or for a champion with no order on file.
    /// </summary>
    public sealed record Note(string Slot, string Kind, double? Range, string? UsuallyMaxed = null);

    private static readonly Dictionary<string, Note[]> Kits = new()
    {
        ["Ezreal"] =
        [
            new Note("Q", "Mystic Shot: a skillshot poke", 1150, UsuallyMaxed: "first"),
            new Note("W", "Essence Flux: a skillshot", 1200, UsuallyMaxed: "second"),
            new Note("E", "Arcane Shift: a blink, not something to throw at an enemy", null, UsuallyMaxed: "last"),
            new Note("R", "Trueshot Barrage: a global ultimate", null),
        ],
    };

    public static readonly string[] Slots = ["Q", "W", "E", "R"];

    public static Note? For(string? champion, string slot) =>
        champion is not null && Kits.TryGetValue(champion, out var kit)
            ? kit.FirstOrDefault(n => n.Slot == slot)
            : null;

    /// <summary>The longest range on file for the champion, or null when nothing is.</summary>
    public static double? Reach(string? champion) =>
        champion is not null && Kits.TryGetValue(champion, out var kit)
            ? kit.Max(n => n.Range)
            : null;
}
