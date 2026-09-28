using System.Net;
using System.Net.Sockets;
using MindControl.Policy;

namespace MindControl.Tests;

/// <summary>
/// The brain view's plumbing: the page is served, and the thoughts reach
/// <c>/brain</c> and stay off <c>/stream</c>, which the dashboard reads.
/// </summary>
[TestClass]
public sealed class CoachServerTests
{
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static readonly Thought Asked = new()
    {
        Id = 7, Phase = "asked", Occasion = "decide", VideoTime = 12.5,
        Branches = [new ThoughtBranch("carry_on", null), new ThoughtBranch("buy", "not in the fountain")],
    };

    [TestMethod]
    public async Task The_page_is_served_at_the_root()
    {
        var port = FreePort();
        using var server = new CoachServer(port, "jev-test");
        using var http = new HttpClient();

        var page = await http.GetStringAsync($"http://localhost:{port}/");

        StringAssert.Contains(page, "<title>mind-control brain</title>", "the embedded page, not the placeholder");
        StringAssert.Contains(page, "new EventSource(\"/brain\")");
    }

    [TestMethod]
    public async Task Thoughts_reach_the_brain_stream_and_not_the_dashboards()
    {
        var port = FreePort();
        using var server = new CoachServer(port, "jev-test");
        server.PublishThought(Asked);
        server.PublishStatus("coaching");
        using var http = new HttpClient();

        var brain = await FirstLines(http, $"http://localhost:{port}/brain", 2);
        var stream = await FirstLines(http, $"http://localhost:{port}/stream", 1);

        Assert.IsTrue(brain.Any(l => l.Contains("\"t\":\"thought\"") && l.Contains("\"id\":7")
            && l.Contains("\"gate\":\"not in the fountain\"")), string.Join("\n", brain));
        Assert.IsTrue(brain.Any(l => l.Contains("\"t\":\"status\"")));
        Assert.IsFalse(stream.Any(l => l.Contains("thought")), string.Join("\n", stream));
        StringAssert.Contains(stream.Single(), "\"t\":\"status\"");
    }

    /// <summary>The first <paramref name="count"/> data lines of an SSE stream: the replay a client is sent on connecting.</summary>
    private static async Task<string[]> FirstLines(HttpClient http, string url, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        List<string> lines = [];
        while (lines.Count < count && await reader.ReadLineAsync(timeout.Token) is { } line)
            if (line.StartsWith("data: "))
                lines.Add(line["data: ".Length..]);
        return [.. lines];
    }
}
