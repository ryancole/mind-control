using System.Text.Json;
using MindControl.Feed;
using MindControl.Policy;
using Misdirection.Client;

namespace MindControl;

/// <summary>
/// Records when the coach pressed a key and when it stepped, as JSONL keyed
/// by video_time, so a run can be replayed visually over the timeline that
/// produced it (etc/ghost-viewer.html). The .msdr recording holds what the
/// hands did and the gaps between, relative to a run's first input; this is
/// the absolute time of each, in the video, with the reason beside it. Lines
/// are in the order they were decided, which for a step is after the bolt it
/// answers; a reader that wants time order sorts (the recording, being one
/// sequence, cannot, and puts such a step at no gap after what preceded it).
/// The header carries the screen size and world bounds the run used. A key
/// or a step also carries <c>input</c>, the misdirection frames the recording
/// wrote for it, as data, the gap before it included; the viewer shows them
/// beside the line and puts an icon to the hand, which is its decoration and
/// not the trace's.
/// </summary>
public sealed class GhostTrace(string path, ushort screenWidth, ushort screenHeight) : IDisposable
{
    private readonly StreamWriter _writer = new(path) { AutoFlush = true };

    public void WriteMeta(Meta meta) => Write(new
    {
        T = "meta",
        Screen = new { Width = screenWidth, Height = screenHeight },
        WorldBounds = meta.WorldBounds,
        Source = meta.Source,
    });

    /// <summary>When a key was pressed, in video time, and what the .msdr recording wrote for it (the gap before it included).</summary>
    public void WriteKey(KeyPress key, IReadOnlyList<Message>? input = null) => Write(new
    {
        T = "key",
        VideoTime = key.VideoTime,
        Key = key.Key,
        Priority = key.Priority,
        Reason = key.Reason,
        Input = input is null ? null : GhostRecording.AsData(input),
    });

    /// <summary>When and which way the coach stepped: the direction the .msdr click cannot name, and the click itself (the gap before it included).</summary>
    public void WriteStep(MoveStep step, IReadOnlyList<Message>? input = null) => Write(new
    {
        T = "step",
        VideoTime = step.VideoTime,
        Direction = step.Direction,
        Dx = step.Dx,
        Dy = step.Dy,
        Priority = step.Priority,
        Reason = step.Reason,
        Input = input is null ? null : GhostRecording.AsData(input),
    });

    private void Write<TLine>(TLine line) =>
        _writer.WriteLine(JsonSerializer.Serialize(line, FeedJson.Options));

    public void Dispose() => _writer.Dispose();
}
