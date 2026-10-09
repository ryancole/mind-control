using MindControl.Feed;
using MindControl.Policy;
using Misdirection.Client;

namespace MindControl;

/// <summary>
/// Records the ghost's input -- what the coach would have done with the mouse
/// and keyboard: key presses, steps and attacks -- as a misdirection protocol
/// file (<c>.msdr</c>), one frame per message, in the order the coaching
/// produced them. The file opens with a
/// <see cref="ScreenSizeMessage"/>, as a device session would, so the mouse
/// coordinates that follow are anchored to the screen they were meant for.
///
/// <para>This is a recording, not a connection. Nothing here opens a port or
/// touches a device; the file is the demonstration, kept in the wire format so
/// it needs no translation later.</para>
///
/// <para>Timing goes in the file too, as the format's <c>FILE_DELAY</c>
/// records (<see cref="DelayMessage"/>): before each press or step, the
/// video time that passed since the previous one, so the file replays at the
/// pace the coach acted. The clock is the VOD's, not the wall's -- a replay at
/// speed 4 records the same gaps as one at speed 1 -- and it starts at the
/// first press or step of a run, since nothing before it is anchored to the
/// video. A step is stamped at the bolt's first sighting, which can be
/// earlier than the press written before it; the file is one sequence and a
/// gap cannot be negative, so such a step follows at no gap and the clock
/// does not move back. The ghost trace (<see cref="GhostTrace"/>) keeps the
/// absolute video time of every press and step, out-of-order stamps included.
/// </para>
///
/// <para>Each of <see cref="Press"/> and <see cref="Step"/>
/// hands back the frames it wrote, the delay before them included, so the
/// coaching output can show not just "pressed Q" but the KeyDown and KeyUp
/// that went into the file for it:
/// <see cref="Show(IEnumerable{Message})"/> as plain text for the console,
/// <see cref="AsData(IEnumerable{Message})"/> as data for the stream and the
/// trace. Neither decorates; an icon for the hand is a front end's choice.</para>
/// </summary>
public sealed class GhostRecording : IDisposable
{
    /// <summary>
    /// How far from the player's model a step's click lands, in game units.
    /// A champion's model is about 130 units across and a bolt 120–200 more,
    /// so 200 clears a bolt's line with a margin while staying a sidestep
    /// and not a retreat. Was a fixed 200 screen pixels, which the game read
    /// as roughly 490 units at the receiver's window -- past a sidestep,
    /// often into the wall behind the champion.
    /// </summary>
    public const double StepUnits = 200;

    /// <summary>
    /// How far from the player's model a walk's click lands (a step toward a
    /// lane, a wave, the brush), in game units. The coach walks in clicks
    /// asked a second apart, never with one order for the whole trip, so a
    /// click must hold more than a second of walking or the model stops
    /// short between them: past the 325–390 a champion walks in a second. A
    /// place nearer than that is clicked on itself, so the last leg does
    /// not overshoot it.
    /// </summary>
    public const double WalkUnits = 400;

    /// <summary>
    /// How far a walk's click may reach past <see cref="WalkUnits"/> to land
    /// beyond ground no one can walk on, in game units: 800, past the blue
    /// nexus's footprint from the fountain side (about 750 along the way to
    /// mid), still on the screen.
    /// </summary>
    public const double MaxWalkUnits = 800;

    /// <summary>
    /// Where the game's view of the ground ends, as a fraction of the screen's
    /// height from the top: below it the HUD's ability bar takes the clicks.
    /// The bottom HUD's top edge, measured off the receiver's frame;
    /// spectral-sight's world-view box ends on the same line.
    /// </summary>
    public const double GroundBottom = 0.775;

