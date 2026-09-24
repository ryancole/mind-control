using System.Text.Json;
using MindControl.Feed;
using MindControl.Policy;

namespace MindControl;

/// <summary>
/// Records the ghost's cursor path and key presses as JSONL keyed by video_time,
/// so a run can be replayed visually over the timeline that produced it
/// (etc/ghost-viewer.html, which draws the cursor and ignores the keys).
/// The header carries the minimap rect and world bounds the run used, letting
/// the viewer invert screen pixels back onto the map.
/// </summary>
public sealed class GhostTrace(string path, MinimapRect minimap, ushort screenWidth, ushort screenHeight) : IDisposable
{
    private readonly StreamWriter _writer = new(path) { AutoFlush = true };

    public void WriteMeta(Meta meta) => Write(new
    {
        T = "meta",
        Minimap = minimap,
        Screen = new { Width = screenWidth, Height = screenHeight },
        WorldBounds = meta.WorldBounds,
        Source = meta.Source,
    });

    public void WriteMove(double videoTime, GhostCursor cursor) => Write(new
    {
        T = "move",
        VideoTime = videoTime,
        X = cursor.X,
        Y = cursor.Y,
    });

    public void WriteGlance(GlanceNote note) => Write(new
    {
        T = "glance",
        VideoTime = note.VideoTime,
        X = note.X,
        Y = note.Y,
        Priority = note.Priority,
        Reason = note.Reason,
    });

    /// <summary>When a key was pressed: the timing the .msdr recording cannot carry.</summary>
    public void WriteKey(KeyPress key) => Write(new
    {
        T = "key",
        VideoTime = key.VideoTime,
        Key = key.Key,
        Priority = key.Priority,
        Reason = key.Reason,
    });

    private void Write<TLine>(TLine line) =>
        _writer.WriteLine(JsonSerializer.Serialize(line, FeedJson.Options));

    public void Dispose() => _writer.Dispose();
}
