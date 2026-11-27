using System.Runtime.Versioning;
using System.Text;
using ScanRelay.Core.Install;
using ScanRelay.Core.Modules;

namespace ScanRelay.Installer;

/// <summary>What the engine needs from the program around it.</summary>
public sealed class SetupEnvironment
{
    /// <summary>The version this setup installs.</summary>
    public required Version AppVersion { get; init; }

    /// <summary>The running setup program, which is copied into the program folder.</summary>
    public required string SetupExePath { get; init; }

    /// <summary>Opens the service files built into setup, or returns null for a build without them.</summary>
    public Func<Stream?> OpenEmbeddedPayload { get; init; } = () => null;

    /// <summary>Quiet mode's console. Null in the wizard.</summary>
    public TextWriter? Console { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public string LogPath { get; init; } = DefaultLogPath;

    public static string DefaultLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ScanRelay-setup.log");
}

/// <summary>A certificate already on the server that could serve the admin screen.</summary>
public sealed record CertificateChoice(string Subject, DateTime Expires, string Thumbprint);

/// <param name="Warnings">Things that did not stop setup but that the admin should read.</param>
/// <param name="LocalUrl">Where the first-run screen is, on the server itself.</param>
public sealed record SetupResult(IReadOnlyList<string> Warnings, string LocalUrl);

/// <summary>
/// Installs, repairs, updates and removes ScanRelay. It has no windows of its own: the
/// wizard and quiet mode both drive it with a <see cref="SetupAnswers"/>.
/// </summary>
public sealed class SetupEngine(SetupEnvironment environment)
{
    public const string PayloadName = "payload.zip";

    /// <summary>The steps of an install, repair or update, in order, as shown on the Progress page.</summary>
    public static readonly IReadOnlyList<string> InstallSteps =
    [
        "Stop the service and back up the database",
        "Copy the program files",
        "Create the data folder",
        "Create the printer account",
        "Install modules",
        "Set the printer account's rights on Inbox",
        "Share Inbox as a hidden share",
        "Set up certificates",
        "Write the install settings",
        "Register the Windows service",
        "Set the firewall rule",
        "Register with Windows installed apps",
        "Start the service",
    ];

    public static readonly IReadOnlyList<string> UninstallSteps =
    [
        "Remove the Windows service",
        "Remove the share",
        "Remove the firewall rule",
        "Remove the printer account",
        "Remove the certificates",
        "Remove ScanRelay from Windows installed apps",
        "Remove the program files",
        "Remove the data folder",
    ];

    public string Version => environment.AppVersion.ToString(3);

    /// <summary>True when what is installed is not this version, so applying is an update (or a downgrade).</summary>
    public bool IsUpdate(InstalledState? existing) => existing is not null && DatabaseBackup.IsNeeded(existing.Version, Version);

