using MindControl;
using MindControl.Device;
using MindControl.Feed;
using MindControl.Policy;

var feedUri = new Uri("http://127.0.0.1:8723");
string? portName = null;
ushort screenWidth = 1920, screenHeight = 1080;
// A placeholder guess at the player's minimap rect until real calibration
// exists (default League HUD, bottom-right, 1920x1080).
var minimap = new MinimapRect(1620, 780, 300, 300);
string? tracePath = null;
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
        case "--port":
            portName = args[++i];
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
                mind-control: reacts to the spectral-sight feed by driving the misdirection device.

                options:
                  --feed <url>       feed base URL          (default http://127.0.0.1:8723)
                  --port <name>      serial port, e.g. COM5 (default: dry run, intents logged)
                  --screen <WxH>     target screen size     (default 1920x1080)
                  --minimap <x,y,w,h> minimap rect on the player's screen (default 1620,780,300,300)
                  --trace <file>     record the ghost's cursor path for etc/ghost-viewer.html
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

using IDeviceLink link = portName is null
    ? new ConsoleDeviceLink()
    : new SerialDeviceLink(portName);

var feed = new FeedClient(feedUri, kinds);
var options = new ReactorOptions { ScreenWidth = screenWidth, ScreenHeight = screenHeight };
using var trace = tracePath is null ? null : new GhostTrace(tracePath, minimap, screenWidth, screenHeight);
var policy = new AttentionPolicy(minimap, new AttentionOptions { SelfChampion = selfChampion });
var reactor = new Reactor(feed, link, policy, options, trace);

// Tap demo: no keyboard policy exists yet, but the tap idiom does. A key typed
// into this console taps the same key through the device — logged in a dry
// run, a physical keypress on hardware. Runs on a background thread so a
// blocked ReadKey never holds up shutdown.
if (!Console.IsInputRedirected)
{
    _ = Task.Run(() =>
    {
        while (!cts.IsCancellationRequested)
        {
            var key = Console.ReadKey(intercept: true);
            if (HidUsage.TryFromChar(key.KeyChar, out var usage))
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} tap: '{key.KeyChar}' (usage 0x{usage:X2})");
                link.Tap(usage);
            }
        }
    });
}

try
{
    Console.WriteLine(portName is null
        ? $"dry run against {feedUri} (no serial port; intents are logged)"
        : $"driving {portName} from {feedUri}");
    if (!Console.IsInputRedirected)
        Console.WriteLine("type a letter/digit here to tap it through the device");
    await reactor.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // Ctrl-C: fall through to the parting PANIC.
}
finally
{
    // Never leave the device with a key held, whatever ended the run.
    link.Send(Intent.Panic);
}
return 0;
