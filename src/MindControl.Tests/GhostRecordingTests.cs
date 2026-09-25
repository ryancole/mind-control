using MindControl.Policy;
using Misdirection.Client;

namespace MindControl.Tests;

/// <summary>
/// The recording is a misdirection protocol file, so the property that
/// matters is that the library reads back exactly what the ghost did, in
/// order and at the pace it did it, behind the screen size those
/// coordinates were meant for.
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
    public void Frames_read_back_in_order_behind_the_screen_size()
    {
        using (var recording = GhostRecording.Append(_path, 1920, 1080))
        {
            recording.Write(new MouseMoveMessage(1700, 900));
            recording.Write(new MouseMoveMessage(1650, 850));
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
            first.Write(new MouseMoveMessage(1, 2));
        using (var second = GhostRecording.Append(_path, 2560, 1440))
            second.Write(new MouseMoveMessage(3, 4));

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
        recording.Write(new MouseMoveMessage(5, 6));

        // Read through a separate handle while the writer is still open: a
        // run ends with Ctrl-C, and the last move must not be sitting in a buffer.
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var messages = ProtocolFile.Read(stream);
        Assert.HasCount(2, messages);
        Assert.AreEqual(new MouseMoveMessage(5, 6), messages[1]);
    }

    [TestMethod]
    public void The_file_reads_by_path_while_a_run_is_still_recording_it()
    {
        using var recording = GhostRecording.Append(_path, 1920, 1080);
        recording.Press(new KeyPress(10.0, "Q", 2, "first"));
        recording.Press(new KeyPress(12.5, "W", 2, "second"));

        // misdirection plays a recording by path, and may do so while a run
        // is still appending to it. The recorder keeps the file open for the
        // run, shared for reading, and the library's reader opens a file a
        // writer still holds, so a read by path sees every frame flushed so
        // far and a clean end of file.
        CollectionAssert.AreEqual(
            new Message[]
            {
                new ScreenSizeMessage(1920, 1080),
                new KeyDownMessage(HidUsage.Q), new KeyUpMessage(HidUsage.Q),
                new DelayMessage(2_500_000),
                new KeyDownMessage(HidUsage.W), new KeyUpMessage(HidUsage.W),
            },
            ProtocolFile.Read(_path).ToArray());
        CollectionAssert.AreEqual(
            new[] { 0.0, 0.0, 0.0, 2.5, 2.5 },
            ProtocolFile.ReadTimed(_path).Select(t => t.At.TotalSeconds).ToArray());

        // What the run writes after that read is there for the next one.
        recording.Press(new KeyPress(14.0, "E", 2, "third"));
        Assert.HasCount(9, ProtocolFile.Read(_path));
    }

    [TestMethod]
    public void A_press_reads_back_as_a_key_down_and_up_on_the_keycap_usage()
    {
        using (var recording = GhostRecording.Append(_path, 1920, 1080))
        {
            recording.Write(new MouseMoveMessage(1700, 900));
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
    public void A_press_with_Ctrl_held_reads_back_as_the_chord()
    {
        IReadOnlyList<Message> pressed;
        using (var recording = GhostRecording.Append(_path, 1920, 1080))
        {
            pressed = recording.Press(new KeyPress(452.1, "Q", 2, "the point goes in Q") { WithControl = true });
            Assert.AreEqual(5, recording.FramesWritten);
        }

        // Ctrl goes down first and comes up last, the tap inside it: the
        // game reads that as a point into Q, not a cast of it.
        var chord = new Message[]
        {
            new KeyDownMessage(HidUsage.LeftControl), new KeyDownMessage(HidUsage.Q),
            new KeyUpMessage(HidUsage.Q), new KeyUpMessage(HidUsage.LeftControl),
        };
        CollectionAssert.AreEqual(chord, pressed.ToArray());
        CollectionAssert.AreEqual(chord, ProtocolFile.Read(_path).Skip(1).ToArray());
        Assert.AreEqual("KeyDown Ctrl (0xE0), KeyDown Q (0x14), KeyUp Q (0x14), KeyUp Ctrl (0xE0)", GhostRecording.Show(pressed));
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

            moved = recording.Write(new MouseMoveMessage(1700, 900));
            CollectionAssert.AreEqual(new Message[] { new MouseMoveMessage(1700, 900) }, moved.ToArray());
            Assert.AreEqual("MouseMove 1700,900", GhostRecording.Show(moved));

            pressed = recording.Press(new KeyPress(17.5, "Q", 2, "Karma in range"));
            CollectionAssert.AreEqual(
                new Message[] { new KeyDownMessage(HidUsage.Q), new KeyUpMessage(HidUsage.Q) }, pressed.ToArray());
            Assert.AreEqual("KeyDown Q (0x14), KeyUp Q (0x14)", GhostRecording.Show(pressed));

            stepped = recording.Step(new MoveStep(218.1, "left", -1, 0, 3, "a bolt from the right"));
            Assert.AreEqual(
                "Delay 200.600s, MouseMove 760,540, MouseButtons Right, MouseButtons None", GhostRecording.Show(stepped));
        }

        // The file holds exactly what was handed back, in order.
        CollectionAssert.AreEqual(
            new Message[] { new ScreenSizeMessage(1920, 1080) }.Concat(moved).Concat(pressed).Concat(stepped).ToArray(),
            ProtocolFile.Read(_path).ToArray());
    }

    [TestMethod]
    public void Presses_and_steps_are_spaced_by_the_video_time_between_them()
    {
        using (var recording = GhostRecording.Append(_path, 1920, 1080))
        {
            Assert.IsNull(recording.LastVideoTime);
            // The first input of a run starts the clock: no delay before it.
            var first = recording.Press(new KeyPress(10.0, "Q", 2, "first"));
            Assert.AreEqual("KeyDown Q (0x14), KeyUp Q (0x14)", GhostRecording.Show(first));
            Assert.AreEqual(10.0, recording.LastVideoTime);

            var second = recording.Press(new KeyPress(12.5, "W", 2, "second"));
            Assert.AreEqual("Delay 2.500s, KeyDown W (0x1A), KeyUp W (0x1A)", GhostRecording.Show(second));

            var step = recording.Step(new MoveStep(13.0, "left", -1, 0, 3, "a bolt"));
            Assert.AreEqual("Delay 0.500s, MouseMove 760,540, MouseButtons Right, MouseButtons None", GhostRecording.Show(step));
            Assert.AreEqual(13.0, recording.LastVideoTime);
            Assert.AreEqual(10, recording.FramesWritten);
        }

        CollectionAssert.AreEqual(
            new Message[]
            {
                new ScreenSizeMessage(1920, 1080),
                new KeyDownMessage(HidUsage.Q), new KeyUpMessage(HidUsage.Q),
                new DelayMessage(2_500_000),
                new KeyDownMessage(HidUsage.W), new KeyUpMessage(HidUsage.W),
                new DelayMessage(500_000),
                new MouseMoveMessage(760, 540),
                new MouseButtonsMessage(MouseButtons.Right),
                new MouseButtonsMessage(MouseButtons.None),
            },
            ProtocolFile.Read(_path).ToArray());

        // The library's timed reader folds the delays into a schedule: the
        // second press lands 2.5s after the first, the step 3s after.
        var timed = ProtocolFile.ReadTimed(_path);
        CollectionAssert.AreEqual(
            new[] { 0.0, 0.0, 0.0, 2.5, 2.5, 3.0, 3.0, 3.0 },
            timed.Select(t => t.At.TotalSeconds).ToArray());
        Assert.IsTrue(timed.All(t => !t.Message.IsFileOnly));
    }

    [TestMethod]
    public void A_step_stamped_before_the_last_press_follows_at_no_gap_and_leaves_the_clock_alone()
    {
        using (var recording = GhostRecording.Append(_path, 1920, 1080))
        {
            recording.Press(new KeyPress(20.0, "Q", 2, "press"));
            // Stamped at the bolt's first sighting, 2s before the press was
            // decided: the file cannot go back, so no gap, and the clock
            // stays at 20 rather than dropping to 18.
            var step = recording.Step(new MoveStep(18.0, "left", -1, 0, 3, "a bolt seen earlier"));
            Assert.AreEqual("MouseMove 760,540, MouseButtons Right, MouseButtons None", GhostRecording.Show(step));
            Assert.AreEqual(20.0, recording.LastVideoTime);
            Assert.AreEqual(TimeSpan.Zero, recording.DelayBefore(18.0));
            Assert.AreEqual(TimeSpan.Zero, recording.DelayBefore(20.0));

            var next = recording.Press(new KeyPress(21.0, "E", 2, "next"));
            Assert.AreEqual("Delay 1.000s, KeyDown E (0x08), KeyUp E (0x08)", GhostRecording.Show(next));
        }

        var timed = ProtocolFile.ReadTimed(_path);
        CollectionAssert.AreEqual(
            new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 1.0 },
            timed.Select(t => t.At.TotalSeconds).ToArray());
    }

    [TestMethod]
    public void Each_run_starts_its_own_clock_so_no_gap_spans_two_runs()
    {
        using (var first = GhostRecording.Append(_path, 1920, 1080))
            first.Press(new KeyPress(10.0, "Q", 2, "first run"));
        using (var second = GhostRecording.Append(_path, 1920, 1080))
        {
            var pressed = second.Press(new KeyPress(500.0, "Q", 2, "second run, much later in the video"));
            Assert.AreEqual("KeyDown Q (0x14), KeyUp Q (0x14)", GhostRecording.Show(pressed));
        }

        Assert.IsFalse(ProtocolFile.Read(_path).Any(m => m is DelayMessage));
    }

    [TestMethod]
    public void Untimed_writes_do_not_move_the_clock()
    {
        using var recording = GhostRecording.Append(_path, 1920, 1080);
        recording.Press(new KeyPress(10.0, "Q", 2, "press"));
        recording.Write(new MouseMoveMessage(1, 2));
        Assert.AreEqual(10.0, recording.LastVideoTime);
        Assert.AreEqual(TimeSpan.FromSeconds(1.5), recording.DelayBefore(11.5));
    }

    [TestMethod]
    public void A_delay_is_rounded_to_the_microsecond_the_file_holds()
    {
        using (var recording = GhostRecording.Append(_path, 1920, 1080))
        {
            recording.Press(new KeyPress(0.0, "Q", 2, "start"));
            // 16.7ms is not a whole number of microseconds in binary; what is
            // handed back must be the record the writer put in the file.
            var pressed = recording.Press(new KeyPress(0.0167, "W", 2, "a frame later"));
            Assert.AreEqual(new DelayMessage(16_700), pressed[0]);
        }

        Assert.AreEqual(new DelayMessage(16_700), ProtocolFile.Read(_path)[3]);
    }

    [TestMethod]
    public void Showing_frames_is_plain_text_with_nothing_added()
    {
        Assert.AreEqual(
            "MouseMove 1,2, KeyDown D (0x07), KeyUp D (0x07), MouseButtons Right, Delay 0.017s",
            GhostRecording.Show(
                new MouseMoveMessage(1, 2),
                new KeyDownMessage(HidUsage.D), new KeyUpMessage(HidUsage.D),
                new MouseButtonsMessage(MouseButtons.Right),
                new DelayMessage(16_700)));
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
                new ScreenSizeMessage(1920, 1080), new DelayMessage(2_500_000), new PongMessage(3),
            ]),
            MindControl.Feed.FeedJson.Options);
        Assert.AreEqual(
            """[{"type":"key_down","key":"Q","usage":20},{"type":"key_up","key":"Q","usage":20},""" +
            """{"type":"mouse_move","x":760,"y":540},{"type":"mouse_buttons","buttons":"Right"},""" +
            """{"type":"screen_size","width":1920,"height":1080},{"type":"delay","microseconds":2500000},""" +
            """{"type":"pong","payload":"03"}]""",
            json);
    }

    [TestMethod]
    public void Keycaps_map_to_usages_and_back()
    {
        foreach (var keycap in "ABCQZ0159")
            Assert.AreEqual(keycap.ToString(), GhostRecording.KeycapOf(GhostRecording.UsageOf(keycap.ToString())));
        Assert.AreEqual("Ctrl", GhostRecording.KeycapOf(HidUsage.LeftControl), "the modifier a chord holds");
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