    /// <summary>
    /// What applying these answers will create or change on the server, in plain words.
    /// Nothing is touched: this is the summary shown before the admin confirms.
    /// </summary>
    public static string Describe(SetupAnswers answers, InstalledState? existing, string? version = null, string? machineName = null)
    {
        version ??= ModuleHost.AppVersion.ToString(3);
        machineName ??= Environment.MachineName;
        var was = existing?.Answers;
        var text = new StringBuilder();

        void Section(string title, params string?[] lines)
        {
            text.AppendLine(title);
            foreach (var line in lines.Where(l => l is not null)) text.AppendLine("  " + line);
            text.AppendLine();
        }

        if (existing is null)
        {
            text.AppendLine($"Setup will install ScanRelay {version} and make the changes below on this server.");
        }
        else if (DatabaseBackup.IsNeeded(existing.Version, version))
        {
            text.AppendLine($"Setup will update ScanRelay from {(existing.Version.Length > 0 ? existing.Version : "an unknown version")} to {version}.");
            text.AppendLine("The service is stopped, the database is backed up, the program files are replaced, installed modules are brought to matching versions and the service is started again.");
        }
        else
        {
            text.AppendLine($"Setup will repair or change ScanRelay {version}. Everything below is checked and put right; what is already right is left as it is.");
        }

        text.AppendLine("Nothing is changed until you confirm.");
        text.AppendLine();

        var installMoved = was is not null && !WinPath.Same(was.InstallFolder, answers.InstallFolder);
        Section(
            "Program files",
            $"{(existing is null ? "Copy the program to" : "Replace the program files in")} {WinPath.Normalise(answers.InstallFolder)}",
            installMoved ? $"Move the installed modules from {WinPath.Normalise(was!.InstallFolder)} and remove that folder." : null,
            "The service can read this folder but cannot write to it.");

        var dataMoved = was is not null && !WinPath.Same(was.DataFolder, answers.DataFolder);
        Section(
            "Data folder",
            dataMoved
                ? $"Move the data folder from {WinPath.Normalise(was!.DataFolder)} to {WinPath.Normalise(answers.DataFolder)}."
                : $"{(existing is null ? "Create" : "Keep")} the hidden data folder {WinPath.Normalise(answers.DataFolder)} with Inbox, Store, Logs, Keys and Backups inside.",
            "Only the service and administrators can open it. Inheritance from the folder above is turned off.",
            existing is not null && DatabaseBackup.IsNeeded(existing.Version, version)
                ? $"Back up the database to {WinPath.Combine(dataMoved ? was!.DataFolder : answers.DataFolder, "Backups")} first."
                : null);

        var accountChanged = was is not null && !was.PrinterAccount.Equals(answers.PrinterAccount, StringComparison.OrdinalIgnoreCase);
        Section(
            "Local account",
            was is null || accountChanged
                ? $"Create the local account \"{answers.PrinterAccount}\" for printers to save scans with."
                : $"Keep the local account \"{answers.PrinterAccount}\" and its password.",
            accountChanged ? $"Delete the old account \"{was!.PrinterAccount}\". Printers get the new account with the next address book push." : null,
            "Its password is generated by setup and stored encrypted. It is never shown.",
            "The account belongs to no groups and cannot sign in at the server or through Remote Desktop.");

        var shareChanged = was is not null && !was.ShareName.Equals(answers.ShareName, StringComparison.OrdinalIgnoreCase);
        Section(
            "Hidden share",
            $"Share {answers.InboxFolder} as \\\\{machineName}\\{answers.ShareName}.",
            shareChanged ? $"Remove the old share {was!.ShareName}." : null,
            $"Only \"{answers.PrinterAccount}\" can use the share. It can create and write files, but not list, read or delete them, unless an installed module needs more.",
            "Store, the database and the logs are never shared.");

        Section(
            "Certificates",
            answers.WebCertThumbprint is { } picked
                ? $"Use the certificate already on this server with thumbprint {SetupAnswers.NormaliseThumbprint(picked)} for the admin screen, and let the service read its key."
                : $"Create a self-signed certificate for the admin screen, for the names {answers.HostName}, {machineName.ToLowerInvariant()} and localhost, unless the one setup made earlier is still good. Browsers will warn about it until it is trusted or replaced.",
            existing?.GraphCertThumbprint is null
                ? "Create the certificate ScanRelay signs in to Microsoft 365 with (self-signed, 2 years, key cannot be exported)."
                : answers.RenewGraphCertificate
                    ? "Replace the certificate ScanRelay signs in to Microsoft 365 with. Sign-in to Microsoft 365 stops until the new certificate is uploaded in the admin screen."
                    : "Keep the certificate ScanRelay signs in to Microsoft 365 with.");

        Section(
            "Windows service",
            $"{(existing is null ? "Register" : "Update")} the service \"{ServiceAccount.ServiceName}\", running as its own low-privilege account {ServiceAccount.AccountName}.",
            "It starts automatically with Windows and restarts itself after a failure.");

        Section(
            "Firewall",
            answers.AddFirewallRule
                ? $"Add the inbound rule \"{SetupAnswers.FirewallRuleName}\" for TCP port {answers.Port}."
                : $"No firewall rule is added{(was is { AddFirewallRule: true } ? $", and the rule \"{SetupAnswers.FirewallRuleName}\" is removed" : "")}. The admin screen can then only be reached from the server itself unless IT opens port {answers.Port}.",
            "Setup does not change who can reach the share. Limiting it to the printer network is a firewall task for IT.");

        Section(
            "Icons",
            answers.DesktopShortcut
                ? "Put a ScanRelay icon on the desktop for everyone who signs in to this server. It opens the admin screen and starts the service first if it is stopped."
                : $"No desktop icon{(was is { DesktopShortcut: true } ? ", and the one that is there is removed" : "")}.",
            answers.TrayIcon
                ? "Show a ScanRelay icon by the clock from each person's next sign-in, to open the admin screen and to start, stop or restart the service."
                : $"No icon by the clock{(was is { TrayIcon: true } ? ", and it no longer starts at sign-in" : "")}.");

