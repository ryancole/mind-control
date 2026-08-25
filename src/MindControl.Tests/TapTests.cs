using MindControl.Device;

namespace MindControl.Tests;

[TestClass]
public sealed class TapTests
{
    private sealed class RecordingLink : IDeviceLink
    {
        public List<Intent> Sent { get; } = [];

        public event Action<DeviceMessage>? MessageReceived { add { } remove { } }

        public void Send(Intent intent) => Sent.Add(intent);

        public void Dispose() { }
    }

    [TestMethod]
    public void Tap_is_a_down_then_up_of_the_same_usage()
    {
        var link = new RecordingLink();

        link.Tap(HidUsage.B, holdMs: 0);

        CollectionAssert.AreEqual(
            new Intent[] { new KeyDown(HidUsage.B), new KeyUp(HidUsage.B) }, link.Sent);
    }
}
