using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using ScanRelay.Core.Install;

namespace ScanRelay.Installer;

/// <summary>
/// The Windows work behind each setup step. Every method is safe to run again, says
/// what it did in the log, and throws with the reason when Windows refuses.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSteps
{
    public const string ServiceExe = "ScanRelay.Service.exe";
    public const string SetupExe = "ScanRelaySetup.exe";

    /// <summary>What an update leaves alone in the program folder.</summary>
    private static readonly string[] KeptInProgramFolder = ["Modules", InstallConfig.FileName, InstalledState.FileName];

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new(ServiceAccount.Sid("TrustedInstaller"));

    public static SecurityIdentifier ServiceSid { get; } = new(ServiceAccount.Sid());

    private const InheritanceFlags ToEverythingInside = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    // ---- Service ----

    public static bool ServiceExists()
    {
        var services = ServiceController.GetServices();
        try
        {
            return services.Any(s => s.ServiceName.Equals(ServiceAccount.ServiceName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var s in services) s.Dispose();
        }
    }

    public static void StopService(SetupLog log)
    {
        if (!ServiceExists())
        {
            log.Line("The ScanRelay service is not registered, so there is nothing to stop.");
            return;
        }

        using var service = new ServiceController(ServiceAccount.ServiceName);
        if (service.Status == ServiceControllerStatus.Stopped)
        {
            log.Line("The ScanRelay service is already stopped.");
            return;
        }

        try
        {
            if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(90));
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            throw new SetupException("The ScanRelay service did not stop within 90 seconds. Stop it in Services, then run setup again.");
        }
        catch (InvalidOperationException e)
        {
            throw new SetupException($"The ScanRelay service could not be stopped: {e.InnerException?.Message ?? e.Message}", e);
        }

        log.Line("Stopped the ScanRelay service.");
    }

    public static void RegisterService(SetupLog log, string installFolder)
    {
        var exe = Path.Combine(installFolder, ServiceExe);

        // The inner quotes are stored with the path, so a folder with spaces in it cannot be misread.
        var binPath = "\"\\\"" + exe + "\\\"\"";
        var name = ServiceAccount.ServiceName;
        if (!ServiceExists())
        {
            // Created as start-on-demand under the default account and then switched below.
            // If the switch fails, nothing is left that would start by itself as SYSTEM.
            Cmd.Run(log, "sc.exe", $"create {name} binPath= {binPath} start= demand DisplayName= {Cmd.Quote(name)}");
            log.Line($"Created the Windows service {name}.");
        }

        var configure = $"config {name} binPath= {binPath} obj= {Cmd.Quote(ServiceAccount.AccountName)} start= delayed-auto DisplayName= {Cmd.Quote(name)}";
        if (!Cmd.TryRun(log, "sc.exe", configure).Succeeded)
        {
            // Some Windows versions want an empty password spelled out for a virtual account.
            Cmd.Run(log, "sc.exe", configure + " password= \"\"");
        }

        Cmd.Run(log, "sc.exe", $"description {name} {Cmd.Quote(ServiceAccount.Description)}");
        Cmd.Run(log, "sc.exe", $"failure {name} reset= 86400 actions= restart/5000/restart/30000/restart/60000");
        log.Line($"The service runs {exe} as {ServiceAccount.AccountName}, starts automatically (delayed) and restarts after a failure.");
    }

    public static void DeleteService(SetupLog log)
    {
        if (!ServiceExists())
        {
            log.Line("The ScanRelay service was not registered.");
            return;
        }

        StopService(log);
        Cmd.Run(log, "sc.exe", $"delete {ServiceAccount.ServiceName}");
        for (var i = 0; i < 30 && ServiceExists(); i++) Thread.Sleep(1000);
        if (ServiceExists()) log.Warn("Windows has marked the ScanRelay service for removal, but something still has it open (often the Services window). It goes when that closes or the server restarts.");
        else log.Line("Removed the ScanRelay service.");
    }

    /// <summary>Starts the service and waits for the admin screen's health check to answer.</summary>
    public static async Task StartServiceAndWaitAsync(SetupLog log, SetupAnswers answers, CancellationToken ct)
    {
        using var service = new ServiceController(ServiceAccount.ServiceName);
        try
        {
            if (service.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)) service.Start();
        }
        catch (InvalidOperationException e)
        {
            throw new SetupException($"The ScanRelay service could not be started: {e.InnerException?.Message ?? e.Message} {WhereToLook(answers)}", e);
        }

        log.Line("Started the ScanRelay service. Waiting for the admin screen to answer.");
        var url = answers.LocalUrl + "/health";

        // The check is to this machine itself, where a self-signed certificate is expected
        // and the name localhost may not be on a certificate the admin picked.
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var last = "it has not answered";
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(90))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    log.Line($"{url} answered 200 after {watch.Elapsed.TotalSeconds:0} seconds.");
                    return;
                }

                last = $"it answered {(int)response.StatusCode}: {Truncate(await response.Content.ReadAsStringAsync(ct))}";
            }
            catch (HttpRequestException e)
            {
                last = e.InnerException?.Message ?? e.Message;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = "the request timed out";
            }

            service.Refresh();
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                throw new SetupException($"The ScanRelay service started and then stopped again. {WhereToLook(answers)}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new SetupException($"The ScanRelay service is running, but {url} did not answer 200 within 90 seconds. Last result: {last}. {WhereToLook(answers)}");
    }

    private static string WhereToLook(SetupAnswers answers) =>
        $"Look in {Path.Combine(answers.DataFolder, "Logs")} and in the Windows Application event log for the reason.";

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "...";

    // ---- Files and folders ----

    /// <summary>Empties the program folder ahead of new files, leaving modules, the install records and a running setup alone.</summary>
    public static void ClearProgramFolder(SetupLog log, string folder, string runningExe)
    {
        Directory.CreateDirectory(folder);
        var removed = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder).ToList())
        {
            if (KeptInProgramFolder.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase)) continue;
            if (SamePath(entry, runningExe)) continue;
            Retry($"deleting {entry}", () =>
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            });
            removed++;
        }

        log.Line($"Removed {removed} old items from {folder}. Modules and the install records were kept.");
    }

    public static void ExtractPayload(SetupLog log, Stream payload, string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        var count = 0;
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            var relative = entry.FullName.Replace('/', '\\').TrimStart('\\');
            if (relative.Length == 0) continue;
            var top = relative.Split('\\')[0];

            // The payload is the service; it never replaces what setup itself owns.
            if (KeptInProgramFolder.Contains(top, StringComparer.OrdinalIgnoreCase) || top.Equals(SetupExe, StringComparison.OrdinalIgnoreCase)) continue;

            var target = Path.GetFullPath(Path.Combine(folder, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupException($"The program files inside setup are damaged: \"{entry.FullName}\" points outside the program folder.");
            }

            if (relative.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Retry($"writing {target}", () => entry.ExtractToFile(target, overwrite: true));
            count++;
        }

        if (!File.Exists(Path.Combine(folder, ServiceExe)))
        {
            throw new SetupException($"The program files inside setup do not include {ServiceExe} at the top level. The payload must be a zip of the published service folder.");
        }

        log.Line($"Copied {count} program files to {folder}.");
    }

    public static void CopySetupExe(SetupLog log, string runningExe, string installFolder)
    {
        var target = Path.Combine(installFolder, SetupExe);
        if (SamePath(runningExe, target))
        {
            log.Line("Setup is running from the program folder, so its own file was left in place.");
            return;
        }

        if (File.Exists(Path.ChangeExtension(runningExe, ".dll")))
        {
            log.Warn("This copy of setup is not a single file (a development build), so the copy in the program folder will not run by itself. Modify and Uninstall from Windows installed apps need a published setup.");
        }

        Retry($"copying setup to {target}", () => File.Copy(runningExe, target, overwrite: true));
        log.Line($"Copied setup to {target} for Modify, Repair and Uninstall.");
    }

    /// <summary>Moves everything from one folder into another, across drives if need be, and removes the old one.</summary>
    public static void MoveFolder(SetupLog log, string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in Directory.EnumerateFileSystemEntries(from).ToList())
        {
            var target = Path.Combine(to, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                if (!Directory.Exists(target) && SameDrive(entry, target)) Directory.Move(entry, target);
                else
                {
                    CopyFolder(entry, target);
                    Directory.Delete(entry, recursive: true);
                }
            }
            else
            {
                File.Move(entry, target, overwrite: true);
            }
        }

        Directory.Delete(from, recursive: true);
        log.Line($"Moved {from} to {to}.");
    }

    public static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var folder in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, folder)));
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    /// <summary>
    /// Deletes a folder. When setup is running from inside it, the last of it goes a few
    /// seconds after setup exits, because a program cannot delete its own file.
    /// </summary>
    public static void DeleteFolder(SetupLog log, string folder, string runningExe)
    {
        if (!Directory.Exists(folder))
        {
            log.Line($"{folder} was already gone.");
            return;
        }

        if (!WinPath.IsSameOrInside(runningExe, folder))
        {
            Retry($"deleting {folder}", () => Directory.Delete(folder, recursive: true));
            log.Line($"Deleted {folder}.");
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(folder).ToList())
        {
            if (SamePath(entry, runningExe)) continue;
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Whatever is still in use goes with the folder below.
            }
        }

        var system = Environment.SystemDirectory;
        var info = new ProcessStartInfo(Path.Combine(system, "cmd.exe"), $"/d /s /c \"ping -n 4 127.0.0.1 >nul & rmdir /s /q {Cmd.Quote(folder)}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,

            // Not the folder being deleted, or the helper itself would keep it in use.
            WorkingDirectory = system,
        };
        Process.Start(info)?.Dispose();
        log.Line($"Emptied {folder}. Setup is running from it, so the folder itself is removed a few seconds after setup closes.");
    }

    // ---- Access rights ----

    /// <summary>The data folder: the service and administrators, and nobody else.</summary>
    public static void SecureDataFolder(SetupLog log, string dataFolder)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var who in new[] { LocalSystem, Administrators, ServiceSid })
        {
            // Full control includes changing rights, which the service needs for the per-person folders in Inbox.
            security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
        }

        var info = new DirectoryInfo(dataFolder);
        info.SetAccessControl(security);
        info.Attributes |= FileAttributes.Hidden;
        log.Line($"Set rights on {dataFolder}: inheritance off; full control for SYSTEM, Administrators and {ServiceAccount.AccountName} only. Marked it hidden.");
    }

    /// <summary>Inbox: on top of what it inherits, the printer account may drop files in and nothing more, unless a module needs more.</summary>
    public static void SetInboxRights(SetupLog log, string inbox, SecurityIdentifier printer, InboxRights rights)
    {
        var info = new DirectoryInfo(inbox);
        var security = info.GetAccessControl(AccessControlSections.Access);

        // Start clean, so an earlier printer account or wider rights from a removed module do not linger.
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRuleSpecific(rule);
        }

        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(printer, (FileSystemRights)(int)rights.Folders, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(printer, (FileSystemRights)(int)rights.Files, InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        info.SetAccessControl(security);
        log.Line($"Set the printer account's rights on {inbox}: {rights.Describe()}. Folders 0x{(int)rights.Folders:X}, files 0x{(int)rights.Files:X}.");
    }

    /// <summary>
    /// The program folder: the service may read and run it, never write to it. Under
    /// Program Files that is already so. A folder elsewhere may have inherited write
    /// access for ordinary users, and is then closed.
    /// </summary>
    public static void SecureProgramFolder(SetupLog log, string folder)
    {
        const FileSystemRights writing = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        const int genericWriteOrAll = 0x40000000 | 0x10000000;

        var info = new DirectoryInfo(folder);
        var rules = info.GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .ToList();
        var trusted = new[] { LocalSystem, Administrators, TrustedInstaller, CreatorOwner };
        var writers = rules
            .Where(r => !trusted.Contains((SecurityIdentifier)r.IdentityReference)
                && ((r.FileSystemRights & writing) != 0 || ((int)r.FileSystemRights & genericWriteOrAll) != 0))
            .Select(r => r.IdentityReference.Value)
            .Distinct()
            .ToList();

        if (writers.Count > 0)
        {
            var closed = new DirectorySecurity();
            closed.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            closed.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
            closed.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
            closed.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
            closed.AddAccessRule(new FileSystemAccessRule(ServiceSid, FileSystemRights.ReadAndExecute, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(closed);
            log.Line($"{folder} could be written to by {string.Join(", ", writers)}. Closed it: full control for SYSTEM and Administrators, read and run for Users and {ServiceAccount.AccountName}.");
            return;
        }

        var through = new[] { ServiceSid, Users, AuthenticatedUsers, Everyone };
        var canRead = rules.Any(r => through.Contains((SecurityIdentifier)r.IdentityReference)
            && (r.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute
            && r.InheritanceFlags == ToEverythingInside
            && !r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly));
        if (canRead)
        {
            log.Line($"{ServiceAccount.AccountName} can already read and run {folder}, and cannot write to it. Rights left as they are.");
            return;
        }

        var security = info.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(ServiceSid, FileSystemRights.ReadAndExecute, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
        log.Line($"Gave {ServiceAccount.AccountName} read and run access to {folder}. It has no write access.");
    }

    /// <summary>
    /// install.json holds the printer password, protected for this machine. Machine
    /// protection alone can be undone by any program on the server, so the file is also closed.
    /// </summary>
    public static void WriteInstallFile(SetupLog log, string installFolder, InstallConfig config)
    {
        var path = Path.Combine(installFolder, InstallConfig.FileName);
        var temporary = path + ".new";
        File.WriteAllText(temporary, InstallFile.Serialize(config));

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(ServiceSid, FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(temporary).SetAccessControl(security);
        File.Move(temporary, path, overwrite: true);
        log.Line($"Wrote {path}. Only SYSTEM, Administrators and {ServiceAccount.AccountName} (read only) can open it.");
    }

    // ---- Printer account ----

    public static SecurityIdentifier AccountSid(string name)
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(Environment.MachineName, name).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException e)
        {
            throw new SetupException($"Windows could not find the local account \"{name}\" that was just set up.", e);
        }
    }

    /// <summary>
    /// Creates or repairs the printer account and returns its protected password for install.json.
    /// The password is only made anew when the account is new or no stored password can be read back.
    /// </summary>
    public static string EnsurePrinterAccount(SetupLog log, string name, InstallConfig? existing)
    {
        string? stored = null;
        var comment = Native.FindLocalUserComment(name);
        if (comment is not null)
        {
            if (!comment.StartsWith(Native.AccountCommentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupException(
                    $"A local account named \"{name}\" already exists on this server and was not created by ScanRelay. "
                    + "Setup will not take it over. Choose another name for the printer account.");
            }

            if (existing is not null && name.Equals(existing.PrinterAccount, StringComparison.OrdinalIgnoreCase) && CanReadPassword(existing))
            {
                stored = existing.PrinterPasswordProtected;
            }
        }

        if (comment is null)
        {
            var password = PrinterPassword.Generate();
            Native.CreateLocalUser(name, password);
            stored = Core.Printers.PrinterAccountSecret.Protect(password);
            log.Line($"Created the local account {name} with a generated {PrinterPassword.Length}-character password.");
        }
        else if (stored is null)
        {
            var password = PrinterPassword.Generate();
            Native.SetLocalUserPassword(name, password);
            stored = Core.Printers.PrinterAccountSecret.Protect(password);
            log.Warn($"The local account {name} existed but its password was not on record, so a new one was generated. Printers get it with the next address book push.");
        }
        else
        {
            log.Line($"The local account {name} exists and its password is on record. The password was kept.");
        }

        Native.SetLocalUserFlags(name);
        foreach (var group in Native.GetLocalGroups(name))
        {
            Native.RemoveFromLocalGroup(group, name);
            log.Line($"Removed {name} from the local group {group}.");
        }

        var sid = AccountSid(name);
        Native.AddAccountRights(sid, "SeDenyInteractiveLogonRight", "SeDenyRemoteInteractiveLogonRight");
        var rights = Native.GetAccountRights(sid);
        var groups = Native.GetLocalGroups(name);
        log.Line($"{name} ({sid}): password never expires and cannot be changed by the account; local groups: {(groups.Count == 0 ? "none" : string.Join(", ", groups))}; rights: {string.Join(", ", rights)}.");
        if (groups.Count > 0) throw new SetupException($"The account {name} is still in the groups {string.Join(", ", groups)} after setup tried to remove it.");
        return stored;
    }

    public static void DeletePrinterAccount(SetupLog log, string name)
    {
        var comment = Native.FindLocalUserComment(name);
        if (comment is null)
        {
            log.Line($"The local account {name} was already gone.");
            return;
        }

        if (!comment.StartsWith(Native.AccountCommentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            log.Warn($"The local account {name} was not created by ScanRelay, so it was left alone.");
            return;
        }

        Native.RemoveAllAccountRights(AccountSid(name));
        Native.DeleteLocalUser(name);
        log.Line($"Deleted the local account {name}.");
    }

    private static bool CanReadPassword(InstallConfig config)
    {
        try
        {
            return Core.Printers.PrinterAccountSecret.Read(config).Length > 0;
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return false;
        }
    }

    // ---- Share ----

    public static void ShareInbox(SetupLog log, string shareName, string inbox, SecurityIdentifier printer, IEnumerable<string> foldersSetupOwns)
    {
        if (Native.FindSharePath(shareName) is { } current)
        {
            if (!foldersSetupOwns.Append(inbox).Any(f => WinPath.Same(f, current)))
            {
                throw new SetupException($"A share named {shareName} already exists on this server for {current}. Setup will not replace it. Choose another share name.");
            }

            Native.DeleteShare(shareName);
            log.Line($"Removed the existing share {shareName} so it can be made again.");
        }

        Native.CreateShare(shareName, inbox, "ScanRelay: printers save scans here.", printer);
        log.Line($"Shared {inbox} as \\\\{Environment.MachineName}\\{shareName}. Only {printer} (the printer account) has access to the share: Change.");
    }

    /// <summary>Removes a share, but only one that points at a folder setup made.</summary>
    public static void RemoveShare(SetupLog log, string shareName, params string[] foldersSetupOwns)
    {
        var current = Native.FindSharePath(shareName);
        if (current is null)
        {
            log.Line($"The share {shareName} was already gone.");
        }
        else if (!foldersSetupOwns.Any(f => WinPath.Same(f, current)))
        {
            log.Warn($"The share {shareName} points at {current}, which is not ScanRelay's, so it was left alone.");
        }
        else
        {
            Native.DeleteShare(shareName);
            log.Line($"Removed the share {shareName}.");
        }
    }

    // ---- Certificates ----

    public static X509Certificate2? FindCertificate(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return null;
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, SetupAnswers.NormaliseThumbprint(thumbprint), validOnly: false);
        return found.Count > 0 ? found[0] : null;
    }

    /// <summary>The certificates an admin could pick for the admin screen: those in the machine store with a private key.</summary>
    public static IReadOnlyList<X509Certificate2> ListWebCertificateChoices()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return [.. store.Certificates.Where(c => c.HasPrivateKey && c.FriendlyName is not (InstalledState.GraphCertFriendlyName or InstalledState.WebCertFriendlyName))];
    }

    /// <summary>Puts a new self-signed certificate in the machine store. Its key stays on this machine and cannot be exported.</summary>
    public static X509Certificate2 AddSelfSigned(SetupLog log, string subject, DateTimeOffset now, IEnumerable<string>? dnsNames, string friendlyName)
    {
        using var made = Core.Cloud.GraphCertificate.CreateSelfSigned(subject, now, years: 2, dnsNames);

        // Round-tripped through a PFX because that is how a key is made to persist in the
        // machine store. The password only guards the bytes inside this method.
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var pfx = made.Export(X509ContentType.Pfx, password);
        var keyFilesBefore = ListKeyFiles();
        var certificate = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        certificate.FriendlyName = friendlyName;
        using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);
        }

        log.Line($"Created the certificate {subject} ({friendlyName}), thumbprint {certificate.Thumbprint}, valid until {certificate.NotAfter:yyyy-MM-dd}, in the machine store. Its key cannot be exported.");

        // If Windows will not name the key's file, it is the one file that appeared while the key was being stored.
        var appeared = ListKeyFiles().Except(keyFilesBefore, StringComparer.OrdinalIgnoreCase).ToList();
        if (!GrantKeyRead(log, certificate, appeared.Count == 1 ? appeared[0] : null))
        {
            throw new SetupException($"Setup created the certificate {certificate.Thumbprint} but could not find its private key file under {CryptoFolder}, so it could not let the service read it.");
        }

        return certificate;
    }

    /// <summary>Removes a certificate and its key from the machine store.</summary>
    public static void RemoveCertificate(SetupLog log, string? thumbprint)
    {
        using var certificate = FindCertificate(thumbprint);
        if (certificate is null)
        {
            if (!string.IsNullOrWhiteSpace(thumbprint)) log.Line($"The certificate {thumbprint} was already gone.");
            return;
        }

        var keyFile = FindKeyFile(certificate);
        using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);
            store.Remove(certificate);
        }

        if (keyFile is not null)
        {
            try
            {
                File.Delete(keyFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Warn($"The certificate {certificate.Thumbprint} was removed, but its key file {keyFile} could not be deleted: {e.Message}");
            }
        }

        log.Line($"Removed the certificate {certificate.Subject} ({certificate.Thumbprint}) and its key.");
    }

    /// <summary>Lets the service use a certificate's private key. Returns false when the key is not a file setup can find.</summary>
    public static bool GrantKeyRead(SetupLog log, X509Certificate2 certificate, string? knownKeyFile = null)
    {
        var keyFile = FindKeyFile(certificate) ?? knownKeyFile;
        if (keyFile is null) return false;
        var info = new FileInfo(keyFile);
        var security = info.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(ServiceSid, FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(security);
        log.Line($"Gave {ServiceAccount.AccountName} read access to the private key of {certificate.Thumbprint} ({keyFile}).");
        return true;
    }

    /// <summary>The file that holds a machine certificate's private key, found from the key's unique name.</summary>
    public static string? FindKeyFile(X509Certificate2 certificate)
    {
        string? unique = null;
        try
        {
            using var rsa = certificate.GetRSAPrivateKey();
            unique = rsa switch
            {
                RSACng cng => cng.Key.UniqueName,
                RSACryptoServiceProvider csp => csp.CspKeyContainerInfo.UniqueKeyContainerName,
                _ => null,
            };
            if (unique is null)
            {
                using var ecdsa = certificate.GetECDsaPrivateKey();
                if (ecdsa is ECDsaCng ec) unique = ec.Key.UniqueName;
            }
        }
        catch (CryptographicException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(unique)) return null;
        return KeyFolders.Select(folder => Path.Combine(CryptoFolder, folder, unique)).FirstOrDefault(File.Exists);
    }

    /// <summary>Where Windows keeps the private keys of machine certificates.</summary>
    private static string CryptoFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Crypto");

    private static readonly string[] KeyFolders = ["Keys", @"RSA\MachineKeys", "SystemKeys", @"DSS\MachineKeys"];

    private static List<string> ListKeyFiles() =>
        [.. KeyFolders.Select(folder => Path.Combine(CryptoFolder, folder)).Where(Directory.Exists).SelectMany(Directory.EnumerateFiles)];

    // ---- Firewall ----

    public static void SetFirewallRule(SetupLog log, bool add, int port)
    {
        var name = Cmd.Quote(SetupAnswers.FirewallRuleName);

        // netsh fails when there is no such rule, which is the usual case and not a problem.
        var removed = Cmd.TryRun(log, "netsh.exe", $"advfirewall firewall delete rule name={name}").Succeeded;
        if (!add)
        {
            log.Line(removed ? $"Removed the firewall rule \"{SetupAnswers.FirewallRuleName}\"." : "No firewall rule was asked for, and there was none to remove.");
            return;
        }

        Cmd.Run(log, "netsh.exe", $"advfirewall firewall add rule name={name} dir=in action=allow protocol=TCP localport={port} description={Cmd.Quote("Lets admins reach the ScanRelay admin screen.")}");
        log.Line($"Added the firewall rule \"{SetupAnswers.FirewallRuleName}\": inbound TCP {port}.");
    }

    // ---- Installed apps ----

    public const string SetupLinkScheme = "scanrelay-setup";
    public const string SetupLinkKey = @"SOFTWARE\Classes\" + SetupLinkScheme;

    public static void RegisterInstalledApp(SetupLog log, string installFolder, string version)
    {
        var setup = Path.Combine(installFolder, SetupExe);
        long bytes = 0;
        foreach (var file in new DirectoryInfo(installFolder).EnumerateFiles("*", SearchOption.AllDirectories)) bytes += file.Length;

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.CreateSubKey(InstalledState.UninstallKey, writable: true);
        key.SetValue("DisplayName", "ScanRelay");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "The ScanRelay authors");
        key.SetValue("InstallLocation", installFolder);
        key.SetValue("DisplayIcon", setup);
        key.SetValue("UninstallString", $"\"{setup}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{setup}\" /quiet /uninstall");
        key.SetValue("ModifyPath", $"\"{setup}\" /modify");
        key.SetValue("NoRepair", 0, RegistryValueKind.DWord);
        key.SetValue("NoModify", 0, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        log.Line($"Registered ScanRelay {version} in Windows installed apps (HKLM\\{InstalledState.UninstallKey}).");

        // A link the admin screen can offer, in a browser on this server, that opens setup at its Modules step.
        // Nothing from the link is passed to setup, so a link cannot make it do anything else.
        using var link = hklm.CreateSubKey(SetupLinkKey, writable: true);
        link.SetValue("", "URL:ScanRelay setup");
        link.SetValue("URL Protocol", "");
        using var command = link.CreateSubKey(@"shell\open\command", writable: true);
        command.SetValue("", $"\"{setup}\" /modules");
        log.Line($"Registered the {SetupLinkScheme}: link for opening setup's Modules step.");
    }

    public static void UnregisterInstalledApp(SetupLog log)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        hklm.DeleteSubKeyTree(InstalledState.UninstallKey, throwOnMissingSubKey: false);
        hklm.DeleteSubKeyTree(SetupLinkKey, throwOnMissingSubKey: false);
        log.Line("Removed ScanRelay from Windows installed apps.");
    }

    // ---- Helpers ----

    public static bool SamePath(string a, string b) =>
        Path.GetFullPath(a).TrimEnd('\\').Equals(Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool SameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>A file the stopped service had open can stay locked for a moment, so file work gets a few tries.</summary>
    private static void Retry(string doing, Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5) throw new SetupException($"Failed {doing}: {e.Message} Close anything that has the file open and run setup again.", e);
                Thread.Sleep(1000);
            }
        }
    }
}