        Section(
            "Admin screen",
            $"{answers.Url}",
            answers.ReopenSetup ? "The local setup screen is reopened: until sign-in is set up again, the admin screen answers only on this server, without sign-in." : null);

        var modules = new List<string?>
        {
            answers.Modules.Count > 0 ? $"From the project's list on GitHub ({answers.Repository}), checked against the project's signature: {string.Join(", ", answers.Modules)}." : null,
            answers.OfflineModuleFiles.Count > 0 ? $"Official modules from files, checked against the project's signature: {string.Join(", ", answers.OfflineModuleFiles)}." : null,
            answers.CustomModuleFiles.Count > 0 ? $"CUSTOM modules, which are not from the project and have not been checked by it: {string.Join(", ", answers.CustomModuleFiles)}." : null,
            answers.RemoveModules.Count > 0 ? $"Remove: {string.Join(", ", answers.RemoveModules)}." : null,
        };
        if (modules.All(m => m is null)) modules.Add("No printer modules are installed or removed. They can be added later by running setup again.");
        Section("Printer modules", [.. modules]);

        Section(
            "Updates",
            answers.UpdateCheck
                ? "ScanRelay may check GitHub once a day for a new release and tell the admins. It never updates itself."
                : "ScanRelay will not check GitHub for updates. Watch the project's releases page instead.");

        return text.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>The backup file an update would write, for the given moment.</summary>
    public static string BackupPath(string dataFolder, string? oldVersion, DateTime nowUtc) =>
        WinPath.Combine(WinPath.Combine(dataFolder, "Backups"), DatabaseBackup.FileName(oldVersion, nowUtc));

    /// <summary>Opens the service files: those built in, or <c>payload.zip</c> beside setup.</summary>
    public Stream OpenPayload()
    {
        if (environment.OpenEmbeddedPayload() is { } embedded) return embedded;
        var beside = Path.Combine(Path.GetDirectoryName(environment.SetupExePath) ?? ".", PayloadName);
        if (File.Exists(beside)) return File.OpenRead(beside);
        throw new SetupException(
            $"This copy of setup has no program files inside it (a development build), and there is no {PayloadName} beside it. "
            + $"Build setup with -p:PayloadZip=<zip of the published service>, or put {PayloadName} in {Path.GetDirectoryName(environment.SetupExePath)}.");
    }

    /// <summary>
    /// Before an update: what it would mean for the installed modules. Changes nothing.
    /// </summary>
    public async Task<IReadOnlyList<string>> CheckModulesAsync(SetupAnswers answers, InstalledState existing, CancellationToken ct = default)
    {
        var log = new SetupLog(environment.LogPath, null, null, 0);
        var modules = new ModuleInstaller(Path.Combine(existing.Answers.InstallFolder, "Modules"), TrustedKeys.BuiltIn(), log, environment.Time);
        using var http = NewHttpClient();
        return await modules.PreviewUpdateAsync(http, CatalogSource(answers, InstallFile.Read(existing.Answers.InstallFolder)), answers.Modules, environment.AppVersion, ct);
    }

    /// <summary>The project's module list, for the wizard's Modules page.</summary>
    public async Task<CatalogIndex> FetchCatalogAsync(SetupAnswers answers, InstalledState? existing, CancellationToken ct = default)
    {
        using var http = NewHttpClient();
        var source = CatalogSource(answers, existing is null ? null : InstallFile.Read(existing.Answers.InstallFolder));
        try
        {
            return await new ModuleCatalog(http, source, TrustedKeys.BuiltIn()).FetchAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModuleException("GitHub did not answer in time.");
        }
    }

    /// <summary>The certificates an admin could pick for the admin screen: those in the machine store with a private key.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<CertificateChoice> ListWebCertificates()
    {
        var choices = new List<CertificateChoice>();
        foreach (var certificate in WindowsSteps.ListWebCertificateChoices())
        {
            using (certificate)
            {
                var subject = certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, forIssuer: false);
                choices.Add(new CertificateChoice(string.IsNullOrEmpty(subject) ? certificate.Subject : subject, certificate.NotAfter, certificate.Thumbprint));
            }
        }

