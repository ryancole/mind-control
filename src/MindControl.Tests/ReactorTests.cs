using System.Net;
using System.Net.Sockets;
using System.Text;
using MindControl.Feed;
using MindControl.Policy;

namespace MindControl.Tests;

/// <summary>
/// The reactor over a real SSE stream from a scripted feed: one run holding
/// two games. The policy must be told of the new game once, before anything
/// from it, and never be shown the old game again.
/// </summary>
[TestClass]
public sealed class ReactorTests
{
    /// <summary>Records what the reactor told it, in order, and signals when it has seen the last frame.</summary>
    private sealed class Recorder(long lastSeq) : IPolicy
    {
        public readonly List<string> Calls = [];
        public readonly List<FrameEnvelope> Frames = [];
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Configure(Meta meta) { }

        public void Resync(FrameEnvelope? latest)
        {
            Calls.Add(latest is null ? "resync" : $"resync {latest.Seq} game {latest.Game}");
            See(latest);
        }

        public void NewGame(int game) => Calls.Add($"new game {game}");

        public void OnFrame(FrameEnvelope frame)
        {
            Calls.Add($"frame {frame.Seq} game {frame.Game}");
            See(frame);
        }

        public void OnEvent(GameEvent evt) => Calls.Add($"event {evt.Kind}");

        private void See(FrameEnvelope? frame)
        {
            if (frame is null)
                return;
            Frames.Add(frame);
            if (frame.Seq >= lastSeq)
                Done.TrySetResult();
        }
    }

    private static string Row(int game, int? gameTime, int track, string champion) =>
        $$"""{"video_time":0,"game":{{game}},"game_time":{{Json(gameTime)}},"track_id":{{track}},"team":"blue","champion":"{{champion}}","x":10,"y":10,"visible":true,"is_self":true}""";

    private static string Json(int? value) => value?.ToString() ?? "null";

    /// <summary>A frame record; <paramref name="game"/> null leaves the key out, as an older timeline does.</summary>
    private static string Frame(long seq, int? game, int? gameTime, params string[] rows) =>
        $"event: frame\nid: {seq}\ndata: {{\"t\":\"frame\",\"seq\":{seq},\"video_time\":{seq * 0.1:0.0}," +
        (game is null ? "" : $"\"game\":{game},") +
        $"\"game_time\":{Json(gameTime)},\"game_time_observed\":{(gameTime is null ? "false" : "true")}," +
        $"\"champions\":[{string.Join(",", rows)}]}}\n\n";

    private static string Event(long seq, string json) => $"event: event\nid: {seq}\ndata: {json}\n\n";

    /// <summary>
    /// Game 0 (from an older-shaped envelope with no game key, then one with
    /// it), the gap between games with no clock, then game 1 with its clock
    /// back near zero.
    /// </summary>
    private static List<string> TwoGames(bool withEvent)
    {
        List<string> records = [];
        long seq = 1;
        records.Add(Frame(seq++, null, 1500, Row(0, 1500, 3, "Ezreal")));
        for (var i = 0; i < 5; i++)
            records.Add(Frame(seq++, 0, 1501 + i, Row(0, 1501 + i, 3, "Ezreal")));
        // Post-game and loading screen: no clock, no rows.
        for (var i = 0; i < 5; i++)
            records.Add(Frame(seq++, 0, null));
        if (withEvent)
            records.Add(Event(seq, $$"""{"t":"event","kind":"new_game","seq":{{seq}},"video_time":{{seq * 0.1:0.0}},"game_time":2,"game":1,"team":null,"champion":null,"track_id":null}"""));
        for (var i = 0; i < 5; i++)
            records.Add(Frame(seq++, 1, 2 + i, Row(1, 2 + i, 14, "Ahri")));
        return records;
    }

    private const long LastSeq = 16;

    [TestMethod]
    public async Task A_new_game_event_resets_the_policy_once_before_the_new_games_frames() =>
        AssertTwoGames(await Run(TwoGames(withEvent: true)));

    [TestMethod]
    public async Task The_envelopes_game_resets_the_policy_when_the_event_never_came() =>
        AssertTwoGames(await Run(TwoGames(withEvent: false)));

    private static void AssertTwoGames(Recorder policy)
    {
        var calls = string.Join("\n", policy.Calls);
        Assert.AreEqual(1, policy.Calls.Count(c => c == "new game 1"), calls);
        var reset = policy.Calls.IndexOf("new game 1");
        Assert.IsFalse(policy.Calls.Take(reset).Any(c => c.EndsWith("game 1")), $"nothing of game 1 before the reset\n{calls}");
        Assert.IsFalse(policy.Calls.Skip(reset).Any(c => c.EndsWith("game 0")), $"nothing of game 0 after it\n{calls}");
        StringAssert.StartsWith(policy.Calls[reset + 1], "resync ", $"the new game's first frame is a baseline\n{calls}");
        Assert.DoesNotContain("event new_game", policy.Calls, "the reset is the reactor's to make, not an event to coach on");
        Assert.AreEqual(0, policy.Frames[0].Game, "a missing game reads as 0");
        Assert.IsTrue(policy.Frames.Any(f => f.GameTime is null), "clockless frames between games are passed on");
    }

    private static async Task<Recorder> Run(List<string> records)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = Serve(listener, records, cts.Token);

        var policy = new Recorder(LastSeq);
        var reactor = new Reactor(new FeedClient(new Uri($"http://localhost:{port}/")), policy, new ReactorOptions
        {
            FrameTimeout = TimeSpan.FromSeconds(5),
        });
        var running = reactor.RunAsync(cts.Token);
        await Task.WhenAny(policy.Done.Task, Task.Delay(Timeout.Infinite, cts.Token).ContinueWith(_ => { }));
        Assert.IsTrue(policy.Done.Task.IsCompleted, $"the last frame never reached the policy:\n{string.Join("\n", policy.Calls)}");
        await cts.CancelAsync();
        try { await running; } catch (OperationCanceledException) { }
        listener.Stop();
        try { await serving; } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
        return policy;
    }

    /// <summary>
    /// /meta, then /stream: the records a few milliseconds apart (the feed's
    /// frames come at ~10 Hz, and the reactor keeps only the latest), and the
    /// stream held open. A reconnect is sent nothing more.
    /// </summary>
    private static async Task Serve(HttpListener listener, List<string> records, CancellationToken ct)
    {
        var streamed = false;
        while (!ct.IsCancellationRequested)
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            var response = context.Response;
            if (context.Request.Url?.AbsolutePath == "/meta")
            {
                response.ContentType = "application/json";
                response.Close("""{"schema":2,"source":"test","width":1920,"height":1080}"""u8.ToArray(), willBlock: false);
                continue;
            }
            response.ContentType = "text/event-stream";
            response.SendChunked = true;
            var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
            if (!streamed)
            {
                streamed = true;
                _ = Task.Run(async () =>
                {
                    foreach (var record in records)
                    {
                        await writer.WriteAsync(record);
                        await Task.Delay(20, ct);
                    }
                }, ct);
            }
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
