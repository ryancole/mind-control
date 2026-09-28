using System.Net;
using System.Text.Json;
using MindControl.Feed;
using MindControl.Policy;
using Misdirection.Client;

namespace MindControl;

/// <summary>
/// Publishes the coaching feedback as a local SSE stream (`/stream`) so a
/// browser — e.g. the spectral-sight dashboard's coaching panel — can show it
/// live. Strictly output: it serves what the reactor already decided and
/// accepts nothing back; the observe-and-advise boundary is unchanged.
/// Every line that is advice (a cue, a key, a step) carries <c>model</c>, the
/// Jev model whose answer it is, so a panel can mark what the model said
/// apart from what the code reports (status, roster). A key or a step also
/// carries <c>input</c>: the misdirection frames the ghost
/// recording wrote for it, as data (<see cref="GhostRecording.AsData(IEnumerable{Message})"/>),
/// or null when nothing was recorded. Any icon for the hand is the panel's.
/// An <c>asking</c> line says which questions are on their way to Jev right
/// now (<c>occasions</c>, empty when none), for a panel's "thinking" light;
/// it is state, not advice, so it carries no <c>model</c>.
/// <para>The brain view is served here too: <c>/</c> is the page
/// (<c>Brain/brain.html</c>, embedded) and <c>/brain</c> its stream, every
/// line <c>/stream</c> carries plus a <c>thought</c> line per question the
/// coach asked (<see cref="Thought"/>). Thoughts stay off <c>/stream</c>, so
/// the dashboard's panel reads what it always has.</para>
/// </summary>
public sealed class CoachServer : IDisposable
{
    private const int ReplayCount = 32;

    /// <summary>How many thoughts a brain view that connects late is sent: a minute of roots, give or take.</summary>
    private const int ThoughtReplayCount = 240;

    private readonly string _model;
    private readonly HttpListener _listener = new();
    private readonly List<(StreamWriter Writer, bool Brain)> _clients = [];
    private readonly Queue<string> _replay = new();
    private readonly Queue<string> _thoughts = new();
    private string? _asking;
    private readonly Lock _lock = new();
    private readonly byte[] _page = BrainPage();