        return [.. choices.OrderBy(c => c.Subject, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The modules in an existing install's program folder. Empty when the record cannot be read.</summary>
    public static IReadOnlyList<InstalledModule> InstalledModules(InstalledState? existing)
    {
        if (existing is null) return [];
        try
        {
            return new ModuleStore(Path.Combine(existing.Answers.InstallFolder, "Modules"), TrustedKeys.BuiltIn()).Read();
        }
        catch (ModuleException)
        {
            return [];
        }
    }

    /// <summary>
    /// Installs, repairs or updates. Every step can be run again, so running setup a
    /// second time over a half-finished install completes it.
    /// </summary>
    public Task<SetupResult> ApplyAsync(SetupAnswers answers, IProgress<SetupProgress>? progress = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ScanRelay setup only runs on Windows.");
        var problems = answers.Validate();
        if (problems.Count > 0) throw new SetupException("The answers are not valid. " + string.Join(" ", problems));
        return ApplyOnWindowsAsync(answers.Clone(), progress, ct);
    }

    /// <summary>Removes everything setup created. The data folder is only kept when asked.</summary>
    public Task<SetupResult> UninstallAsync(bool keepData, IProgress<SetupProgress>? progress = null, CancellationToken ct = default)
    {
        return Task.Run(
            () => OperatingSystem.IsWindows()
                ? UninstallOnWindows(keepData, progress, ct)
                : throw new PlatformNotSupportedException("ScanRelay setup only runs on Windows."),
            ct);
    }

    [SupportedOSPlatform("windows")]
    private async Task<SetupResult> ApplyOnWindowsAsync(SetupAnswers a, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        var log = new SetupLog(environment.LogPath, environment.Console, progress, InstallSteps.Count);
        var now = environment.Time.GetUtcNow();
        a.InstallFolder = Path.GetFullPath(WinPath.Normalise(a.InstallFolder));
        a.DataFolder = Path.GetFullPath(WinPath.Normalise(a.DataFolder));
        var setupExe = Path.GetFullPath(environment.SetupExePath);

        var state = InstalledState.Detect();
        var was = state?.Answers;
        var appUpdate = IsUpdate(state);
        log.Line($"ScanRelay setup {Version} started on {Environment.MachineName}. "
            + (state is null ? "No existing install was found." : $"Found version {(state.Version.Length > 0 ? state.Version : "unknown")} in {was!.InstallFolder}."));
        log.Line("Answers: " + a.ToJson().ReplaceLineEndings(" "));

        // Opened before anything is touched, so a setup with no program files fails with the server unchanged.
        await using var payload = OpenPayload();

        InstallConfig? previousConfig = null;
        string protectedPassword = "";
        string? graphThumbprint = null;
        string? webThumbprint = null;
        var webMadeBySetup = false;
        var modules = new ModuleInstaller(a.ModulesFolder, TrustedKeys.BuiltIn(), log, environment.Time);

        await StepAsync(log, InstallSteps[0], ct, () =>
        {
            WindowsSteps.StopService(log);
            var oldData = was?.DataFolder ?? a.DataFolder;
            var database = Path.Combine(oldData, DatabaseBackup.DatabaseName);
            if (!File.Exists(database))
            {
                log.Line($"There is no database at {database} yet, so there is nothing to back up.");
            }
            else if (!DatabaseBackup.IsNeeded(state?.Version, Version))
            {
                log.Line($"Version {Version} is already installed, so the database was not backed up.");
            }
            else
            {
                var backup = BackupPath(oldData, state?.Version, now.UtcDateTime);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                foreach (var suffix in DatabaseBackup.Suffixes.Where(s => File.Exists(database + s)))
                {
                    File.Copy(database + suffix, backup + suffix, overwrite: true);
                }

                log.Line($"Backed up the database to {backup}.");
            }

            if (was is not null && !WinPath.Same(oldData, a.DataFolder) && Directory.Exists(oldData))
            {
                if (File.Exists(Path.Combine(a.DataFolder, DatabaseBackup.DatabaseName)))
                {
                    throw new SetupException($"The new data folder {a.DataFolder} already holds a ScanRelay database. Choose an empty folder, or move that one away first.");
                }

                // A shared folder cannot be moved, and the share is made again for the new place later.
                WindowsSteps.RemoveShare(log, was.ShareName, Path.Combine(oldData, "Inbox"));
                WindowsSteps.MoveFolder(log, oldData, a.DataFolder);
            }

            return Task.CompletedTask;
        });

        await StepAsync(log, InstallSteps[1], ct, () =>
        {
            Directory.CreateDirectory(a.InstallFolder);
            if (was is not null && !WinPath.Same(was.InstallFolder, a.InstallFolder) && Directory.Exists(was.InstallFolder))
            {
                // Modules and the install records are what an update keeps, so they follow the program to its new folder.
                var oldModules = Path.Combine(was.InstallFolder, "Modules");
                if (Directory.Exists(oldModules) && !Directory.Exists(a.ModulesFolder)) WindowsSteps.CopyFolder(oldModules, a.ModulesFolder);
                foreach (var name in new[] { InstallConfig.FileName, InstalledState.FileName })
                {
                    var source = Path.Combine(was.InstallFolder, name);
                    var target = Path.Combine(a.InstallFolder, name);
                    if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target);
                }

                log.Line($"Carried the modules and install records over from {was.InstallFolder}.");
            }

            previousConfig = InstallFile.Read(a.InstallFolder);
            WindowsSteps.CloseTrayIcons(log, a.InstallFolder);
            if (was is not null && !WinPath.Same(was.InstallFolder, a.InstallFolder)) WindowsSteps.CloseTrayIcons(log, was.InstallFolder);
            WindowsSteps.WaitForServiceProgramToClose(log, a.InstallFolder);

            // Each file already gets several tries. If one still cannot be written, the whole copy is tried once
            // more after a pause: whatever held it has usually let go by then, as running setup again shows.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    WindowsSteps.ClearProgramFolder(log, a.InstallFolder, setupExe);
                    WindowsSteps.ExtractPayload(log, payload, a.InstallFolder);
                    WindowsSteps.CopySetupExe(log, setupExe, a.InstallFolder);
                    break;
                }
                catch (SetupException e) when (attempt == 1 && e.InnerException is IOException or UnauthorizedAccessException)
                {
                    log.Line($"Copying the program files failed: {e.Message} Waiting 10 seconds and copying them all again, once.");
                    Thread.Sleep(TimeSpan.FromSeconds(10));
                    WindowsSteps.WaitForServiceProgramToClose(log, a.InstallFolder);
                }
            }

            Directory.CreateDirectory(a.ModulesFolder);
            return Task.CompletedTask;
        });

