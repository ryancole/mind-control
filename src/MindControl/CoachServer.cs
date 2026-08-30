using System.Net;
using System.Text.Json;
using MindControl.Feed;
using MindControl.Policy;

namespace MindControl;

/// <summary>
/// Publishes the coaching feedback as a local SSE stream (`/stream`) so a
/// browser — e.g. the spectral-sight dashboard's coaching panel — can show it
/// live. Strictly output: it serves what the reactor already decided and
/// accepts nothing back; the observe-and-advise boundary is unchanged.
/// Positions go out normalized to the minimap rect (0..1, y down), which is
/// the same space the dashboard draws its map in.
/// </summary>
public sealed class CoachServer : IDisposable
{
    private const int ReplayCount = 32;

    private readonly MinimapRect _minimap;
    private readonly HttpListener _listener = new();
    private readonly List<StreamWriter> _clients = [];
    private readonly Queue<string> _replay = new();
    private readonly Lock _lock = new();

    public CoachServer(int port, MinimapRect minimap)
    {
        _minimap = minimap;
        // localhost (not 127.0.0.1): the one prefix http.sys grants without
        // elevation or a urlacl reservation.
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public void PublishGlance(GlanceNote note, int? gameTime)
    {
        var (nx, ny) = Normalize(note.X, note.Y);
        Publish(new
        {
            T = "glance", VideoTime = note.VideoTime, GameTime = gameTime,
            Nx = nx, Ny = ny, Priority = note.Priority, Reason = note.Reason,
        });
    }

    public void PublishMove(double videoTime, GhostCursor cursor, int? gameTime)
    {
        var (nx, ny) = Normalize(cursor.X, cursor.Y);
        Publish(new { T = "move", VideoTime = videoTime, GameTime = gameTime, Nx = nx, Ny = ny });
    }

    public void PublishStatus(string state, string? reason = null) =>
        Publish(new { T = "status", State = state, Reason = reason });

    private (double, double) Normalize(ushort x, ushort y) =>
        ((x - _minimap.X) / _minimap.Width, (y - _minimap.Y) / _minimap.Height);

    private void Publish<TLine>(TLine line)
    {
        var data = $"data: {JsonSerializer.Serialize(line, FeedJson.Options)}\n\n";
        lock (_lock)
        {
            _replay.Enqueue(data);
            while (_replay.Count > ReplayCount)
                _replay.Dequeue();
            // Localhost writes land in http.sys buffers; a client that has
            // gone away throws and is dropped rather than stalling the loop.
            _clients.RemoveAll(client =>
            {
                try { client.Write(data); return false; }
                catch (Exception e) when (e is IOException or ObjectDisposedException or HttpListenerException)
                {
                    client.Dispose();
                    return true;
                }
            });
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                return; // disposed
            }

            var response = context.Response;
            if (context.Request.Url?.AbsolutePath != "/stream")
            {
                response.StatusCode = 404;
                response.Close();
                continue;
            }

            response.ContentType = "text/event-stream";
            // The dashboard is served from another local origin (the feed's).
            response.AppendHeader("Access-Control-Allow-Origin", "*");
            response.AppendHeader("Cache-Control", "no-cache");
            response.SendChunked = true;
            var writer = new StreamWriter(response.OutputStream) { AutoFlush = true };
            lock (_lock)
            {
                try
                {
                    foreach (var line in _replay)
                        writer.Write(line);
                    _clients.Add(writer);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or HttpListenerException)
                {
                    writer.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        // Clients first: disposing a StreamWriter flushes into http.sys, which
        // throws once the listener's request queue handle is closed.
        lock (_lock)
        {
            foreach (var client in _clients)
            {
                try { client.Dispose(); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or HttpListenerException)
                {
                    // A client that is already gone; nothing left to flush.
                }
            }
            _clients.Clear();
        }
        _listener.Close();
    }
}
