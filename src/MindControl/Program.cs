using MindControl;
using MindControl.Device;
using MindControl.Feed;
using MindControl.Policy;

var feedUri = new Uri("http://127.0.0.1:8723");
string? portName = null;
ushort screenWidth = 1920, screenHeight = 1080;
HashSet<string>? kinds =
[
    EventKind.Death, EventKind.Respawn, EventKind.Cast, EventKind.Reappeared,
    EventKind.Identified, EventKind.LevelUp, EventKind.Roster,
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
        case "--kinds":
            // Which event kinds reach the policy; "all" disables the filter.
            // The default set excludes "vanished" — fog-out traffic dominates
            // the raw feed and would churn the bounded notice queue.
            var list = args[++i];
            kinds = list == "all" ? null : [.. list.Split(',', StringSplitOptions.TrimEntries)];
            break;
        case "--help" or "-h":
            Console.WriteLine("""
                mind-control: reacts to the spectral-sight feed by driving the misdirection device.

                options:
                  --feed <url>     feed base URL          (default http://127.0.0.1:8723)
                  --port <name>    serial port, e.g. COM5 (default: dry run, intents logged)
                  --screen <WxH>   target screen size     (default 1920x1080)
                  --kinds <a,b|all> event kinds passed to the policy
                                   (default death,respawn,cast,reappeared,identified,level_up,roster)
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
var reactor = new Reactor(feed, link, new NoOpPolicy(), options);

try
{
    Console.WriteLine(portName is null
        ? $"dry run against {feedUri} (no serial port; intents are logged)"
        : $"driving {portName} from {feedUri}");
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