    public CoachServer(int port, string model)
    {
        _model = model;
        // localhost (not 127.0.0.1): the one prefix http.sys grants without
        // elevation or a urlacl reservation.
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    /// <summary>
    /// Coaching in words only -- see <see cref="Policy.CoachCue"/>. The
    /// dashboard writes it to the log.
    /// </summary>
    public void PublishCue(CoachCue cue, int? gameTime) =>
        Publish(new
        {
            T = "cue", VideoTime = cue.VideoTime, GameTime = gameTime,
            Priority = cue.Priority, Reason = cue.Reason, Model = _model,
        });

    /// <summary>
    /// A key the coach would have pressed. Carries the key on its own (the
    /// chord as the player would name it: "Q", "Ctrl+Q") and the full sentence
    /// as <c>reason</c>, so a panel that only knows how to print a reason
    /// still prints the right thing.
    /// </summary>
    public void PublishKey(KeyPress key, int? gameTime, IReadOnlyList<Message>? input = null) =>
        Publish(new
        {
            T = "key", VideoTime = key.VideoTime, GameTime = gameTime,
            Key = key.Chord, Priority = key.Priority, Reason = key.Sentence, Model = _model,
            Input = Data(input),
        });

    /// <summary>
    /// A step the coach would have taken. Like a key it carries the full
    /// sentence as <c>reason</c>; the direction rides alongside as a name and
    /// a screen-space unit vector. A sidestep is on the ground in front of
    /// the player and carries no place on the map; a walk carries the place
    /// it goes to as <c>destination</c>, and an attack the enemy it is on as
    /// <c>target</c>; <c>attack_move</c> is true for a walk ordered as an
    /// attack-move.
    /// </summary>
    public void PublishStep(MoveStep step, int? gameTime, IReadOnlyList<Message>? input = null) =>
        Publish(new
        {
            T = "step", VideoTime = step.VideoTime, GameTime = gameTime,
            Direction = step.Direction, Dx = step.Dx, Dy = step.Dy, Destination = step.Destination, Target = step.Target,
            AttackMove = step.AttackMove,
            Priority = step.Priority, Reason = step.Sentence, Model = _model,
            Input = Data(input),
        });

    private static IReadOnlyList<object>? Data(IReadOnlyList<Message>? input) =>
        input is null ? null : GhostRecording.AsData(input);

    public void PublishStatus(string state, string? reason = null) =>
        Publish(new { T = "status", State = state, Reason = reason });

    /// <summary>A team's locked five, for the dashboard's header — state, not advice.</summary>
    public void PublishRoster(GameEvent evt) =>
        Publish(new
        {
            T = "roster", VideoTime = evt.VideoTime, GameTime = evt.GameTime,
            Team = evt.Team, Champions = evt.Champions,
        });

    /// <summary>
    /// The questions on their way to Jev, by occasion, oldest first. It
    /// changes several times a second, so it is kept out of the replay (it
    /// would crowd out the roster); a client that connects is sent the latest
    /// one instead, after the replay.
    /// </summary>
    public void PublishAsking(IReadOnlyList<string> occasions) =>
        Publish(new { T = "asking", Occasions = occasions }, replay: false);

    /// <summary>
    /// A question the coach asked, or its answer, for the brain view only.
    /// Written with the model's own wire options (snake_case, nulls left
    /// out), since it carries the state the model was shown.
    /// </summary>
    public void PublishThought(Thought thought)
    {
        var data = $"data: {JsonSerializer.Serialize(new ThoughtLine(thought), Moment.JsonOptions)}\n\n";
        lock (_lock)
        {
            _thoughts.Enqueue(data);
            while (_thoughts.Count > ThoughtReplayCount)
                _thoughts.Dequeue();
            Send(data, brainOnly: true);
        }
    }

    /// <summary>The thought as a line: under <c>thought</c>, with a <c>t</c> like every other line.</summary>
    private sealed record ThoughtLine(Thought Thought)
    {
        public string T => "thought";
    }

    private void Publish<TLine>(TLine line, bool replay = true)
    {
        var data = $"data: {JsonSerializer.Serialize(line, FeedJson.Options)}\n\n";
        lock (_lock)
        {
            if (replay)
            {
                _replay.Enqueue(data);
                while (_replay.Count > ReplayCount)
                    _replay.Dequeue();
            }
            else
                _asking = data;
            Send(data, brainOnly: false);
        }
    }

    private void Send(string data, bool brainOnly)
    {
        // Localhost writes land in http.sys buffers; a client that has
        // gone away throws and is dropped rather than stalling the loop.
        _clients.RemoveAll(client =>
        {
            if (brainOnly && !client.Brain)
                return false;
            try { client.Writer.Write(data); return false; }
            catch (Exception e) when (e is IOException or ObjectDisposedException or HttpListenerException)
            {
                client.Writer.Dispose();
                return true;
            }
        });
    }

    private static byte[] BrainPage()
    {
        using var stream = typeof(CoachServer).Assembly.GetManifestResourceStream("MindControl.Brain.brain.html");
        if (stream is null)
            return "brain.html was not embedded in this build"u8.ToArray();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
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
            var path = context.Request.Url?.AbsolutePath;
            if (path is "/" or "/index.html")
            {
                response.ContentType = "text/html; charset=utf-8";
                response.AppendHeader("Cache-Control", "no-cache");
                try { response.Close(_page, willBlock: false); }
                catch (Exception e) when (e is IOException or HttpListenerException) { }
                continue;
            }
            if (path is not ("/stream" or "/brain"))
            {
                response.StatusCode = 404;
                response.Close();
                continue;
            }
            var brain = path == "/brain";

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
                    if (brain)
                        foreach (var line in _thoughts)
                            writer.Write(line);
                    if (_asking is not null)
                        writer.Write(_asking);
                    _clients.Add((writer, brain));
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
            foreach (var (client, _) in _clients)
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
