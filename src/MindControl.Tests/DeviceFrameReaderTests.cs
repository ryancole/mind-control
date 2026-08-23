using MindControl.Device;

namespace MindControl.Tests;

[TestClass]
public sealed class DeviceFrameReaderTests
{
    private static readonly byte[] PongV1 = [0xAB, 0x80, 0x01, 0x01, 0x82];

    [TestMethod]
    public void Reassembles_a_frame_split_across_reads()
    {
        var reader = new DeviceFrameReader();
        Assert.IsEmpty(reader.Feed(PongV1.AsSpan(0, 2)));
        Assert.IsEmpty(reader.Feed(PongV1.AsSpan(2, 2)));
        var messages = reader.Feed(PongV1.AsSpan(4));
        Assert.AreEqual(new Pong(1), messages.Single());
    }

    [TestMethod]
    public void Skips_noise_before_the_start_byte()
    {
        var reader = new DeviceFrameReader();
        var messages = reader.Feed([0x00, 0xFF, 0x42, .. PongV1]);
        Assert.AreEqual(new Pong(1), messages.Single());
    }

    [TestMethod]
    public void Discards_a_bad_checksum_and_resyncs_on_the_next_frame()
    {
        var reader = new DeviceFrameReader();
        byte[] corrupted = [0xAB, 0x80, 0x01, 0x01, 0x00];
        var messages = reader.Feed([.. corrupted, .. PongV1]);
        Assert.AreEqual(new Pong(1), messages.Single());
    }

    [TestMethod]
    public void Rejects_oversized_length_and_resyncs()
    {
        var reader = new DeviceFrameReader();
        // len 0xFF would swallow the following stream if honored; the parser
        // must drop it (mirroring the firmware's NACK(3) path) and recover.
        var messages = reader.Feed([0xAB, 0x80, 0xFF, .. PongV1]);
        Assert.AreEqual(new Pong(1), messages.Single());
    }

    [TestMethod]
    public void Start_byte_inside_a_payload_is_not_a_framing_hazard()
    {
        var reader = new DeviceFrameReader();
        // Hypothetical 2-byte message whose payload contains 0xAB: len is read
        // before the payload, so it must parse as one unknown message.
        byte[] frame = [0xAB, 0x42, 0x02, 0xAB, 0x01, (byte)(0x42 + 0x02 + 0xAB + 0x01)];
        var messages = reader.Feed(frame);
        var unknown = (UnknownDeviceMessage)messages.Single();
        Assert.AreEqual(0x42, unknown.Type);
        CollectionAssert.AreEqual(new byte[] { 0xAB, 0x01 }, unknown.Payload);
    }

    [TestMethod]
    public void Nack_reasons_carry_through()
    {
        var reader = new DeviceFrameReader();
        byte[] disarmed = [0xAB, 0x81, 0x01, 0x04, 0x86];
        Assert.AreEqual(new Nack(Nack.Disarmed), reader.Feed(disarmed).Single());
    }
}
