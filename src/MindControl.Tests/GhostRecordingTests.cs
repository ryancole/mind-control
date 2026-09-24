using MindControl.Policy;
using Misdirection.Client;

namespace MindControl.Tests;

/// <summary>
/// The recording is a misdirection protocol file, so the property that
/// matters is that the library reads back exactly what the ghost did, in
/// order, behind the screen size those coordinates were meant for.
/// </summary>
[TestClass]
public sealed class GhostRecordingTests
{
    private string _path = "";

    [TestInitialize]
    public void CreatePath() =>
        _path = Path.Combine(Path.GetTempPath(), $"mind-control-{Guid.NewGuid():N}", "ghost.msdr");

    [TestCleanup]
    public void DeletePath()
    {
        if (Path.GetDirectoryName(_path) is { } dir && Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    [TestMethod]
    public void Moves_read_back_as_mouse_moves_behind_the_screen_size()
    {
        using (var recording = GhostRecording.Append(_path, 1920, 1080))
        {
            recording.Move(new GhostCursor(1700, 900));
            recording.Move(new GhostCursor(1650, 850));
            Assert.AreEqual(3, recording.FramesWritten);
        }

        var messages = ProtocolFile.Read(_path);
        CollectionAssert.AreEqual(
            new Message[]
            {
                new ScreenSizeMessage(1920, 1080),
                new MouseMoveMessage(1700, 900),
                new MouseMoveMessage(1650, 850),
            },
            messages.ToArray());
    }

    [TestMethod]
    public void A_second_run_appends_after_the_first_and_restates_its_screen()
    {
        using (var first = GhostRecording.Append(_path, 1920, 1080))
            first.Move(new GhostCursor(1, 2));
        using (var second = GhostRecording.Append(_path, 2560, 1440))
            second.Move(new GhostCursor(3, 4));

        var messages = ProtocolFile.Read(_path);
        CollectionAssert.AreEqual(
            new Message[]
            {
                new ScreenSizeMessage(1920, 1080),
                new MouseMoveMessage(1, 2),
                new ScreenSizeMessage(2560, 1440),
                new MouseMoveMessage(3, 4),
            },
            messages.ToArray());
    }

    [TestMethod]
    public void Any_message_can_be_recorded_so_keys_have_somewhere_to_go()
    {
        using (var recording = GhostRecording.Append(_path, 1920, 1080))
        {
            recording.Write(new KeyDownMessage(HidUsage.F));
            recording.Write(new KeyUpMessage(HidUsage.F));
        }

        var messages = ProtocolFile.Read(_path);
        Assert.HasCount(3, messages);
        Assert.AreEqual(new KeyDownMessage(HidUsage.F), messages[1]);
        Assert.AreEqual(new KeyUpMessage(HidUsage.F), messages[2]);
    }

    [TestMethod]
    public void Every_frame_is_on_disk_before_the_recording_is_disposed()
    {
        using var recording = GhostRecording.Append(_path, 1920, 1080);
        recording.Move(new GhostCursor(5, 6));

        // Read through a separate handle while the writer is still open: a
        // run ends with Ctrl-C, and the last move must not be sitting in a buffer.
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var messages = ProtocolFile.Read(stream);
        Assert.HasCount(2, messages);
        Assert.AreEqual(new MouseMoveMessage(5, 6), messages[1]);
    }

    [TestMethod]
    public void A_press_reads_back_as_a_key_down_and_up_on_the_keycap_usage()
    {
        using (var recording = GhostRecording.Append(_path, 1920, 1080))
        {
            recording.Move(new GhostCursor(1700, 900));
            recording.Press(new KeyPress(17.5, "Q", 2, "Karma in range"));
            Assert.AreEqual(4, recording.FramesWritten);
        }

        CollectionAssert.AreEqual(
            new Message[]
            {
                new ScreenSizeMessage(1920, 1080),
                new MouseMoveMessage(1700, 900),
                new KeyDownMessage(HidUsage.Q),
                new KeyUpMessage(HidUsage.Q),
            },
            ProtocolFile.Read(_path).ToArray());
    }

    [TestMethod]
    public void A_step_reads_back_as_a_right_click_a_step_from_the_players_model()
    {
        using (var recording = GhostRecording.Append(_path, 1920, 1080))
        {
            recording.Step(new MoveStep(218.1, "left", -1, 0, 3, "a bolt from the right"));
            Assert.AreEqual(4, recording.FramesWritten);
        }

        // The model is at the screen's centre by default; the click lands
        // StepPx to the left of it, and the button goes down and back up.
        CollectionAssert.AreEqual(
            new Message[]
            {
                new ScreenSizeMessage(1920, 1080),
                new MouseMoveMessage((ushort)(960 - GhostRecording.StepPx), 540),
                new MouseButtonsMessage(MouseButtons.Right),
                new MouseButtonsMessage(MouseButtons.None),
            },
            ProtocolFile.Read(_path).ToArray());
    }

    [TestMethod]
    public void A_step_is_taken_from_the_anchor_given_and_stays_on_the_screen()
    {
        using (var recording = GhostRecording.Append(_path, 1920, 1080, playerAnchor: (100, 540)))
            recording.Step(new MoveStep(1, "up-left", -0.70710678, -0.70710678, 3, "diagonal"));

        // 100 - 141 is off the left edge, so the click is clamped to it; the
        // y lands 141 above the anchor, rounded.
        var messages = ProtocolFile.Read(_path);
        Assert.AreEqual(new MouseMoveMessage(0, 399), messages[1]);
    }

    [TestMethod]
    public void What_was_written_is_handed_back_and_shown_plainly()
    {
        IReadOnlyList<Message> moved, pressed, stepped;
        using (var recording = GhostRecording.Append(_path, 1920, 1080))
        {
            Assert.AreEqual("ScreenSize 1920x1080", recording.Header);

            moved = recording.Move(new GhostCursor(1700, 900));
            CollectionAssert.AreEqual(new Message[] { new MouseMoveMessage(1700, 900) }, moved.ToArray());
            Assert.AreEqual("MouseMove 1700,900", GhostRecording.Show(moved));

            pressed = recording.Press(new KeyPress(17.5, "Q", 2, "Karma in range"));
            CollectionAssert.AreEqual(
                new Message[] { new KeyDownMessage(HidUsage.Q), new KeyUpMessage(HidUsage.Q) }, pressed.ToArray());
            Assert.AreEqual("KeyDown Q (0x14), KeyUp Q (0x14)", GhostRecording.Show(pressed));

            stepped = recording.Step(new MoveStep(218.1, "left", -1, 0, 3, "a bolt from the right"));
            Assert.AreEqual(
                "MouseMove 760,540, MouseButtons Right, MouseButtons None", GhostRecording.Show(stepped));
        }

        // The file holds exactly what was handed back, in order.
        CollectionAssert.AreEqual(
            new Message[] { new ScreenSizeMessage(1920, 1080) }.Concat(moved).Concat(pressed).Concat(stepped).ToArray(),
            ProtocolFile.Read(_path).ToArray());
    }

    [TestMethod]
    public void Showing_frames_is_plain_text_with_nothing_added()
    {
        Assert.AreEqual(
            "MouseMove 1,2, KeyDown D (0x07), KeyUp D (0x07), MouseButtons Right",
            GhostRecording.Show(
                new MouseMoveMessage(1, 2),
                new KeyDownMessage(HidUsage.D), new KeyUpMessage(HidUsage.D),
                new MouseButtonsMessage(MouseButtons.Right)));
        Assert.AreEqual("", GhostRecording.Show());
        // A frame this recording never writes still shows, as the library shows it.
        Assert.AreEqual("Ping []", GhostRecording.Show(new PingMessage()));
    }

    [TestMethod]
    public void Frames_as_data_name_their_type_and_fields_for_a_front_end()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            GhostRecording.AsData(
            [
                new KeyDownMessage(HidUsage.Q), new KeyUpMessage(HidUsage.Q),
                new MouseMoveMessage(760, 540), new MouseButtonsMessage(MouseButtons.Right),
                new ScreenSizeMessage(1920, 1080), new PongMessage(3),
            ]),
            MindControl.Feed.FeedJson.Options);
        Assert.AreEqual(
            """[{"type":"key_down","key":"Q","usage":20},{"type":"key_up","key":"Q","usage":20},""" +
            """{"type":"mouse_move","x":760,"y":540},{"type":"mouse_buttons","buttons":"Right"},""" +
            """{"type":"screen_size","width":1920,"height":1080},{"type":"pong","payload":"03"}]""",
            json);
    }

    [TestMethod]
    public void Keycaps_map_to_usages_and_back()
    {
        foreach (var keycap in "ABCQZ0159")
            Assert.AreEqual(keycap.ToString(), GhostRecording.KeycapOf(GhostRecording.UsageOf(keycap.ToString())));
        Assert.AreEqual("?", GhostRecording.KeycapOf(HidUsage.Space));
    }

    [TestMethod]
    public void Keycaps_map_to_usages_and_unknown_ones_are_refused()
    {
        Assert.AreEqual(HidUsage.Q, GhostRecording.UsageOf("Q"));
        Assert.AreEqual(HidUsage.D, GhostRecording.UsageOf("d"));
        Assert.AreEqual(HidUsage.Digit1, GhostRecording.UsageOf("1"));
        Assert.AreEqual(HidUsage.Digit0, GhostRecording.UsageOf("0"));
        Assert.ThrowsExactly<ArgumentException>(() => GhostRecording.UsageOf("Space"));
    }
}
