using Jev;
using Microsoft.Extensions.Configuration;
using MindControl;
using MindControl.Feed;
using MindControl.Policy;

var feedUri = new Uri("http://127.0.0.1:8723");
ushort screenWidth = 1920, screenHeight = 1080;
// Where the player's model sits on their screen; a step's right-click is
// taken from here. The camera is locked, so it is one place -- the centre,
// give or take, on the default HUD.
(ushort X, ushort Y)? playerAnchor = null;
// Where the minimap sits on the player's screen; a walk to a place on the
// map is one right-click there. Null takes the stock HUD's corner, a
// placeholder until etc/minimap-calibrator.html has read the real one.
MinimapRect? minimap = null;
string? tracePath = null;
string? logPath = null;
string? auditPath = null;
// The ghost's input in misdirection's wire format. On by default: this file is
// the demonstration the whole pipeline exists to produce. data/ is gitignored.
// A file of its own per run, named for when the run started, so a session's
// ghost is never mixed with an earlier one's.
string? recordPath = $"data/ghost-{DateTime.Now:yyyyMMdd-HHmmss}.msdr";
string? selfChampion = null;
// Pinned, not the alias: the thresholds in JevOptions were tuned against one
// release's calibration, and jev-latest moves without notice.
var model = JevModels.Jev1_13_0;
var servePort = 8724;
HashSet<string>? kinds =
[
    // Identity corrections and rosters are bookkeeping the policy and the
    // dashboard keep; the rest are the coaching stages (level_up is kept for
    // the log, where it explains the skill_point half a second behind it). Listed explicitly,
    // which means a kind spectral-sight adds later is dropped until it is
    // named here -- worth knowing, because the symptom is silence rather
    // than an error.
    EventKind.Identified, EventKind.Roster,
    EventKind.Ability, EventKind.Threat, EventKind.Skillshot, EventKind.LevelUp,
    EventKind.SkillPoint, EventKind.SkillSpent,
    // Logged only for now: no question is asked of them yet. The turrets'
    // state rides the self row, which is what the questions are told.
    EventKind.LastHit, EventKind.MissedCs, EventKind.TurretDestroyed, EventKind.TurretRebuilt,
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
        case "--anchor":
            var anchor = args[++i].Split(',');
            playerAnchor = (ushort.Parse(anchor[0]), ushort.Parse(anchor[1]));
            break;
        case "--minimap":
            var rect = args[++i].Split(',');
            minimap = new MinimapRect(
                double.Parse(rect[0]), double.Parse(rect[1]), double.Parse(rect[2]), double.Parse(rect[3]));
            break;
        case "--trace":
            tracePath = args[++i];
            break;
        case "--log":
            logPath = args[++i];
            break;
        case "--audit":
            auditPath = args[++i];
            break;
        case "--record":
            // "none" turns it off, the way "all" lifts the --kinds filter.
            var record = args[++i];
            recordPath = record == "none" ? null : record;
            break;
        case "--self":
            selfChampion = args[++i];
            break;
        case "--model":
            model = args[++i];
            break;
        case "--serve":
            servePort = int.Parse(args[++i]);
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
                The coaching itself is Jev's (TypeSafe's System One model): every decision is a
                question put to it about what the player can see, answered as a probability.

                options:
                  --feed <url>       feed base URL          (default http://127.0.0.1:8723)
                  --screen <WxH>     target screen size     (default 1920x1080)
                  --anchor <x,y>     the player's model on their screen, where a step is taken
                                     from (default: screen centre; the camera is locked)
                  --minimap <x,y,w,h> the minimap on their screen, where a walk to lane is
                                     clicked (default: the stock HUD's bottom-right corner;
                                     etc/minimap-calibrator.html reads it off a screenshot)
                  --trace <file>     record when the coach pressed and stepped, for etc/ghost-viewer.html
                  --log <file>       also append coaching feedback to this file
                  --audit <file>     record every question put to Jev and its answer (JSONL)
                  --record <file|none> append the ghost's mouse and key input as a misdirection
                                     protocol file (.msdr)  (default data/ghost-<yyyyMMdd-HHmmss>.msdr,
                                     a fresh file for each run)
                  --self <champion>  the coached player's champion (default: majority-vote is_self)
                  --model <id>       the Jev model to ask (default jev-1.13.0)
                  --serve <port>     SSE stream of coaching feedback for the dashboard's
                                     coaching panel (default 8724; 0 disables)
                  --kinds <a,b|all>  event kinds passed to the policy (default: the ones the coach uses)

                The Jev API key is read from this project's user secrets (entry "Jev"):
                  dotnet user-secrets set Jev <key> --project src/MindControl
                or, failing that, from the TYPESAFE_API_KEY environment variable.

                Button presses need the ability HUD read; steps need the threat stage; aim
                remarks need the skillshot stage. All three come from a spectral-sight run
                made with --coach; walking a player who stands still to lane needs a
                world-calibrated feed, and putting a point into an ability needs the
                ability HUD too, whose level-up chevrons say when a point is waiting. On
                a feed without them the coach says so once and asks only about what it
                can see.
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
    // The handler can fire again while Main is already unwinding (a second
    // Ctrl+C, or dotnet watch forwarding the signal) — after the using has
    // disposed the source.
    try { cts.Cancel(); } catch (ObjectDisposedException) { }
};

