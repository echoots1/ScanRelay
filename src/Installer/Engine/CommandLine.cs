namespace ScanRelay.Installer;

public enum SetupMode
{
    /// <summary>No mode given: install, or the maintenance screen when already installed.</summary>
    Auto,
    Modify,
    Uninstall,
}

/// <summary>How setup was started.</summary>
/// <param name="Quiet">No windows: do the work, write to the console, set the exit code.</param>
/// <param name="AnswersFile">Quiet mode: a JSON file of <see cref="SetupAnswers"/>.</param>
/// <param name="KeepData">Uninstall: leave the data folder in place.</param>
/// <param name="Error">What is wrong with the command line, or null.</param>
public sealed record CommandLine(SetupMode Mode, bool Quiet, string? AnswersFile, bool KeepData, bool Help, string? Error)
{
    public const string Usage = """
        ScanRelaySetup.exe                              Install, or change an existing install.
        ScanRelaySetup.exe /modify                      Change, repair or remove an existing install.
        ScanRelaySetup.exe /uninstall                   Remove ScanRelay.
        ScanRelaySetup.exe /quiet /answers <file.json>  Install, repair or update with no windows.
        ScanRelaySetup.exe /quiet /uninstall [/keepdata]
        Quiet mode exits with 0 when it worked and 1 when it did not. The log is %ProgramData%\ScanRelay-setup.log.
        """;

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var mode = SetupMode.Auto;
        var quiet = false;
        var keepData = false;
        var help = false;
        string? answers = null;
        string? error = null;

        for (var i = 0; i < args.Count && error is null; i++)
        {
            var arg = args[i];
            var isSwitch = arg.StartsWith('/') || arg.StartsWith('-');
            switch (isSwitch ? arg.TrimStart('/', '-').ToLowerInvariant() : "")
            {
                case "quiet" or "q" or "silent":
                    quiet = true;
                    break;
                case "modify":
                    if (mode == SetupMode.Uninstall) error = "/modify and /uninstall cannot be used together.";
                    mode = SetupMode.Modify;
                    break;
                case "uninstall":
                    if (mode == SetupMode.Modify) error = "/modify and /uninstall cannot be used together.";
                    mode = SetupMode.Uninstall;
                    break;
                case "keepdata":
                    keepData = true;
                    break;
                case "answers":
                    if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1])) error = "/answers needs the path of a JSON file after it.";
                    else answers = args[++i];
                    break;
                case "?" or "h" or "help":
                    help = true;
                    break;
                default:
                    error = $"\"{arg}\" is not something setup understands.";
                    break;
            }
        }

        if (error is null && keepData && mode != SetupMode.Uninstall) error = "/keepdata only goes with /uninstall.";
        if (error is null && answers is not null && mode == SetupMode.Uninstall) error = "/answers cannot be used with /uninstall.";
        if (error is null && answers is not null && !quiet) error = "/answers only goes with /quiet.";
        if (error is null && quiet && mode == SetupMode.Modify) error = "/modify opens a window, so it cannot be used with /quiet. Use /quiet /answers <file.json> to change an install.";
        return new CommandLine(mode, quiet, answers, keepData, help, error);
    }
}
