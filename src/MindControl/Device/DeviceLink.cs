using System.IO.Ports;

namespace MindControl.Device;

/// <summary>
/// Fire-and-forget path to the misdirection board. No per-frame ACK exists;
/// NACKs and PONGs arrive asynchronously on <see cref="MessageReceived"/>.
/// </summary>
public interface IDeviceLink : IDisposable
{
    event Action<DeviceMessage>? MessageReceived;

    void Send(Intent intent);
}

/// <summary>115200 8N1, no flow control, DTR/RTS unused.</summary>
public sealed class SerialDeviceLink : IDeviceLink
{
    private readonly SerialPort _port;
    private readonly DeviceFrameReader _reader = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private readonly Lock _writeLock = new();

    public event Action<DeviceMessage>? MessageReceived;

    public SerialDeviceLink(string portName)
    {
        _port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
        };
        _port.Open();
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    public void Send(Intent intent)
    {
        var frame = Wire.Encode(intent);
        lock (_writeLock)
        {
            _port.Write(frame, 0, frame.Length);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[256];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await _port.BaseStream.ReadAsync(buffer, ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            if (n == 0)
                return;
            foreach (var message in _reader.Feed(buffer.AsSpan(0, n)))
                MessageReceived?.Invoke(message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _port.Close();
            _readLoop.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // Closing a serial port mid-read throws on some drivers; nothing to do.
        }
        _cts.Dispose();
        _port.Dispose();
    }
}

/// <summary>Dry-run link: logs every intent instead of writing to a port.</summary>
public sealed class ConsoleDeviceLink : IDeviceLink
{
    public event Action<DeviceMessage>? MessageReceived;

    public void Send(Intent intent)
    {
        var hex = Convert.ToHexString(Wire.Encode(intent));
        Console.WriteLine($"[dry-run] {intent} -> {hex}");
        // Pretend to be an armed, live board so the connect sequence completes.
        if (intent is PingIntent)
            MessageReceived?.Invoke(new Pong(1));
    }

    public void Dispose() { }
}
