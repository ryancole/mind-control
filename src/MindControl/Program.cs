using MindControl;
using MindControl.Feed;
using MindControl.Policy;

var feedUri = new Uri("http://127.0.0.1:8723");
ushort screenWidth = 1920, screenHeight = 1080;
// A placeholder guess at the player's minimap rect until real calibration
// exists (default League HUD, bottom-right, 1920x1080).
var minimap = new MinimapRect(1620, 780, 300, 300);
string? tracePath = null;
string? logPath = null;
string? selfChampion = null;
HashSet<string>? kinds =
[
    EventKind.Death, EventKind.Respawn, EventKind.Cast, EventKind.Vanished,
    EventKind.Reappeared, EventKind.Identified, EventKind.LevelUp, EventKind.Roster,
];

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--feed":
            feedUri = new Uri(args[++i]);
            break;
        case "--screen":
            var parts = args[++i].Split('x');
            screenWidth = ushort.Parse(parts[0]);
            screenHeight = ushort.Parse(parts[1]);
            break;
        case "--minimap":
            var rect = args[++i].Split(',');
            minimap = new MinimapRect(
                double.Parse(rect[0]), double.Parse(rect[1]),
                double.Parse(rect[2]), double.Parse(rect[3]));
            break;
        case "--trace":
            tracePath = args[++i];
            break;
        case "--log":
            logPath = args[++i];
            break;
        case "--self":
            selfChampion = args[++i];
            break;
        case "--kinds":
            // Which event kinds reach the policy; "all" disables the filter.
            var list = args[++i];
            kinds = list == "all" ? null : [.. list.Split(',', StringSplitOptions.TrimEntries)];
            break;
        case "--help" or "-h":
            Console.WriteLine("""
                mind-control: watches the spectral-sight feed and prints fair-play coaching feedback.
                It observes and advises only — no input is ever sent to the game or any device.

                options:
                  --feed <url>       feed base URL          (default http://127.0.0.1:8723)
                  --screen <WxH>     target screen size     (default 1920x1080)
                  --minimap <x,y,w,h> minimap rect on the player's screen (default 1620,780,300,300)
                  --trace <file>     record the ghost's cursor path for etc/ghost-viewer.html
                  --log <file>       also append coaching feedback to this file
                  --self <champion>  the coached player's champion (default: majority-vote is_self)
                  --kinds <a,b|all>  event kinds passed to the policy (default: all but the noisy ones)
                """);
            return 0;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]} (try --help)");
            return 2;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var feed = new FeedClient(feedUri, kinds);
var options = new ReactorOptions { ScreenWidth = screenWidth, ScreenHeight = screenHeight };
using var trace = tracePath is null ? null : new GhostTrace(tracePath, minimap, screenWidth, screenHeight);
using TextWriter? log = logPath is null ? null : new StreamWriter(logPath, append: true) { AutoFlush = true };
var policy = new AttentionPolicy(minimap, new AttentionOptions { SelfChampion = selfChampion });
var reactor = new Reactor(feed, policy, options, log, trace);

try
{
    Console.WriteLine($"coaching against {feedUri} — feedback to the console" +
        (logPath is null ? "" : $" and {logPath}") + "; no input is sent anywhere");
    await reactor.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // Ctrl-C: clean shutdown.
}
return 0;
