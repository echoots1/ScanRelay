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
internal static partial class WindowsSteps
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
        // The stream is left open and read from its start, so the copy can be tried again from the same payload.
        if (payload.CanSeek) payload.Position = 0;
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
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

    // ---- Helpers ----

    public static bool SamePath(string a, string b) =>
        Path.GetFullPath(a).TrimEnd('\\').Equals(Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool SameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>How long to wait before each further try at a file, in milliseconds. About half a minute in all.</summary>
    private static readonly int[] RetryWaits = [500, 1000, 2000, 3000, 5000, 8000, 10000];

    /// <summary>Every running copy of the program at this path. The caller disposes them. A process that may not be looked at is left out.</summary>
    private static List<Process> RunningFrom(string exePath)
    {
        var running = new List<Process>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            try
            {
                if (process.MainModule?.FileName is { } path && SamePath(path, exePath)) running.Add(process);
                else process.Dispose();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                process.Dispose();
            }
        }

        return running;
    }

    /// <summary>Ends a process and waits a little for it to go. False when it was already gone or is not ours to end.</summary>
    private static bool TryEnd(Process process)
    {
        try
        {
            process.Kill();
            process.WaitForExit(10000);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

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
                // About half a minute in all: a virus scanner or a program that is still closing can hold a file that long.
                if (attempt > RetryWaits.Length) throw new SetupException($"Failed {doing}: {e.Message} Close anything that has the file open and run setup again.", e);
                Thread.Sleep(RetryWaits[attempt - 1]);
            }
        }
    }
}