    /// <summary>
    /// Screen pixels per game unit on the ground around the player's model,
    /// at a screen 1080 pixels tall, for a run that opened without the feed's
    /// layout (<see cref="FeedLayout"/>): the camera's measured scale, from
    /// the viewport rectangle the game draws on the minimap. The layout
    /// derives the same number for its own feed from the meta; the fallback
    /// is for a recording with no feed behind it, and is exact only for a
    /// window of the receiver's shape.
    /// </summary>
    public const double FallbackPxPerUnitAt1080 = 0.329;

    /// <summary>
    /// The attack-move key on the game's default bindings: pressed, then a
    /// left-click on the ground, it orders a walk that stops to attack an
    /// enemy in range on the way.
    /// </summary>
    public const string AttackMoveKey = "A";

    /// <summary>
    /// Where the feed's pixels sit, read from its meta: the game's own area
    /// of the frame, which this recording's screen is, the world view's
    /// origin, which a seen target's <see cref="MoveStep.ViewPx"/> is
    /// relative to, and the ground's scale, which a distance in game units
    /// is converted by. Boxes in frame pixels.
    /// </summary>
    public sealed record FeedLayout(PixelBox Game, int ViewX, int ViewY, double PxPerUnitX, double PxPerUnitY)
    {
        // The camera's viewport as a fraction of the map, from the rectangle
        // the game draws on the minimap: 78x48 crop pixels on the reference
        // crop whose map square spans 289px (spectral-sight's calibration,
        // measured over 1,135 frames). The zoom is fixed, so the fraction is
        // the game's, not the window's; the feed's minion projections agree
        // with it within a few percent.
        private const double ViewportFractionX = 78.0 / 289.0;
        private const double ViewportFractionY = 48.0 / 289.0;

        /// <summary>
        /// The layout <paramref name="meta"/> describes. A feed that does not
        /// name the boxes and the world's extent is one this reactor cannot
        /// place a click on, and says so rather than guess.
        /// </summary>
        public static FeedLayout Of(Meta meta)
        {
            if (meta.GameArea is not { Width: > 0, Height: > 0 } game ||
                meta.WorldView is not { } view ||
                meta.WorldBounds is not { } bounds)
                throw new InvalidOperationException(
                    "the feed's meta names no game_area, world_view and world_bounds; a spectral-sight that publishes them is needed");
            // How much map the camera shows, then how many game-area pixels a
            // unit takes: the world view's pixels over the units it spans,
            // per axis.
            var unitsX = (bounds.MaxX - bounds.MinX) * ViewportFractionX;
            var unitsY = (bounds.MaxY - bounds.MinY) * ViewportFractionY;
            return new FeedLayout(game, view.X, view.Y, view.Width / unitsX, view.Height / unitsY);
        }

        /// <summary>
        /// Screen pixels per game unit on the ground at a screen
        /// <paramref name="screenWidth"/> by <paramref name="screenHeight"/>.
        /// The two axes differ by a percent or two of camera tilt; a click is
        /// a length, and the average is the least wrong single number.
        /// </summary>
        public double PxPerUnit(int screenWidth, int screenHeight) =>
            (PxPerUnitX * screenWidth / Game.Width + PxPerUnitY * screenHeight / Game.Height) / 2;
    }

    private readonly ProtocolFileWriter _writer;
    private readonly ushort _width, _height;
    private readonly (ushort X, ushort Y) _anchor;
    // Where the feed's pixels sit; null when not known, and a seen target is
    // then clicked where its distance projects.
    private readonly FeedLayout? _layout;
    // Screen pixels per game unit on the ground: the layout's measured scale,
    // or the fallback when the run opened without a feed behind it.
    private readonly double _pxPerUnit;
    // Video time of the last press or step, or null before a run's first:
    // the reference the next delay is measured from.
    private double? _lastVideoTime;

    private GhostRecording(
        ProtocolFileWriter writer, ushort width, ushort height, (ushort X, ushort Y) anchor, FeedLayout? layout)
    {
        _writer = writer;
        _width = width;
        _height = height;
        _anchor = anchor;
        _layout = layout;
        _pxPerUnit = layout is { } l ? l.PxPerUnit(width, height) : FallbackPxPerUnitAt1080 * height / 1080.0;
        Header = Show(new ScreenSizeMessage(width, height));
    }

