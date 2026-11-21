using System.Diagnostics;
using System.Text;

namespace ScanRelay.Installer;

public sealed record CommandResult(string Command, int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>A Windows tool said no. The message carries the whole command and everything it printed.</summary>
public sealed class CommandException(CommandResult result)
    : Exception($"The command failed with exit code {result.ExitCode}: {result.Command}{Environment.NewLine}{result.Output}".TrimEnd())
{
    public CommandResult Result { get; } = result;
}

/// <summary>The one place setup runs other programs.</summary>
public static class Cmd
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    /// <summary>Runs a Windows tool from System32 and throws, with its output, unless it exits with 0.</summary>
    public static CommandResult Run(SetupLog log, string tool, string arguments)
    {
        var result = TryRun(log, tool, arguments);
        return result.Succeeded ? result : throw new CommandException(result);
    }

    /// <summary>Runs a tool where a failure is something the caller decides about.</summary>
    /// <remarks>
    /// The arguments are one string, quoted by the caller, because sc.exe and netsh.exe
    /// each read their command line in their own way.
    /// </remarks>
    public static CommandResult TryRun(SetupLog log, string tool, string arguments)
    {
        var path = Path.Combine(Environment.SystemDirectory, tool);
        var command = $"{tool} {arguments}";
        var output = new StringBuilder();
        var info = new ProcessStartInfo(path, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) => Append(output, e.Data);
            process.ErrorDataReceived += (_, e) => Append(output, e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(Limit))
            {
                process.Kill(entireProcessTree: true);
                throw new CommandException(new CommandResult(command, -1, $"Stopped after {Limit.TotalMinutes:0} minutes without finishing.\n{output}"));
            }

            // Waits for the output readers to drain as well.
            process.WaitForExit();
            var result = new CommandResult(command, process.ExitCode, output.ToString().Trim());
            log.Line($"  > {command}  (exit code {result.ExitCode})");
            if (!result.Succeeded && result.Output.Length > 0) log.Line("    " + result.Output.ReplaceLineEndings(Environment.NewLine + "    "));
            return result;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new CommandException(new CommandResult(command, -1, $"{path} could not be started: {e.Message}"));
        }
    }

    /// <summary>Wraps a value in quotes for a command line. Windows names and paths cannot themselves contain a quote.</summary>
    public static string Quote(string value) => "\"" + value.Replace("\"", "") + "\"";

    private static void Append(StringBuilder output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (output) output.AppendLine(line.TrimEnd());
    }
}
