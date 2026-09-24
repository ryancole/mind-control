using MindControl.Feed;

namespace MindControl.Policy;

public sealed record CastOptions
{
    /// <summary>
    /// The coached player's champion, when known. Without it the per-frame
    /// <c>is_self</c> flag is used, which flaps onto allies whenever the
    /// viewport roams; <c>--self</c> is the reliable mode, as it is for
    /// attention.
    /// </summary>
    public string? SelfChampion { get; init; }

    /// <summary>
    /// How long an enemy must sit in range with the ability up before the
    /// coach presses it. On the fixture (a human Ezreal), a two-second hold
    /// presses Q 16 times in seventeen minutes, and on 9 of those the player
    /// pressed it themselves inside the next two seconds: the coach is a
    /// beat ahead, not somewhere else. One second doubles the count and
    /// starts firing on enemies passing through the edge of range.
    /// </summary>
    public double HoldSeconds { get; init; } = 2.0;

    /// <summary>
    /// Slack past the printed cooldown before the slot is trusted to be up.
    /// The HUD prints whole seconds and the reading lands a frame or two
    /// after the cast, so the countdown is a floor, not a clock.
    /// </summary>
    public double ReadyMarginSeconds { get; init; } = 0.5;

    /// <summary>
    /// Below this fraction of the resource bar the player cannot pay for
    /// the cast, and telling them to press it is telling them to press a
    /// greyed-out button. Read off the nameplate when it resolves (about a
    /// third of frames); the last reading stands between resolutions.
    /// </summary>
    public double MinResource { get; init; } = 0.15;

    /// <summary>
    /// The same slot is not pressed again inside this window. It is about
    /// a cooldown plus a hold, so a press the player ignores is repeated
    /// roughly as often as the ability comes back up, and no more.
    /// </summary>
    public double RepeatSeconds { get; init; } = 8.0;
}

/// <summary>
/// The abilities a coach throws at a visible enemy standing in range, per
/// champion: the slot and its range in world units. Only poke that is aimed
/// at an enemy belongs here. Ezreal's E is a blink and his R is global, and
/// "press E, there is an enemy 400 units away" is the opposite of coaching;
/// so neither is listed, and a champion not listed gets no cast coaching at
/// all rather than a guess.
/// </summary>
public static class AbilityKits
{
    public sealed record Poke(string Slot, double Range);

    private static readonly Dictionary<string, Poke[]> Kits = new()
    {
        ["Ezreal"] = [new Poke("Q", 1150), new Poke("W", 1200)],
    };

    public static IReadOnlyList<Poke> For(string? champion) =>
        champion is not null && Kits.TryGetValue(champion, out var kit) ? kit : [];
}

/// <summary>
/// The keyboard half of the demonstration: an ability that was up, with an
/// enemy in its range for long enough that a coach would have thrown it, and
/// the player did not. It presses the key -- as a <see cref="KeyPress"/>, which
/// the reactor logs, streams and records -- and says who was in range and for
/// how long.
///
/// <para><b>What it knows.</b> Which slots are up comes from the player's own
/// ability HUD: an <c>ability</c> event names the button and prints its
/// cooldown, so from then on the slot is known to be up at
/// <c>at + countdown</c> plus a margin. Nothing is assumed before the first
/// cast of a slot is seen -- the slot may not be skilled yet -- and a cast
/// whose countdown could not be read leaves the slot unknown until the next
/// one that can. Who is in range comes from the frame: a visible enemy row
/// with world coordinates, within the slot's range of the player's own row.
/// The ranges live in <see cref="AbilityKits"/>, per champion, and only for
/// poke. Mana is the nameplate's resource fraction, last reading standing.</para>
///
/// <para><b>What is said, and what is not.</b> A press when the enemy has sat
/// in range for <see cref="CastOptions.HoldSeconds"/> with the slot up, once
/// per <see cref="CastOptions.RepeatSeconds"/> per slot. The hold is measured
/// from whichever came later, the enemy entering range or the slot coming up,
/// so a cast that lands as they arrive is not followed by a press the moment
/// it is back. Silence while the slot is on cooldown or unknown, while the
/// enemy is in fog (a visible row only -- fair play, same rule as attention),
/// while the resource bar is empty, and while the player is dead. Nothing is
/// said about the cast itself: what the player's own cast came to is
/// <see cref="ExecutionPolicy"/>'s question.</para>
///
/// <para><b>What is not yet demonstrated.</b> Where the coach would have
/// aimed. A press is the key alone; the ghost's cursor is attention's, on the
/// minimap, and moving it to the target would need arbitration the composite
/// does not have (see <see cref="CompositePolicy"/>). And the press is not a
/// claim that the shot would have landed -- only that a good player throws
/// the ability when it is up and there is someone to throw it at.</para>
///
/// <para>Everything here is the player's own screen: their cooldowns, their
/// mana, enemies drawn in front of them. It produces key presses only, never
/// a cursor. All state resets on <see cref="Resync"/>: a gap may be a new game
/// in which the slots are not skilled, and a cooldown that straddles two games
/// is wrong in the way that matters. Re-learning costs one cast per slot.</para>
/// </summary>
public sealed class CastPolicy(CastOptions? options = null) : IPolicy
{
    private readonly CastOptions _options = options ?? new CastOptions();
    private readonly List<CoachCue> _cues = [];
    private readonly List<KeyPress> _keys = [];

