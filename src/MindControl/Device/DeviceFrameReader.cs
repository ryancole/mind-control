namespace MindControl.Device;

public abstract record DeviceMessage;

/// <summary>Sent in reply to PING, and unsolicited at boot — an unsolicited PONG means the board just reset.</summary>
public sealed record Pong(byte Version) : DeviceMessage;

/// <summary>Reasons: 1 bad checksum, 2 unknown type, 3 bad length, 4 disarmed (expected, not an error), 5 rollover full.</summary>
public sealed record Nack(byte Reason) : DeviceMessage
{
    public const byte BadChecksum = 1;
    public const byte UnknownType = 2;
    public const byte BadLength = 3;
    public const byte Disarmed = 4;
    public const byte RolloverFull = 5;
}

/// <summary>A well-framed message with a type this host does not know. Ignorable, worth logging.</summary>
public sealed record UnknownDeviceMessage(byte Type, byte[] Payload) : DeviceMessage;

/// <summary>
/// Incremental decoder for the device→host side of the serial link, the same
/// state machine the firmware runs: HUNT → TYPE → LEN → PAYLOAD → SUM. On a
/// bad checksum or oversized length the frame is discarded and the reader goes
/// back to HUNT, matching the firmware's resync behavior.
/// </summary>
public sealed class DeviceFrameReader
{
    private enum State { Hunt, Type, Len, Payload, Sum }

    private State _state = State.Hunt;
    private byte _type;
    private int _len;
    private readonly byte[] _payload = new byte[Wire.MaxPayload];
    private int _got;

    /// <summary>Feed raw serial bytes; returns every complete message they finished.</summary>
    public List<DeviceMessage> Feed(ReadOnlySpan<byte> bytes)
    {
        var messages = new List<DeviceMessage>();
        foreach (var b in bytes)
        {
            switch (_state)
            {
                case State.Hunt:
                    if (b == Wire.Sof)
                        _state = State.Type;
                    break;
                case State.Type:
                    _type = b;
                    _state = State.Len;
                    break;
                case State.Len:
                    if (b > Wire.MaxPayload)
                    {
                        _state = State.Hunt;
                        break;
                    }
                    _len = b;
                    _got = 0;
                    _state = _len == 0 ? State.Sum : State.Payload;
                    break;
                case State.Payload:
                    _payload[_got++] = b;
                    if (_got == _len)
                        _state = State.Sum;
                    break;
                case State.Sum:
                    int sum = _type + _len;
                    for (var i = 0; i < _len; i++)
                        sum += _payload[i];
                    if (b == (byte)sum)
                        messages.Add(Decode());
                    _state = State.Hunt;
                    break;
            }
        }
        return messages;
    }

    private DeviceMessage Decode() => ((MsgType)_type, _len) switch
    {
        (MsgType.Pong, 1) => new Pong(_payload[0]),
        (MsgType.Nack, 1) => new Nack(_payload[0]),
        _ => new UnknownDeviceMessage(_type, _payload[.._len]),
    };
}
