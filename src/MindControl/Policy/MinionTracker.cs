using MindControl.Feed;

namespace MindControl.Policy;

/// <summary>
/// Follows the minions on the player's screen from frame to frame, so that a
/// bar has a history: how fast it is falling, and so how soon it is empty.
/// The feed gives a minion no identity -- each frame's bars are a fresh list
/// -- so identity is made here, the way a viewer would: a bar is the same
/// minion as the nearest one of its side seen a moment ago, if it is near
/// enough to have walked there. Perception, not judgement: whether a falling
/// bar is a last hit to wait for or take is the coach model's call.
/// </summary>
public sealed class MinionTracker
{
    /// <summary>
    /// How far, in game units, a bar may be from where a minion was last seen
    /// and still be that minion: a minion walks about 35 units a tenth of a
    /// second, and the projection is good to about a hundred.
    /// </summary>
    public const double MatchUnits = 200;

    /// <summary>How long a minion may go unseen (a covered bar, a dropped frame) and still be followed.</summary>
    public const double ForgetAfterSeconds = 1;

    /// <summary>How far back a bar's readings are kept to measure its fall.</summary>
    public const double WindowSeconds = 1.5;

    /// <summary>The least span of readings a fall is measured over; less is one reading's noise.</summary>
    public const double MinSpanSeconds = 0.4;

    private sealed class Track(string team, double x, double y, double seen)
    {
        public string Team { get; } = team;
        public double X { get; set; } = x;
        public double Y { get; set; } = y;
        public double Seen { get; set; } = seen;
        public List<(double At, double Health)> Readings { get; } = [];
    }

    private readonly List<Track> _tracks = [];
    private readonly Dictionary<Minion, Track> _current = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Takes one frame's bars: each placed one is matched to the minion it
    /// most likely is, or starts a new one, and its reading is kept. A bar
    /// that reads fuller than a moment ago is not the same minion's (a bar
    /// only falls), so its history restarts.
    /// </summary>
    public void Update(double videoTime, Minion[]? minions)
    {
        _current.Clear();
        _tracks.RemoveAll(t => videoTime - t.Seen > ForgetAfterSeconds);
        if (minions is null)
            return;

        var placed = minions.Where(m => m is { WorldX: not null, WorldY: not null }).ToArray();
        var pairs = placed
            .SelectMany(m => _tracks
                .Where(t => t.Team == m.Team)
                .Select(t => (Minion: m, Track: t, Distance: double.Hypot(m.WorldX!.Value - t.X, m.WorldY!.Value - t.Y))))
            .Where(p => p.Distance <= MatchUnits)
            .OrderBy(p => p.Distance);
        var taken = new HashSet<Track>();
        foreach (var (minion, track, _) in pairs)
        {
            if (_current.ContainsKey(minion) || !taken.Add(track))
                continue;
            _current[minion] = track;
        }
        foreach (var minion in placed.Where(m => !_current.ContainsKey(m)))
        {
            var track = new Track(minion.Team, minion.WorldX!.Value, minion.WorldY!.Value, videoTime);
            _tracks.Add(track);
            _current[minion] = track;
        }
        foreach (var (minion, track) in _current)
        {
            track.X = minion.WorldX!.Value;
            track.Y = minion.WorldY!.Value;
            track.Seen = videoTime;
            if (minion.Health is not { } health)
                continue;
            if (track.Readings.Count > 0 && health > track.Readings[^1].Health + 0.05)
                track.Readings.Clear();
            track.Readings.Add((videoTime, health));
            track.Readings.RemoveAll(r => videoTime - r.At > WindowSeconds);
        }
    }

    /// <summary>
    /// How fast a bar of the latest frame is falling, in bar per second (0
    /// when it holds), and how many seconds until it is empty at that rate
    /// (null when it is not falling); both null when the minion has not been
    /// followed for <see cref="MinSpanSeconds"/>. A least-squares slope over
    /// the kept readings, so one misread bar moves it little.
    /// </summary>
    public (double? FallingPerSecond, double? SecondsToEmpty) Trend(Minion minion)
    {
        if (!_current.TryGetValue(minion, out var track) || track.Readings is not { Count: >= 3 } readings
            || readings[^1].At - readings[0].At < MinSpanSeconds)
            return (null, null);
        var meanT = readings.Average(r => r.At);
        var meanH = readings.Average(r => r.Health);
        var spread = readings.Sum(r => (r.At - meanT) * (r.At - meanT));
        var slope = readings.Sum(r => (r.At - meanT) * (r.Health - meanH)) / spread;
        var falling = Math.Max(0, Math.Round(-slope, 2));
        return (falling, falling > 0 ? Math.Round(readings[^1].Health / falling, 1) : null);
    }

    /// <summary>Forgets every minion: after a gap, a bar is nobody we have seen.</summary>
    public void Clear()
    {
        _tracks.Clear();
        _current.Clear();
    }
}
