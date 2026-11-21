using System.Globalization;

namespace ScanRelay.Installer;

/// <summary>One line of progress for whoever is watching: the wizard's Progress page or nobody.</summary>
/// <param name="Step">The step being worked on, counting from 1; 0 before the first.</param>
/// <param name="StepName">Set on the line that starts a step.</param>
public sealed record SetupProgress(int Step, int Steps, string? StepName, string Line);

/// <summary>
/// Where setup says what it is doing: always the log file, and the console and the
/// wizard when they are there. Never given a password or a key.
/// </summary>
public sealed class SetupLog(string? filePath, TextWriter? console, IProgress<SetupProgress>? progress, int steps)
{
    private readonly Lock _lock = new();
    private int _step;

    /// <summary>What needs the admin's attention but did not stop setup.</summary>
    public List<string> Warnings { get; } = [];

    public void Step(string name)
    {
        _step++;
        Write($"[{_step}/{steps}] {name}", name);
    }

    public void Line(string text) => Write(text, null);

    public void Warn(string text)
    {
        lock (_lock) Warnings.Add(text);
        Write("WARNING: " + text, null);
    }

    private void Write(string text, string? stepName)
    {
        lock (_lock)
        {
            if (filePath is not null)
            {
                try
                {
                    File.AppendAllText(filePath, $"{DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}Z {text}{Environment.NewLine}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A log that cannot be written must not stop the install it describes.
                }
            }

            console?.WriteLine(text);
        }

        progress?.Report(new SetupProgress(_step, steps, stepName, text));
    }
}
