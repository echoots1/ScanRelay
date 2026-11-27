namespace ScanRelay.Installer;

public enum SetupMode
{
    /// <summary>No mode given: install, or the maintenance screen when already installed.</summary>
    Auto,
    Modify,
    Uninstall,

    /// <summary>Straight to the Modules step of an existing install. The admin screen's button starts this.</summary>
    Modules,

    /// <summary>The icon by the clock: open the admin screen, and start, stop or restart the service.</summary>
    Tray,

    /// <summary>Open the admin screen in the browser, starting the service first if it is not running. The desktop icon starts this.</summary>
    Open,

    /// <summary>Start, stop or restart the service, then exit.</summary>
    Service,

    /// <summary>Say whether the service is running, by exit code: 0 running, 3 not running, 4 not installed.</summary>
    Status,
}

/// <summary>How setup was started.</summary>
/// <param name="Quiet">No windows: do the work, write to the console, set the exit code.</param>
/// <param name="AnswersFile">Quiet mode: a JSON file of <see cref="SetupAnswers"/>.</param>
/// <param name="KeepData">Uninstall: leave the data folder in place.</param>
/// <param name="Error">What is wrong with the command line, or null.</param>
/// <param name="ServiceVerb">With <see cref="SetupMode.Service"/>: start, stop or restart.</param>
public sealed record CommandLine(SetupMode Mode, bool Quiet, string? AnswersFile, bool KeepData, bool Help, string? Error, string? ServiceVerb = null)
{
    /// <summary>The modes that change nothing about the install, so they need no administrator to begin with.</summary>
    public bool IsEverydayUse => Mode is SetupMode.Tray or SetupMode.Open or SetupMode.Service or SetupMode.Status;

    public const string Usage = """
        ScanRelaySetup.exe                              Install, or change an existing install.
        ScanRelaySetup.exe /modify                      Change, repair or remove an existing install.
        ScanRelaySetup.exe /modules                     Add, update or remove printer modules on an existing install.
        ScanRelaySetup.exe /uninstall                   Remove ScanRelay.
        ScanRelaySetup.exe /quiet /answers <file.json>  Install, repair or update with no windows.
        ScanRelaySetup.exe /quiet /uninstall [/keepdata]
        Quiet mode exits with 0 when it worked and 1 when it did not. The log is %ProgramData%\ScanRelay-setup.log.

        Everyday use, once installed:
        ScanRelaySetup.exe /open                        Open the admin screen, starting the service first if it is stopped.
        ScanRelaySetup.exe /tray                        Show the ScanRelay icon by the clock.
        ScanRelaySetup.exe /service start|stop|restart  Windows asks for an administrator.
        ScanRelaySetup.exe /status                      Exit code 0: running. 3: not running. 4: not installed.
        """;

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var mode = SetupMode.Auto;
        var quiet = false;
        var keepData = false;
        var help = false;
        string? answers = null;
        string? error = null;
        string? verb = null;
        var modes = 0;

        // Every argument is read even after a mistake, so /quiet is honoured and the mistake goes to the console, not a window.
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            var isSwitch = arg.StartsWith('/') || arg.StartsWith('-');
            switch (isSwitch ? arg.TrimStart('/', '-').ToLowerInvariant() : "")
            {
                case "quiet" or "q" or "silent":
                    quiet = true;
                    break;
                case "modify":
                    modes++;
                    if (mode == SetupMode.Uninstall) error ??= "/modify and /uninstall cannot be used together.";
                    if (mode == SetupMode.Modules) error ??= "/modules cannot be used with /modify or /uninstall.";
                    mode = SetupMode.Modify;
                    break;
                case "modules":
                    modes++;
                    if (mode != SetupMode.Auto) error ??= "/modules cannot be used with /modify or /uninstall.";
                    mode = SetupMode.Modules;
                    break;
                case "uninstall":
                    modes++;
                    if (mode == SetupMode.Modify) error ??= "/modify and /uninstall cannot be used together.";
                    if (mode == SetupMode.Modules) error ??= "/modules cannot be used with /modify or /uninstall.";
                    mode = SetupMode.Uninstall;
                    break;
                case "tray" or "open" or "status":
                    modes++;
                    if (mode != SetupMode.Auto) error ??= $"{arg} cannot be used with another mode.";
                    mode = Enum.Parse<SetupMode>(arg.TrimStart('/', '-'), ignoreCase: true);
                    break;
                case "service":
                    modes++;
                    if (mode != SetupMode.Auto) error ??= "/service cannot be used with another mode.";
                    mode = SetupMode.Service;
                    if (i + 1 < args.Count && args[i + 1].ToLowerInvariant() is "start" or "stop" or "restart") verb = args[++i].ToLowerInvariant();
                    else error ??= "/service needs start, stop or restart after it.";
                    break;
                case "keepdata":
                    keepData = true;
                    break;
                case "answers":
                    if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith('/')) error ??= "/answers needs the path of a JSON file after it.";
                    else answers = args[++i];
                    break;
                case "?" or "h" or "help":
                    help = true;
                    break;
                default:
                    error ??= $"\"{arg}\" is not something setup understands.";
                    break;
            }
        }

        if (error is null && keepData && mode != SetupMode.Uninstall) error = "/keepdata only goes with /uninstall.";
        if (error is null && answers is not null && mode == SetupMode.Uninstall) error = "/answers cannot be used with /uninstall.";
        if (error is null && answers is not null && !quiet) error = "/answers only goes with /quiet.";
        if (error is null && quiet && mode == SetupMode.Modify) error = "/modify opens a window, so it cannot be used with /quiet. Use /quiet /answers <file.json> to change an install.";
        if (error is null && quiet && mode == SetupMode.Modules) error = "/modules opens a window, so it cannot be used with /quiet. Use /quiet /answers <file.json> to change an install.";
        if (modes > 1) error ??= "Give one of /modify, /modules, /uninstall, /tray, /open, /service and /status, not several.";
        var everyday = mode is SetupMode.Tray or SetupMode.Open or SetupMode.Service or SetupMode.Status;
        if (error is null && everyday && (quiet || answers is not null || keepData)) error = "/tray, /open, /service and /status go with nothing else.";
        return new CommandLine(mode, quiet, answers, keepData, help, error, verb);
    }
}
