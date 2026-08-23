using System.Text.Json;
using MindControl.Device;

namespace MindControl.Tests;

/// <summary>
/// Pins the encoder and decoder byte-for-byte against misdirection's
/// protocol-vectors.json. If these fail after a sync, the protocol moved and
/// this host must move with it.
/// </summary>
[TestClass]
public sealed class WireVectorTests
{
    private static JsonElement _root;

    [ClassInitialize]
    public static void LoadVectors(TestContext _)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "protocol-vectors.json"));
        _root = JsonDocument.Parse(json).RootElement;
    }

    private static IEnumerable<JsonElement> Vectors(string direction) =>
        _root.GetProperty("vectors").EnumerateArray()
            .Where(v => v.GetProperty("direction").GetString() == direction);

    private static byte[] ExpectedBytes(JsonElement vector) =>
        [.. vector.GetProperty("bytes").EnumerateArray().Select(b => b.GetByte())];

    [TestMethod]
    public void Encoder_matches_every_host_to_device_vector()
    {
        var checkedCount = 0;
        foreach (var vector in Vectors("host_to_device"))
        {
            var name = vector.GetProperty("name").GetString();
            var intent = ToIntent(vector.GetProperty("typeName").GetString()!, vector.GetProperty("fields"));
            CollectionAssert.AreEqual(ExpectedBytes(vector), Wire.Encode(intent), $"vector '{name}'");
            checkedCount++;
        }
        Assert.AreEqual(13, checkedCount, "vector file should cover every host->device message");
    }

    private static Intent ToIntent(string typeName, JsonElement fields) => typeName switch
    {
        "PANIC" => Intent.Panic,
        "PING" => Intent.Ping,
        "KEY_DOWN" => new KeyDown(fields.GetProperty("usage").GetByte()),
        "KEY_UP" => new KeyUp(fields.GetProperty("usage").GetByte()),
        "MOUSE_MOVE" => new MouseMove(fields.GetProperty("x").GetUInt16(), fields.GetProperty("y").GetUInt16()),
        "MOUSE_BTN" => new MouseButtons(fields.GetProperty("mask").GetByte()),
        "MOUSE_WHEEL" => new MouseWheel(fields.GetProperty("vert").GetSByte(), fields.GetProperty("horiz").GetSByte()),
        "SCREEN_SIZE" => new ScreenSize(fields.GetProperty("width").GetUInt16(), fields.GetProperty("height").GetUInt16()),
        _ => throw new AssertFailedException($"vector file has host->device type '{typeName}' this encoder cannot produce"),
    };

    [TestMethod]
    public void Decoder_reads_every_device_to_host_vector()
    {
        var checkedCount = 0;
        foreach (var vector in Vectors("device_to_host"))
        {
            var name = vector.GetProperty("name").GetString();
            var messages = new DeviceFrameReader().Feed(ExpectedBytes(vector));
            Assert.HasCount(1, messages, $"vector '{name}'");

            var fields = vector.GetProperty("fields");
            DeviceMessage expected = vector.GetProperty("typeName").GetString() switch
            {
                "PONG" => new Pong(fields.GetProperty("version").GetByte()),
                "NACK" => new Nack(fields.GetProperty("reason").GetByte()),
                var t => throw new AssertFailedException($"vector file has device->host type '{t}' this decoder cannot read"),
            };
            Assert.AreEqual(expected, messages[0], $"vector '{name}'");
            checkedCount++;
        }
        Assert.AreEqual(2, checkedCount, "vector file should cover PONG and NACK");
    }

    [TestMethod]
    public void Protocol_constants_match_the_vector_file()
    {
        Assert.AreEqual(Wire.Sof, _root.GetProperty("sof").GetByte());
        foreach (var vector in _root.GetProperty("vectors").EnumerateArray())
        {
            var typeName = vector.GetProperty("typeName").GetString()!;
            var expected = vector.GetProperty("type").GetByte();
            var actual = (byte)Enum.Parse<MsgType>(ToPascal(typeName));
            Assert.AreEqual(expected, actual, $"MsgType.{ToPascal(typeName)}");
        }
    }

    private static string ToPascal(string screamingSnake) =>
        string.Concat(screamingSnake.Split('_').Select(w => w[0] + w[1..].ToLowerInvariant()));
}