// The key lives in user secrets, not the environment; the client only knows
// the environment on its own, so it is handed over explicitly.
var secrets = new ConfigurationBuilder().AddUserSecrets(typeof(Program).Assembly, optional: true).Build();
using var jev = OpenJev(secrets["Jev"], model);
if (jev is null)
    return 2;

var feed = new FeedClient(feedUri, kinds);
var options = new ReactorOptions { ScreenWidth = screenWidth, ScreenHeight = screenHeight };
using var trace = tracePath is null ? null : new GhostTrace(tracePath, screenWidth, screenHeight);
using TextWriter? log = logPath is null ? null : new StreamWriter(logPath, append: true) { AutoFlush = true };
using var audit = auditPath is null ? null : new JevAudit(auditPath);
using var recording = recordPath is null
    ? null
    : GhostRecording.Append(recordPath, screenWidth, screenHeight, playerAnchor, minimap);
// One policy owns everything -- hands and feet -- because they are one set
// of questions about one moment, and the model answers them together.
var policy = new JevPolicy(jev, new JevOptions { SelfChampion = selfChampion },
    audit is null ? null : audit.Write);
using var coach = servePort == 0 ? null : new CoachServer(servePort, model);
if (coach is not null)
    policy.AskingChanged += coach.PublishAsking;
var reactor = new Reactor(feed, policy, options, log, trace, coach, recording);

try
{
    Console.WriteLine($"coaching against {feedUri} with {model} — feedback to the console" +
        (logPath is null ? "" : $" and {logPath}") +
        (coach is null ? "" : $", served at http://localhost:{servePort}/stream") +
        (recording is null ? "" : $"; ghost input recorded to {recordPath}, opened with {recording.Header}, "
            + $"walks clicked on the minimap at {recording.Minimap}") +
        (auditPath is null ? "" : $"; questions and answers to {auditPath}") +
        "; no input is sent anywhere");
    await reactor.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // Ctrl-C: clean shutdown.
}
return 0;

static JevClient? OpenJev(string? apiKey, string model)
{
    try
    {
        return new JevClient(new JevClientOptions
        {
            ApiKey = apiKey,
            DefaultModel = model,
            Timeout = TimeSpan.FromSeconds(2),
            SerializerOptions = Moment.JsonOptions,
        });
    }
    catch (JevConfigurationException e)
    {
        Console.Error.WriteLine($"jev: {e.Message}");
        Console.Error.WriteLine("set the key with: dotnet user-secrets set Jev <key> --project src/MindControl");
        return null;
    }
}
