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
    public void Keycaps_map_to_usages_and_unknown_ones_are_refused()
    {
        Assert.AreEqual(HidUsage.Q, GhostRecording.UsageOf("Q"));
        Assert.AreEqual(HidUsage.D, GhostRecording.UsageOf("d"));
        Assert.AreEqual(HidUsage.Digit1, GhostRecording.UsageOf("1"));
        Assert.AreEqual(HidUsage.Digit0, GhostRecording.UsageOf("0"));
        Assert.ThrowsExactly<ArgumentException>(() => GhostRecording.UsageOf("Space"));
    }
}