    /// <summary>The <see cref="ScreenSizeMessage"/> this run opened with, as <see cref="Show(IEnumerable{Message})"/> prints it.</summary>
    public string Header { get; }

    /// <summary>
    /// Opens <paramref name="path"/> for appending, creating it (and its
    /// directory) if needed, and writes the screen size the coordinates that
    /// follow are in. Appending to a file from an earlier run is fine: each run
    /// restates its screen size, so a reader always knows which one applies.
    /// <paramref name="playerAnchor"/> is where the player's model sits on
    /// their screen, which a step is taken from; the camera is locked, so it
    /// is one place, and the default is the screen's centre.
    /// <paramref name="layout"/> is where the feed's pixels sit, which a seen
    /// target's world-view pixels are moved and scaled from onto this screen,
    /// the game's own area.
    /// </summary>
    public static GhostRecording Append(
        string path, ushort screenWidth, ushort screenHeight, (ushort X, ushort Y)? playerAnchor = null,
        FeedLayout? layout = null)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);
        var writer = ProtocolFileWriter.Append(path);
        try
        {
            writer.Write(new ScreenSizeMessage(screenWidth, screenHeight));
            writer.Flush();
        }
        catch
        {
            writer.Dispose();
            throw;
        }
        var anchor = playerAnchor ?? ((ushort)(screenWidth / 2), (ushort)(screenHeight / 2));
        return new GhostRecording(writer, screenWidth, screenHeight, anchor, layout);
    }

    /// <summary>Frames written through this recording, the screen size and any delays included.</summary>
    public long FramesWritten => _writer.FramesWritten;

    /// <summary>
    /// Video time of the last press or step, from which the next delay is
    /// measured; null before a run's first. Never moves backwards.
    /// </summary>
    public double? LastVideoTime => _lastVideoTime;

    /// <summary>
    /// The coach pressed a key: a down and an up, back to back, after the gap
    /// since the last press or step. A tap is the only press there is; a
    /// held key would need a policy to say how long, and none holds one.
    /// With <see cref="KeyPress.WithControl"/> the tap sits inside a Ctrl
    /// down and up -- the chord the game reads as a point into the ability
    /// rather than a cast of it. Ctrl goes on the wire as its own usage; the
    /// firmware folds it into the modifier byte.
    /// </summary>
    public IReadOnlyList<Message> Press(KeyPress key)
    {
        var usage = UsageOf(key.Key);
        return key.WithControl
            ? WriteAt(key.VideoTime,
                new KeyDownMessage(HidUsage.LeftControl), new KeyDownMessage(usage),
                new KeyUpMessage(usage), new KeyUpMessage(HidUsage.LeftControl))
            : WriteAt(key.VideoTime, new KeyDownMessage(usage), new KeyUpMessage(usage));
    }

    /// <summary>
    /// The coach stepped: a move to the ground <see cref="StepUnits"/> from
    /// the player's model in the step's direction, then a right-click
    /// there -- a press and a release, as with a key. The click is a move
    /// order, which is how a step is taken in the game; the cursor is left
    /// where it was clicked, as a player's would be, until the next step
    /// moves it. A step toward somewhere farther (one with a
    /// <see cref="MoveStep.Destination"/>) is a longer click aimed that way
    /// (<see cref="WalkUnits"/>), or on the place itself once it is nearer
    /// than that: the game window, never the minimap, one leg of the trip a
    /// second, and never on ground no one can walk on
    /// (<see cref="ClearedWalkUnits"/>). An attack (a step with a <see cref="MoveStep.Target"/>)
    /// is the same click on the target itself, as far from the player's
    /// model as the target stands: a right-click on an enemy is the order to
    /// attack it. An <see cref="MoveStep.AttackMove"/> lands on the same
    /// spot but is ordered with <see cref="AttackMoveKey"/> and a left-click
    /// instead of the right-click. A target seen on the screen
    /// (<see cref="MoveStep.ViewPx"/>) is clicked where it was seen, once
    /// the feed's layout is known.
    /// </summary>
    public IReadOnlyList<Message> Step(MoveStep step)
    {
        var (x, y) = step switch
        {
            { ViewPx: { } seen } when _layout is { } layout => Seen(seen, layout),
            { Target: { } target } => OnTheGround(step, target.DistanceUnits * _pxPerUnit),
            { Destination: not null } => OnTheGround(step, ClearedWalkUnits(step) * _pxPerUnit),
            _ => OnTheGround(step, StepUnits * _pxPerUnit),
        };
        if (step.AttackMove)
        {
            var key = UsageOf(AttackMoveKey);
            return WriteAt(step.VideoTime,
                new MouseMoveMessage(x, y),
                new KeyDownMessage(key), new KeyUpMessage(key),
                new MouseButtonsMessage(MouseButtons.Left),
                new MouseButtonsMessage(MouseButtons.None));
        }
        return WriteAt(step.VideoTime,
            new MouseMoveMessage(x, y),
            new MouseButtonsMessage(MouseButtons.Right),
            new MouseButtonsMessage(MouseButtons.None));
    }

    /// <summary>
    /// How far from the player's model a walk's click lands, in game units:
    /// <see cref="WalkUnits"/>'s worth, or the place itself when it is
    /// nearer, moved off ground no one can walk on when the step says where
    /// the model stood (<see cref="RiftWalls.ClearOfWalls"/>) -- farther, up
    /// to <see cref="MaxWalkUnits"/>'s worth and never off the game's view
    /// of the ground, so the game paths round what is in the way. A click on
    /// the place itself looks nearer first.
    /// </summary>
    private double ClearedWalkUnits(MoveStep step)
    {
        var units = Math.Min(WalkUnits, step.DistanceUnits ?? double.PositiveInfinity);
        if (step.From is not { } from)
            return units;
        var max = Math.Min(MaxWalkUnits, OnTheGroundPx(step) / _pxPerUnit);
        // Screen y grows down, world y north: flip back for the map.
        return RiftWalls.ClearOfWalls(from.X, from.Y, step.Dx, -step.Dy, units, max,
            nearerFirst: units == step.DistanceUnits);
    }

    /// <summary>
    /// How far from the player's model a click can go in the step's direction
    /// and still land on the game's view of the ground: inside the screen and
    /// above the HUD along its foot (<see cref="GroundBottom"/>).
    /// </summary>
    private double OnTheGroundPx(MoveStep step)
    {
        var px = double.PositiveInfinity;
        if (step.Dx > 0) px = Math.Min(px, (_width - 1 - _anchor.X) / step.Dx);
        if (step.Dx < 0) px = Math.Min(px, _anchor.X / -step.Dx);
        if (step.Dy > 0) px = Math.Min(px, (GroundBottom * _height - _anchor.Y) / step.Dy);
        if (step.Dy < 0) px = Math.Min(px, _anchor.Y / -step.Dy);
        return Math.Max(0, px);
    }

    /// <summary>
    /// A world-view pixel as a pixel of this screen: moved to the frame's own
    /// by the world view's origin, then to the game's by its area's, then
    /// scaled from the game's size to the screen's. The feed's spaces share
    /// a scale, so only the last step stretches.
    /// </summary>
    private (ushort X, ushort Y) Seen((double X, double Y) view, FeedLayout layout) => (
        (ushort)Math.Clamp(Math.Round((view.X + layout.ViewX - layout.Game.X) * _width / layout.Game.Width), 0, _width - 1),
        (ushort)Math.Clamp(Math.Round((view.Y + layout.ViewY - layout.Game.Y) * _height / layout.Game.Height), 0, _height - 1));

    private (ushort X, ushort Y) OnTheGround(MoveStep step, double px) => (
        (ushort)Math.Clamp(Math.Round(_anchor.X + step.Dx * px), 0, _width - 1),
        (ushort)Math.Clamp(Math.Round(_anchor.Y + step.Dy * px), 0, _height - 1));

    /// <summary>
    /// The gap the file records before an input at <paramref name="videoTime"/>:
    /// the video time since the last press or step; none before a run's
    /// first, and none for a stamp earlier than the last (the clock does not
    /// move back). What <see cref="WriteAt"/> writes ahead of the frames.
    /// </summary>
    public TimeSpan DelayBefore(double videoTime) =>
        _lastVideoTime is { } last && videoTime > last ? TimeSpan.FromSeconds(videoTime - last) : TimeSpan.Zero;

    /// <summary>
    /// The keycap the coach names, as the HID usage the wire carries. Letters
    /// and digits, which is every ability and summoner slot on the default
    /// bindings; anything else is a policy naming a key this recording was
    /// not taught, and that is a bug to hear about, not a frame to guess at.
    /// </summary>
    public static byte UsageOf(string key) => key switch
    {
        [>= 'A' and <= 'Z' and var letter] => (byte)(HidUsage.A + (letter - 'A')),
        [>= 'a' and <= 'z' and var letter] => (byte)(HidUsage.A + (letter - 'a')),
        ['0'] => HidUsage.Digit0,
        [>= '1' and <= '9' and var digit] => (byte)(HidUsage.Digit1 + (digit - '1')),
        _ => throw new ArgumentException($"no HID usage known for key \"{key}\"", nameof(key)),
    };

    /// <summary>
    /// The keycap a wire usage stands for, the inverse of <see cref="UsageOf"/>,
    /// plus "Ctrl" for the modifier a chord holds; "?" for a usage this
    /// recording never writes.
    /// </summary>
    public static string KeycapOf(byte usage) => usage switch
    {
        >= HidUsage.A and <= HidUsage.Z => ((char)('A' + (usage - HidUsage.A))).ToString(),
        HidUsage.Digit0 => "0",
        >= HidUsage.Digit1 and <= HidUsage.Digit9 => ((char)('1' + (usage - HidUsage.Digit1))).ToString(),
        HidUsage.LeftControl => "Ctrl",
        _ => "?",
    };

    /// <summary>
    /// Frames as the console prints them: plain text, comma separated, one
    /// entry per frame. A key carries its keycap and the usage on the wire;
    /// a move its screen pixels; a button change the mask it set. Anything
    /// this recording does not write is shown as the library shows a frame,
    /// type and hex. No decoration: the text is the data, and a front end
    /// that wants an icon for the hand picks it from <see cref="AsData"/>.
    /// </summary>
    public static string Show(IEnumerable<Message> frames) => string.Join(", ", frames.Select(Describe));

    public static string Show(params Message[] frames) => Show((IEnumerable<Message>)frames);

    /// <summary>
    /// Frames as the stream and the trace carry them: one object per frame
    /// with a <c>type</c> naming the message (<c>key_down</c>,
    /// <c>mouse_move</c>, ...) and its fields by name, so a front end can
    /// decorate by type without parsing text.
    /// </summary>
    public static IReadOnlyList<object> AsData(IEnumerable<Message> frames) => frames.Select(AsData).ToList();

    public static object AsData(Message frame) => frame switch
    {
        KeyDownMessage k => new { Type = "key_down", Key = KeycapOf(k.Usage), k.Usage },
        KeyUpMessage k => new { Type = "key_up", Key = KeycapOf(k.Usage), k.Usage },
        MouseMoveMessage m => new { Type = "mouse_move", m.X, m.Y },
        MouseButtonsMessage b => new { Type = "mouse_buttons", Buttons = b.Buttons.ToString() },
        MouseWheelMessage w => new { Type = "mouse_wheel", w.Vertical, w.Horizontal },
        ScreenSizeMessage s => new { Type = "screen_size", s.Width, s.Height },
        DelayMessage d => new { Type = "delay", d.Microseconds },
        _ => new { Type = frame.Type.ToString().ToLowerInvariant(), Payload = Convert.ToHexString(frame.ToFrame().Payload) },
    };

    private static string Describe(Message frame) => frame switch
    {
        KeyDownMessage k => $"KeyDown {KeycapOf(k.Usage)} (0x{k.Usage:X2})",
        KeyUpMessage k => $"KeyUp {KeycapOf(k.Usage)} (0x{k.Usage:X2})",
        MouseMoveMessage m => $"MouseMove {m.X},{m.Y}",
        MouseButtonsMessage b => $"MouseButtons {b.Buttons}",
        MouseWheelMessage w => $"MouseWheel {w.Vertical},{w.Horizontal}",
        ScreenSizeMessage s => $"ScreenSize {s.Width}x{s.Height}",
        // Seconds to the millisecond: gaps between inputs are tenths to tens
        // of seconds, and a microsecond is below anything a hand can tell apart.
        DelayMessage d => $"Delay {d.Duration.TotalSeconds:0.000}s",
        _ => frame.ToFrame().ToString(),
    };

    /// <summary>
    /// Input the coach made at <paramref name="videoTime"/>: the gap since the
    /// last press or step as a <see cref="DelayMessage"/> when there is one
    /// (see <see cref="DelayBefore"/>), then the frames, in order. Returns
    /// everything written, delay first, so the caller can show what went into
    /// the file. <see cref="Press"/> and <see cref="Step"/> are the callers.
    /// </summary>
    public IReadOnlyList<Message> WriteAt(double videoTime, params Message[] messages)
    {
        var delay = DelayBefore(videoTime);
        var written = new List<Message>(messages.Length + 1);
        if (delay > TimeSpan.Zero)
        {
            // One record in practice: the writer splits a gap only past 71
            // minutes, and no two coached moments are that far apart. Read
            // back what it split rather than guess, so the frames handed
            // back are the frames in the file.
            var before = _writer.FramesWritten;
            _writer.WriteDelay(delay);
            written.AddRange(DelaysWritten(delay, _writer.FramesWritten - before));
        }
        written.AddRange(Write(messages));
        if (_lastVideoTime is null || videoTime > _lastVideoTime)
            _lastVideoTime = videoTime;
        return written;
    }

    /// <summary>
    /// The <see cref="DelayMessage"/> records <see cref="ProtocolFileWriter.WriteDelay"/>
    /// wrote for <paramref name="delay"/>: <paramref name="records"/> of them,
    /// the maximum each but the last, rounded to the microsecond as it rounds.
    /// </summary>
    private static IEnumerable<Message> DelaysWritten(TimeSpan delay, long records)
    {
        var micros = Math.DivRem(delay.Ticks, TimeSpan.TicksPerMicrosecond, out var remainder);
        if (remainder * 2 >= TimeSpan.TicksPerMicrosecond) micros++;
        for (var i = 0; i < records; i++)
        {
            var chunk = (uint)Math.Min(micros, uint.MaxValue);
            yield return new DelayMessage(chunk);
            micros -= chunk;
        }
    }

    /// <summary>
    /// Frames as given, in order, with no delay before them; returns them so
    /// the caller can show what went into the file. For input with no moment
    /// of its own, and for tests; the coach's presses and steps go through
    /// <see cref="WriteAt"/>.
    /// </summary>
    public IReadOnlyList<Message> Write(params Message[] messages)
    {
        foreach (var message in messages)
            _writer.Write(message);
        // Flushed per call, like the trace and the log: a run ends with
        // Ctrl-C, and the last thing the ghost did should be on disk by then.
        _writer.Flush();
        return messages;
    }

    public void Dispose() => _writer.Dispose();
}
