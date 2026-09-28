namespace MindControl;

/// <summary>What a console line is: plain plumbing, coaching advice, or a failure.</summary>
public enum Tone { Plain, Advice, Error }

/// <summary>
/// Colours console lines by <see cref="Tone"/>: advice green, errors red.
/// Only the console is coloured -- a redirected stream, the --log file and
/// the coach stream stay plain text -- and NO_COLOR turns it off.
/// Lines come from the feed loop and from Jev's answers at once, so setting
/// the colour, writing and resetting it happen under one lock.
/// </summary>
public static class ConsoleTone
{
    private static readonly Lock Gate = new();
    private static readonly bool NoColor =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    public static void WriteLine(Tone tone, string line) => Write(Console.Out, tone, line);

    /// <summary>An error line to stderr, in red.</summary>
    public static void Error(string line) => Write(Console.Error, Tone.Error, line);

    private static void Write(TextWriter writer, Tone tone, string line)
    {
        var redirected = writer == Console.Error ? Console.IsErrorRedirected : Console.IsOutputRedirected;
        if (tone == Tone.Plain || NoColor || redirected)
        {
            lock (Gate)
                writer.WriteLine(line);
            return;
        }
        lock (Gate)
        {
            Console.ForegroundColor = tone == Tone.Error ? ConsoleColor.Red : ConsoleColor.Green;
            writer.WriteLine(line);
            Console.ResetColor();
        }
    }
}
