using Jev;
using MindControl.Policy;

namespace MindControl.Tests;

/// <summary>
/// A scripted coach model. Records every question set it is asked, answers
/// each question from <see cref="Script"/> (silence by default: every yes/no
/// a no, every look level 0, every choice its first option), and can hold its
/// answers back so a test can see what happens while a question is in flight.
/// </summary>
internal sealed class FakeJev : IJevClient
{
    public sealed record Ask(Moment State, IReadOnlyDictionary<string, Question> Questions, RequestOptions? Options);

    public readonly List<Ask> Asks = [];

    /// <summary>The answer to a question by id, or null for the silent default.</summary>
    public Func<string, Question, Answer?> Script { get; set; } = (_, _) => null;

    /// <summary>When set, answers are held until <see cref="Release"/>.</summary>
    public bool Hold { get; set; }

    /// <summary>When set, every question fails with this.</summary>
    public Exception? Fault { get; set; }

    private readonly Queue<(TaskCompletionSource<SystemOneResponse> Pending, IReadOnlyDictionary<string, Question> Questions)> _held = new();

    public int Pending => _held.Count;

    public Ask Last => Asks[^1];

    public Task<SystemOneResponse> SystemOneAsync(
        object? state, IReadOnlyDictionary<string, Question> questions, string? model = null,
        RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        Asks.Add(new Ask((Moment)state!, questions, options));
        if (Fault is not null)
            return Task.FromException<SystemOneResponse>(Fault);
        if (!Hold)
            return Task.FromResult(Respond(questions));
        var pending = new TaskCompletionSource<SystemOneResponse>();
        _held.Enqueue((pending, questions));
        return pending.Task;
    }

    public Task<SystemOneResponse> SystemOneAsync(
        SystemOneRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SystemOneAsync(request.State, request.Questions, request.Model, options, cancellationToken);

    public Task<ListModelsResponse> ListModelsAsync(RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ListModelsResponse([]));

    /// <summary>Answers everything held back, in the order it was asked.</summary>
    public void Release()
    {
        while (_held.TryDequeue(out var held))
            held.Pending.SetResult(Respond(held.Questions));
    }

    private SystemOneResponse Respond(IReadOnlyDictionary<string, Question> questions) =>
        new("jev-test", questions.ToDictionary(q => q.Key, q => Script(q.Key, q.Value) ?? Default(q.Value)), new Usage(0, 0));

    private static Answer Default(Question question) => question switch
    {
        NoulQuestion => No,
        ScoreQuestion score => Level(score, 0),
        ChoiceQuestion choice => Pick(choice, choice.Options.First()),
        _ => throw new NotSupportedException(question.Type),
    };

    public static readonly NoulAnswer Yes = new(0.95);
    public static readonly NoulAnswer No = new(0.05);

    public static ScoreAnswer Level(Question question, int level)
    {
        var levels = ((ScoreQuestion)question).Levels;
        var legend = levels.Select((l, i) => (Key: i.ToString(), Value: l.ToString()!)).ToDictionary(p => p.Key, p => p.Value);
        var probabilities = levels.Select((_, i) => (Key: i.ToString(), Value: i == level ? 1.0 : 0.0)).ToDictionary(p => p.Key, p => p.Value);
        return new ScoreAnswer(level, legend, probabilities, 1.0);
    }

    public static ChoiceAnswer Pick(Question question, string option)
    {
        var probabilities = ((ChoiceQuestion)question).Options.ToDictionary(o => o, o => o == option ? 1.0 : 0.0);
        return new ChoiceAnswer(option, probabilities, 1.0);
    }
}