        await StepAsync(log, InstallSteps[2], ct, () =>
        {
            foreach (var name in new[] { "", "Inbox", "Store", "Logs", "Keys", "Backups" }) Directory.CreateDirectory(Path.Combine(a.DataFolder, name));
            log.Line($"The data folder {a.DataFolder} has Inbox, Store, Logs, Keys and Backups.");
            WindowsSteps.SecureDataFolder(log, a.DataFolder);
            return Task.CompletedTask;
        });

        await StepAsync(log, InstallSteps[3], ct, () =>
        {
            if (was is not null && !was.PrinterAccount.Equals(a.PrinterAccount, StringComparison.OrdinalIgnoreCase))
            {
                WindowsSteps.DeletePrinterAccount(log, was.PrinterAccount);
            }

            protectedPassword = WindowsSteps.EnsurePrinterAccount(log, a.PrinterAccount, previousConfig);
            return Task.CompletedTask;
        });

        await StepAsync(log, InstallSteps[4], ct, async () =>
        {
            modules.Remove(a.RemoveModules);
            var fromFiles = modules.InstallOfficialFiles(a.OfflineModuleFiles).Concat(modules.InstallCustomFiles(a.CustomModuleFiles, a.AcceptCustomModules)).ToList();
            using var http = NewHttpClient();
            await modules.InstallFromCatalogAsync(http, CatalogSource(a, previousConfig), a.Modules, a.RemoveModules, fromFiles, appUpdate, environment.AppVersion, ct);
            foreach (var warning in modules.CustomWarnings(environment.AppVersion)) log.Warn(warning);
            var installed = modules.Installed;
            log.Line(installed.Count == 0
                ? "No modules are installed."
                : "Installed modules: " + string.Join(", ", installed.Select(m => $"{m.Id} {m.Version} ({m.Status})")) + ".");
        });

        await StepAsync(log, InstallSteps[5], ct, () =>
        {
            // Done after the modules step, because the rights depend on what is now installed.
            var rights = InboxRights.For(modules.ShareRights());
            WindowsSteps.SetInboxRights(log, Path.Combine(a.DataFolder, "Inbox"), WindowsSteps.AccountSid(a.PrinterAccount), rights);
            return Task.CompletedTask;
        });

