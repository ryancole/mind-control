using System.Text.Json;
using MindControl.Policy;

namespace MindControl;

/// <summary>
/// Records every question put to the coach model and its answer as JSONL
/// keyed by video_time: the state the model saw, the questions as they were
/// asked, and the answers exactly as they came back. This is how the coaching
/// is audited and tuned -- a press or a step in the log traces back to a
/// probability here, and a silence to the probability that fell short -- and
/// it is the record of what the model was shown, which is the fair-play
/// boundary made inspectable.
/// </summary>
public sealed class JevAudit(string path) : IDisposable
{
    private readonly StreamWriter _writer = new(path) { AutoFlush = true };

    public void Write(Consultation consultation)
    {
        var line = new
        {
            consultation.VideoTime,
            consultation.Occasion,
            consultation.ElapsedMs,
            consultation.Response?.Model,
            consultation.Error,
            consultation.State,
            Questions = consultation.Questions.ToDictionary(
                q => q.Key, q => new { q.Value.Type, q.Value.Instructions }),
            Answers = consultation.Response?.Answers.ToDictionary(
                a => a.Key, a => a.Value.Raw.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : a.Value.Raw),
        };
        _writer.WriteLine(JsonSerializer.Serialize(line, Moment.JsonOptions));
    }

    public void Dispose() => _writer.Dispose();
}