    /// <summary>When each slot is known to be up; absent means not known at all.</summary>
    private readonly Dictionary<string, double> _readyAt = [];

    /// <summary>When the nearest enemy came inside each slot's range, and who it is now.</summary>
    private readonly Dictionary<string, (double Since, string Who, double Units)> _inRange = [];

    private readonly Dictionary<string, double> _lastPress = [];
    private double? _resource;

    public void Configure(Meta meta)
    {
        // A false flag means the HUD was not read, not that nothing was cast.
        // Without ability events no slot is ever known to be up, so this
        // policy would be silent for the whole run; say why, once.
        if (!meta.HasAbilities)
            _cues.Add(new CoachCue(0, 1,
                "cast coaching is off: this feed carries no ability stage "
                + "(spectral-sight needs its ability HUD calibrated)"));
    }

    public IReadOnlyList<GlanceNote> DrainNotes() => [];

    public IReadOnlyList<CoachCue> DrainCues() => Drain(_cues);

    public IReadOnlyList<KeyPress> DrainKeys() => Drain(_keys);

    private static IReadOnlyList<T> Drain<T>(List<T> list)
    {
        if (list.Count == 0)
            return [];
        var drained = list.ToArray();
        list.Clear();
        return drained;
    }

    public void Resync(FrameEnvelope? latest)
    {
        _keys.Clear();
        _readyAt.Clear();
        _inRange.Clear();
        _lastPress.Clear();
        _resource = null;
    }

    public GhostCursor? OnFrame(FrameEnvelope frame)
    {
        if (Self(frame) is not { } self)
        {
            _inRange.Clear();
            return null;
        }
        if (self.Resource is { } resource)
            _resource = resource;
        if (self.Alive == false)
        {
            _inRange.Clear();
            return null;
        }

        foreach (var poke in AbilityKits.For(self.Champion))
        {
            if (Nearest(frame, self, poke.Range) is not { } enemy)
            {
                _inRange.Remove(poke.Slot);
                continue;
            }
            if (!_inRange.TryGetValue(poke.Slot, out var held))
                held = (frame.VideoTime, enemy.Who, enemy.Units);
            _inRange[poke.Slot] = held with { Who = enemy.Who, Units = enemy.Units };

            if (!_readyAt.TryGetValue(poke.Slot, out var ready) || frame.VideoTime < ready)
                continue;
            if (_resource is { } mana && mana < _options.MinResource)
                continue;
            var heldFor = frame.VideoTime - Math.Max(held.Since, ready);
            if (heldFor < _options.HoldSeconds)
                continue;
            if (frame.VideoTime - _lastPress.GetValueOrDefault(poke.Slot, double.NegativeInfinity)
                < _options.RepeatSeconds)
                continue;

            _lastPress[poke.Slot] = frame.VideoTime;
            _keys.Add(new KeyPress(frame.VideoTime, poke.Slot, 2,
                $"{enemy.Who} has been in {poke.Slot} range ({enemy.Units:0} units) "
                + $"for {heldFor:0.0}s with {poke.Slot} up"));
        }
        return null;
    }

    public GhostCursor? OnEvent(GameEvent evt)
    {
        if (evt.Kind != EventKind.Ability || evt.Slot is not { } slot || evt.At is not { } at)
            return null;
        // A cast that could not be timed leaves the slot unknown: it is on
        // cooldown for some length nothing read, and a press before it is
        // back would be a press of a greyed-out button.
        if (evt.Countdown is { } countdown)
            _readyAt[slot] = at + countdown + _options.ReadyMarginSeconds;
        else
            _readyAt.Remove(slot);
        return null;
    }

    private ChampionRow? Self(FrameEnvelope frame) => _options.SelfChampion is { } name
        ? frame.Champions.FirstOrDefault(c => c.Champion == name)
        : frame.Champions.FirstOrDefault(c => c.IsSelf);

    /// <summary>
    /// The closest enemy the player can see inside <paramref name="range"/>.
    /// Visible rows only: an enemy in fog is not in range of anything the
    /// player knows about.
    /// </summary>
    private static (string Who, double Units)? Nearest(FrameEnvelope frame, ChampionRow self, double range)
    {
        if (self is not { WorldX: { } sx, WorldY: { } sy })
            return null;
        (string Who, double Units)? best = null;
        foreach (var row in frame.Champions)
        {
            if (row.Team == self.Team || !row.Visible || row is not { WorldX: { } x, WorldY: { } y })
                continue;
            var units = double.Hypot(x - sx, y - sy);
            if (units <= range && (best is null || units < best.Value.Units))
                best = (row.Champion ?? $"track {row.TrackId}", units);
        }
        return best;
    }
}