        await StepAsync(log, InstallSteps[6], ct, () =>
        {
            var inbox = Path.Combine(a.DataFolder, "Inbox");
            var ours = was is null ? Array.Empty<string>() : [Path.Combine(was.DataFolder, "Inbox")];
            if (was is not null && !was.ShareName.Equals(a.ShareName, StringComparison.OrdinalIgnoreCase))
            {
                WindowsSteps.RemoveShare(log, was.ShareName, [.. ours, inbox]);
            }

            WindowsSteps.ShareInbox(log, a.ShareName, inbox, WindowsSteps.AccountSid(a.PrinterAccount), ours);
            return Task.CompletedTask;
        });

        await StepAsync(log, InstallSteps[7], ct, () =>
        {
            graphThumbprint = EnsureGraphCertificate(log, a, state?.GraphCertThumbprint ?? previousConfig?.GraphCertThumbprint, now);
            (webThumbprint, webMadeBySetup) = EnsureWebCertificate(log, a, state, previousConfig, now);
            return Task.CompletedTask;
        });

        await StepAsync(log, InstallSteps[8], ct, () =>
        {
            var config = InstallFile.Build(a, previousConfig, graphThumbprint, webThumbprint, protectedPassword);
            config.DataFolder = a.DataFolder;
            config.ModulesFolder = a.ModulesFolder;
            WindowsSteps.WriteInstallFile(log, a.InstallFolder, config);
            if (a.ReopenSetup) log.Line("The local setup screen will be open when the service starts.");
            return Task.CompletedTask;
        });

        await StepAsync(log, InstallSteps[9], ct, () =>
        {
            WindowsSteps.RegisterService(log, a.InstallFolder);
            WindowsSteps.SecureProgramFolder(log, a.InstallFolder);
            return Task.CompletedTask;
        });

        await StepAsync(log, InstallSteps[10], ct, () =>
        {
            WindowsSteps.SetFirewallRule(log, a.AddFirewallRule, a.Port);
            return Task.CompletedTask;
        });

        await StepAsync(log, InstallSteps[11], ct, () =>
        {
            var saved = new InstalledState
            {
                Version = Version,
                Answers = a.ForSaving(modules.Installed.Where(m => m.Status == ModuleStatus.Official).Select(m => m.Id)),
                GraphCertThumbprint = graphThumbprint,
                WebCertThumbprint = webThumbprint,
                WebCertCreatedBySetup = webMadeBySetup,
                InstalledUtc = state is { InstalledUtc.Year: > 2000 } ? state.InstalledUtc : now.UtcDateTime,
                ChangedUtc = now.UtcDateTime,
            };
            saved.WriteTo(a.InstallFolder);
            log.Line($"Saved the answers for Modify and Repair in {Path.Combine(a.InstallFolder, InstalledState.FileName)}. There are no secrets in them.");
            WindowsSteps.RegisterInstalledApp(log, a.InstallFolder, Version);
            WindowsSteps.SetEverydayUse(log, a.InstallFolder, a.LocalUrl, a.DesktopShortcut, a.TrayIcon);
            return Task.CompletedTask;
        });

        await StepAsync(log, InstallSteps[12], ct, async () =>
        {
            await WindowsSteps.StartServiceAndWaitAsync(log, a, ct);
            if (was is not null && !WinPath.Same(was.InstallFolder, a.InstallFolder))
            {
                WindowsSteps.DeleteFolder(log, was.InstallFolder, setupExe);
            }
        });

