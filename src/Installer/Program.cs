using ScanRelay.Core.Modules;

namespace ScanRelay.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var line = CommandLine.Parse(args);
        var environment = new SetupEnvironment
        {
            AppVersion = ModuleHost.AppVersion,
            SetupExePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ScanRelaySetup.exe"),
            OpenEmbeddedPayload = () => typeof(Program).Assembly.GetManifestResourceStream(SetupEngine.PayloadName),
            Console = line.Quiet ? QuietConsole() : null,
        };

        if (line.IsEverydayUse && line.Error is null) return RunEveryday(line);
        return line.Quiet ? RunQuiet(line, environment) : RunWizard(line, environment);
    }

    /// <summary>The tray icon and the desktop icon. Nothing here changes the install.</summary>
    private static int RunEveryday(CommandLine line)
    {
        switch (line.Mode)
        {
            case SetupMode.Status:
                return ServiceControl.ExitCode(ServiceControl.State());

            case SetupMode.Service:
            {
                var problem = Everyday.ControlService(line.ServiceVerb!, IsElevated());
                if (problem is null) return 0;
                if (IsElevated()) new SetupLog(SetupEnvironment.DefaultLogPath, null, null, 0).Line(problem);
                return 1;
            }

            case SetupMode.Open:
            {
                ApplicationConfiguration.Initialize();
                Everyday.EnsureTray();
                var problem = Everyday.Open(IsElevated());
                if (problem is null) return 0;
                MessageBox.Show(problem, Everyday.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            default:
            {
                using var once = new Mutex(initiallyOwned: true, TrayIcon.MutexName, out var first);
                if (!first) return 0;
                ApplicationConfiguration.Initialize();
                using var tray = new TrayIcon(IsElevated());
                Application.Run(tray);
                return 0;
            }
        }
    }

    /// <summary>No windows at all: for automated installs and tests. The exit code says whether it worked.</summary>
    private static int RunQuiet(CommandLine line, SetupEnvironment environment)
    {
        var engine = new SetupEngine(environment);
        try
        {
            if (line.Error is not null) throw new SetupException(line.Error + Environment.NewLine + CommandLine.Usage);
            if (line.Help)
            {
                Console.Out.WriteLine(CommandLine.Usage);
                return 0;
            }

            if (!IsElevated()) throw new SetupException(NotElevated);

            // On a worker thread, so nothing in the engine can wait on this one.
            var result = Task.Run(() => line.Mode == SetupMode.Uninstall
                ? engine.UninstallAsync(line.KeepData)
                : engine.ApplyAsync(QuietAnswers(line))).GetAwaiter().GetResult();
            Console.Out.WriteLine(result.Warnings.Count == 0 ? "ScanRelay setup finished." : $"ScanRelay setup finished with {result.Warnings.Count} warning(s).");
            return 0;
        }
        catch (Exception e)
        {
            var reason = e is SetupException ? e.Message : e.ToString();
            Console.Error.WriteLine("ScanRelay setup failed: " + reason);
            Console.Error.WriteLine("The full log is " + environment.LogPath);
            new SetupLog(environment.LogPath, null, null, 0).Line("ScanRelay setup failed: " + e);
            return 1;
        }
        finally
        {
            Console.Out.Flush();
            Console.Error.Flush();
        }
    }

    private const string NotElevated = "ScanRelay setup must be run as an administrator. It creates an account, a share and a Windows service.";

    /// <summary>
    /// Setup starts as whoever ran it, because the same program is also the tray icon and the
    /// desktop icon, which must not ask for an administrator each time. Anything that changes
    /// the install checks this first.
    /// </summary>
    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static SetupAnswers QuietAnswers(CommandLine line)
    {
        if (line.AnswersFile is null)
        {
            // With no file: the defaults for a new install, or a repair of what is there.
            return InstalledState.Detect()?.Answers ?? new SetupAnswers();
        }

        if (!File.Exists(line.AnswersFile)) throw new SetupException($"The answers file {Path.GetFullPath(line.AnswersFile)} was not found.");
        return SetupAnswers.FromJson(File.ReadAllText(line.AnswersFile));
    }

    private static TextWriter QuietConsole()
    {
        Native.AttachParentConsole();
        var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(writer);
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        return writer;
    }

    private static int RunWizard(CommandLine line, SetupEnvironment environment)
    {
        ApplicationConfiguration.Initialize();
        const string title = "ScanRelay Setup";
        if (line.Error is not null || line.Help)
        {
            MessageBox.Show((line.Error is null ? "" : line.Error + "\n\n") + CommandLine.Usage, title, MessageBoxButtons.OK, line.Error is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return line.Error is null ? 0 : 1;
        }

        if (!IsElevated())
        {
            // Setup itself needs an administrator. Start again as one: Windows asks, as it does for any installer.
            try
            {
                using var again = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(environment.SetupExePath, string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a => a.Contains(' ') ? $"\"{a}\"" : a)))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                if (again is null) return 1;
                again.WaitForExit();
                return again.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show(NotElevated, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }

        var engine = new SetupEngine(environment);
        var state = InstalledState.Detect();
        if (state is null && line.Mode != SetupMode.Auto)
        {
            MessageBox.Show("ScanRelay is not installed on this server, so there is nothing to change or remove.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        var flow = line.Mode switch
        {
            SetupMode.Uninstall => WizardFlow.Uninstall,
            SetupMode.Modify => WizardFlow.Maintenance,
            SetupMode.Modules => WizardFlow.Modules,
            _ when state is null => WizardFlow.Install,
            _ when engine.IsUpdate(state) => WizardFlow.Update,
            _ => WizardFlow.Maintenance,
        };

        using var form = new WizardForm(new WizardModel(engine, state, flow));
        Application.Run(form);
        return form.Succeeded ? 0 : 1;
    }
}
