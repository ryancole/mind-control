using MindControl.Feed;

namespace MindControl.Policy;

public sealed record DodgeOptions
{
    /// <summary>
    /// Movement across a bolt's line below this is standing still. The same
    /// number as <see cref="ExecutionOptions.StillPx"/>, for the same reason:
    /// the fixture's hits split into 0–17px and 40–74px with nothing between,
    /// and a step is only owed where the player was not already taking one.
    /// </summary>
    public double StillPx { get; init; } = 20;

    /// <summary>
    /// Two hits whose arrivals fall within this many seconds are one fall of
    /// the player's health read twice, and one moment; see
    /// <see cref="ExecutionOptions.SameFallSeconds"/>. A coach steps once.
    /// </summary>
    public double SameFallSeconds { get; init; } = 0.5;
}

/// <summary>
/// Directions on the player's screen, named the way a coach says them.
/// Screen space throughout: x to the right, y <em>down</em>, which is the
/// space a threat's heading is in. Eight names, 45 degrees each.
/// </summary>
public static class ScreenDirections
{
    private static readonly string[] Names =
        ["right", "down-right", "down", "down-left", "left", "up-left", "up", "up-right"];

    private static readonly string[] Sources =
        ["the right", "the lower right", "below", "the lower left",
         "the left", "the upper left", "above", "the upper right"];

    /// <summary>The way to go: "left", "up-right", ...</summary>
    public static string Name(double dx, double dy) => Names[Sector(dx, dy)];

    /// <summary>Where a thing travelling along (<paramref name="ux"/>, <paramref name="uy"/>) came from: "the upper right", "above", ...</summary>
    public static string Source(double ux, double uy) => Sources[Sector(-ux, -uy)];

    private static int Sector(double dx, double dy)
    {
        // atan2 in screen space: 0 to the right, +90 straight down.
        var degrees = Math.Atan2(dy, dx) * 180 / Math.PI;
        return (int)Math.Floor((degrees + 22.5 + 360) / 45) % 8;
    }

    /// <summary>
    /// The sidestep for a bolt travelling along (<paramref name="ux"/>,
    /// <paramref name="uy"/>): a unit vector across its line. Either
    /// perpendicular clears the line by the same margin, so the choice
    /// between them is not geometry, and with nothing else known a coach
    /// picks the side toward safety. Blue is always the local player's team,
    /// its base is the bottom-left of the map, and the camera never rotates,
    /// so toward safety is down-left on the screen; the side with the larger
    /// component that way wins. A bolt travelling exactly along that
    /// diagonal is a tie, and the side pointing down the screen takes it, so
    /// the answer is a rule and not a rounding.
    /// </summary>
    public static (double Dx, double Dy) Sidestep(double ux, double uy)
    {
        var length = double.Hypot(ux, uy);
        ux /= length;
        uy /= length;
        (double Dx, double Dy) a = (-uy, ux), b = (uy, -ux);
        const double toBaseX = -0.70710678118654752, toBaseY = 0.70710678118654752;
        var scoreA = a.Dx * toBaseX + a.Dy * toBaseY;
        var scoreB = b.Dx * toBaseX + b.Dy * toBaseY;
        if (Math.Abs(scoreA - scoreB) < 1e-9)
            return a.Dy >= b.Dy ? a : b;
        return scoreA > scoreB ? a : b;
    }
}