        log.Line($"ScanRelay {Version} is installed and running. The admin screen is at {a.Url} (on this server: {a.LocalUrl}).");
        if (log.Warnings.Count > 0) log.Line($"Finished with {log.Warnings.Count} warning(s). They are listed above.");
        return new SetupResult([.. log.Warnings], a.LocalUrl);
    }

    [SupportedOSPlatform("windows")]
    private static string EnsureGraphCertificate(SetupLog log, SetupAnswers a, string? currentThumbprint, DateTimeOffset now)
    {
        using var current = WindowsSteps.FindCertificate(currentThumbprint);
        if (current is { HasPrivateKey: true } && !a.RenewGraphCertificate)
        {
            log.Line($"The Graph certificate {current.Thumbprint} is in place, valid until {current.NotAfter:yyyy-MM-dd}. It was kept.");
            if (current.NotAfter < now.AddDays(60)) log.Warn($"The Graph certificate expires on {current.NotAfter:yyyy-MM-dd}. Run Repair with \"Renew the Graph certificate\" before then.");
            RequireKeyAccess(log, current);
            return current.Thumbprint;
        }

        using var made = WindowsSteps.AddSelfSigned(log, "CN=ScanRelay Graph", now, null, InstalledState.GraphCertFriendlyName);
        if (current is not null) WindowsSteps.RemoveCertificate(log, current.Thumbprint);
        if (!string.IsNullOrWhiteSpace(currentThumbprint))
        {
            log.Warn((current is null ? $"The Graph certificate {currentThumbprint} was no longer in the certificate store, so a new one was made. " : "The Graph certificate was replaced. ")
                + "ScanRelay cannot sign in to Microsoft 365 until the new certificate is uploaded: open the admin screen and follow Settings.");
        }

        return made.Thumbprint;
    }

    [SupportedOSPlatform("windows")]
    private static (string Thumbprint, bool MadeBySetup) EnsureWebCertificate(SetupLog log, SetupAnswers a, InstalledState? state, InstallConfig? previousConfig, DateTimeOffset now)
    {
        // The certificate setup made last time, if any. The friendly name covers an install whose state file was lost.
        var previousThumbprint = state?.WebCertThumbprint ?? previousConfig?.WebCertThumbprint;
        using var previous = WindowsSteps.FindCertificate(previousThumbprint);
        var previousIsOurs = previous is not null && (state?.WebCertCreatedBySetup == true || previous.FriendlyName == InstalledState.WebCertFriendlyName);

        if (a.WebCertThumbprint is { } wanted)
        {
            var thumbprint = SetupAnswers.NormaliseThumbprint(wanted);
            using var picked = WindowsSteps.FindCertificate(thumbprint)
                ?? throw new SetupException($"The web certificate with thumbprint {thumbprint} is not in this server's certificate store (Local Computer, Personal). Import it there first, or choose a self-signed certificate.");
            if (!picked.HasPrivateKey)
            {
                throw new SetupException($"The web certificate {thumbprint} ({picked.Subject}) has no private key on this server, so it cannot be used for HTTPS.");
            }

            if (picked.NotAfter < now) log.Warn($"The web certificate {thumbprint} expired on {picked.NotAfter:yyyy-MM-dd}. Browsers will refuse it.");
            if (!WindowsSteps.GrantKeyRead(log, picked))
            {
                log.Warn($"Setup could not find the key file of the web certificate {thumbprint} (it may be on a hardware device). Give {ServiceAccount.AccountName} read access to its private key by hand: certlm.msc, All Tasks, Manage Private Keys.");
            }

            log.Line($"The admin screen will use the certificate {picked.Subject} ({thumbprint}), valid until {picked.NotAfter:yyyy-MM-dd}.");
            if (previousIsOurs && !previous!.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                WindowsSteps.RemoveCertificate(log, previous.Thumbprint);
            }

            return (thumbprint, false);
        }

        var names = new[] { a.HostName.ToLowerInvariant(), Environment.MachineName.ToLowerInvariant(), "localhost" }.Distinct().ToArray();
        if (previousIsOurs && previous!.HasPrivateKey && previous.NotAfter > now.AddDays(30) && names.All(n => previous.MatchesHostname(n)))
        {
            log.Line($"The self-signed web certificate {previous.Thumbprint} covers {string.Join(", ", names)} and is valid until {previous.NotAfter:yyyy-MM-dd}. It was kept.");
            RequireKeyAccess(log, previous);
            return (previous.Thumbprint, true);
        }

        using var made = WindowsSteps.AddSelfSigned(log, $"CN={a.HostName}", now, names, InstalledState.WebCertFriendlyName);
        if (previousIsOurs) WindowsSteps.RemoveCertificate(log, previous!.Thumbprint);
        return (made.Thumbprint, true);
    }

    [SupportedOSPlatform("windows")]
    private static void RequireKeyAccess(SetupLog log, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
    {
        if (!WindowsSteps.GrantKeyRead(log, certificate))
        {
            throw new SetupException($"Setup could not find the private key file of the certificate {certificate.Thumbprint} under %ProgramData%\\Microsoft\\Crypto, so it could not let the service read it.");
        }
    }

    [SupportedOSPlatform("windows")]
    private SetupResult UninstallOnWindows(bool keepData, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        var log = new SetupLog(environment.LogPath, environment.Console, progress, UninstallSteps.Count);
        var setupExe = Path.GetFullPath(environment.SetupExePath);
        var state = InstalledState.Detect();
        if (state is null)
        {
            log.Warn("No ScanRelay install was found in Windows installed apps. Setup removes whatever is left under the default names.");
            state = new InstalledState();
        }

        var a = state.Answers;
        var config = InstallFile.Read(a.InstallFolder);
        log.Line($"ScanRelay setup {Version} is removing ScanRelay from {a.InstallFolder}. The data folder {a.DataFolder} is {(keepData ? "kept" : "deleted")}.");

        // Every step is tried even when an earlier one fails, so one stuck item does not leave the rest behind.
        var failures = new List<string>();
        void Step(int index, Action work)
        {
            ct.ThrowIfCancellationRequested();
            log.Step(UninstallSteps[index]);
            try
            {
                work();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.Line($"FAILED: {UninstallSteps[index]}: {e}");
                failures.Add($"{UninstallSteps[index]}: {e.Message}");
            }
        }

        Step(0, () => WindowsSteps.DeleteService(log));
        Step(1, () => WindowsSteps.RemoveShare(log, a.ShareName, Path.Combine(a.DataFolder, "Inbox")));
        Step(2, () => WindowsSteps.SetFirewallRule(log, add: false, a.Port));
        Step(3, () => WindowsSteps.DeletePrinterAccount(log, a.PrinterAccount));
        Step(4, () =>
        {
            WindowsSteps.RemoveCertificate(log, state.GraphCertThumbprint ?? config?.GraphCertThumbprint);
            var webThumbprint = state.WebCertThumbprint ?? config?.WebCertThumbprint;
            using var web = WindowsSteps.FindCertificate(webThumbprint);
            if (web is null) return;
            if (state.WebCertCreatedBySetup || web.FriendlyName == InstalledState.WebCertFriendlyName) WindowsSteps.RemoveCertificate(log, webThumbprint);
            else log.Line($"The web certificate {web.Thumbprint} was chosen by an admin, not made by setup, so it was left in the certificate store.");
        });
        if (failures.Count > 0)
        {
            // The install records say what is ScanRelay's. They stay until everything they name is gone, so uninstall can be run again.
            log.Line("Something above could not be removed, so the program files, the data folder and the entry in Windows installed apps were left in place.");
            throw new SetupException("ScanRelay was not fully removed. " + string.Join(" ", failures) + " Put that right and run the uninstall again.");
        }

        Step(5, () =>
        {
            WindowsSteps.RemoveEverydayUse(log);
            WindowsSteps.CloseTrayIcons(log, a.InstallFolder);
            WindowsSteps.UnregisterInstalledApp(log);
        });
        Step(6, () => WindowsSteps.DeleteFolder(log, a.InstallFolder, setupExe));
        Step(7, () =>
        {
            if (keepData) log.Line($"The data folder {a.DataFolder} was kept, as asked. It still holds the scans, the database and the logs.");
            else WindowsSteps.DeleteFolder(log, a.DataFolder, setupExe);
        });

        if (failures.Count > 0)
        {
            throw new SetupException("ScanRelay was not fully removed. " + string.Join(" ", failures));
        }

        log.Line("ScanRelay has been removed.");
        return new SetupResult([.. log.Warnings], "");
    }

    private static async Task StepAsync(SetupLog log, string name, CancellationToken ct, Func<Task> work)
    {
        ct.ThrowIfCancellationRequested();
        log.Step(name);
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            log.Line($"Cancelled during: {name}.");
            throw;
        }
        catch (Exception e)
        {
            // The log gets everything; the admin gets the reason.
            log.Line($"FAILED: {name}: {e}");
            throw new SetupException($"The step \"{name}\" failed. {e.Message}", e);
        }
    }

    private static InstallConfig CatalogSource(SetupAnswers answers, InstallConfig? previous)
    {
        // Keeps cloud endpoints an existing install was pointed at, such as a test server.
        var source = InstallFile.Build(answers, previous, null, null, null);
        return source;
    }

    private static HttpClient NewHttpClient() => new() { Timeout = TimeSpan.FromSeconds(100) };
}