/// <summary>
/// The movement half of the demonstration: a bolt that hit the player while
/// they stood still, and the step a coach would have taken instead. It says
/// which way -- as a <see cref="MoveStep"/>, which the reactor logs, streams,
/// traces and records as a right-click on the ground -- and what came at them
/// from where.
///
/// <para><b>What it knows.</b> Only <c>threat</c> events, which are the
/// player's own screen: a bolt that came at their model, its heading in
/// screen pixels, when it was first seen and when it arrived, whether their
/// printed health fell, and how far they moved across its line meanwhile.
/// No frame is consulted and no enemy position is used; the step is relative
/// to the bolt, and the camera is locked so the player is always at the same
/// place on their screen.</para>
///
/// <para><b>When it steps.</b> On a hit while standing still, and only then:
/// that is the one moment where what the coach would have done differs from
/// what the player did. A dodge needs no advice, an unknown outcome is
/// nothing known, and a hit while moving is silence for the reason
/// <see cref="ExecutionPolicy"/> gives -- a bolt gives about 0.3s of warning,
/// so being in motion is all a player can bring, and they brought it. Two
/// bolts credited with one fall of the health bar are one moment and one
/// step. There is no gate on the warning time: the step is a response to
/// the enemy's cast, which lands before the bolt's first sighting, and the
/// event's <c>at</c> is the latest moment it could have been taken, so the
/// step is stamped there and says how much warning there was. On the
/// execution fixture that is 14 steps in seventeen minutes, one per landing
/// that found the player still.</para>
///
/// <para><b>Which way.</b> Across the bolt's line, on the side toward the
/// player's own base -- see <see cref="ScreenDirections.Sidestep"/> for why
/// that and not the other. The direction is named in eight screen
/// directions ("left", "up-right"), and the bolt's origin the same way
/// ("from the upper right"), because the player's screen is the frame they
/// act in.</para>
///
/// <para><b>What the copy must not claim.</b> That the bolt was dodgeable. A
/// threat is any bolt launched at an enemy champion's plate and a ranged
/// auto-attack qualifies as readily as a skillshot, and until spectral-sight
/// names the ability nothing here can tell them apart. So it is "a bolt", and
/// the step is what a good player does when one is coming regardless: a
/// sidestep costs nothing against an auto and everything against a Q. Nor
/// that a human reacts to a first sighting: the warning is stated, not
/// judged.</para>
///
/// <para>It produces steps only, never a cursor: the step is an action, not
/// somewhere to look, so it does not contest attention's cursor (see
/// <see cref="CompositePolicy"/>); the recording writes it as a click and
/// the cursor is wherever the click left it. The last landing is the only
/// state, and it resets on <see cref="Resync"/>.</para>
/// </summary>
public sealed class DodgePolicy(DodgeOptions? options = null) : IPolicy
{
    private readonly DodgeOptions _options = options ?? new DodgeOptions();
    private readonly List<CoachCue> _cues = [];
    private readonly List<MoveStep> _moves = [];
    private double? _lastLanding;

    public void Configure(Meta meta)
    {
        // A false flag means the stage did not run, not that nothing came at
        // the player. Say so once rather than be a silent policy that looks broken.
        if (!meta.HasThreats)
            _cues.Add(new CoachCue(0, 1,
                "movement coaching is off: this feed carries no threat stage "
                + "(spectral-sight needs a --coach run)"));
    }

    public IReadOnlyList<GlanceNote> DrainNotes() => [];

    public IReadOnlyList<CoachCue> DrainCues() => Drain(_cues);

    public IReadOnlyList<MoveStep> DrainMoves() => Drain(_moves);

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
        _moves.Clear();
        _lastLanding = null;
    }

    public GhostCursor? OnFrame(FrameEnvelope frame) => null;

    public GhostCursor? OnEvent(GameEvent evt)
    {
        if (evt.Kind != EventKind.Threat || evt.Outcome != "hit")
            return null;
        var landing = evt.Arrival ?? evt.VideoTime;
        if (_lastLanding is { } previous && Math.Abs(landing - previous) <= _options.SameFallSeconds)
            return null;   // the same fall of the health bar, credited to a second bolt
        _lastLanding = landing;

        // No motion measurement means nothing known about whether they were
        // already stepping; a step on top of a step is not coaching.
        if (evt.MovedAcross is not { } across || across >= _options.StillPx)
            return null;
        if (evt.Heading is not [var ux, var uy] || double.Hypot(ux, uy) == 0)
            return null;

        var (dx, dy) = ScreenDirections.Sidestep(ux, uy);
        var damage = evt.Damage is { } d ? $" for {d}" : "";
        var warning = evt.At is { } at && evt.Arrival is { } arrival
            ? $", {arrival - at:0.00}s after it came into view"
            : "";
        _moves.Add(new MoveStep(evt.At ?? evt.VideoTime, ScreenDirections.Name(dx, dy), dx, dy, 3,
            $"a bolt from {ScreenDirections.Source(ux, uy)} hit you{damage} while you stood still{warning}"));
        return null;
    }
}
