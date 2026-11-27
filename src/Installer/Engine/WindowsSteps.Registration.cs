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

/// <summary>What setup records with Windows: installed apps, the desktop icon, the icon by the clock and the setup link.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsSteps
{
    // ---- Everyday use: the desktop icon and the icon by the clock ----

    public const string DesktopShortcutName = "ScanRelay.lnk";
    public const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "ScanRelay";

    /// <summary>Where the admin screen's address on this server is kept for the icons, which run as an ordinary person and may not read the install settings.</summary>
    public const string LocalUrlValue = "LocalUrl";

    public static string DesktopShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), DesktopShortcutName);

    /// <summary>The machine's registry as 64-bit programs see it, which is where Windows keeps installed apps and what starts at sign-in.</summary>
    public static RegistryKey OpenMachineKey() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    /// <summary>Adds or removes the desktop icon and the icon by the clock, to match what was asked.</summary>
    public static void SetEverydayUse(SetupLog log, string installFolder, string localUrl, bool desktopShortcut, bool trayIcon)
    {
        var setup = Path.Combine(installFolder, SetupExe);
        using var hklm = OpenMachineKey();
        using (var app = hklm.CreateSubKey(InstalledState.UninstallKey, writable: true))
        {
            app.SetValue(LocalUrlValue, localUrl);
        }

        if (desktopShortcut)
        {
            try
            {
                CreateShortcut(DesktopShortcutPath, setup, "/open", installFolder, "Open ScanRelay. Starts it first if it is stopped.");
                log.Line($"Put a ScanRelay icon on the desktop for everyone who signs in to this server ({DesktopShortcutPath}).");
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or System.Reflection.TargetInvocationException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                log.Warn($"The desktop icon could not be made: {(e.InnerException ?? e).Message} ScanRelay works without it; open {localUrl} in a browser on this server.");
            }
        }
        else if (File.Exists(DesktopShortcutPath))
        {
            File.Delete(DesktopShortcutPath);
            log.Line("Removed the ScanRelay desktop icon, as asked.");
        }

        using var run = hklm.CreateSubKey(RunKey, writable: true);
        if (trayIcon)
        {
            run.SetValue(RunValue, $"\"{setup}\" /tray");
            log.Line("The ScanRelay icon by the clock will show for each person from their next sign-in. It also shows the first time the desktop icon is used.");
        }
        else if (run.GetValue(RunValue) is not null)
        {
            run.DeleteValue(RunValue, throwOnMissingValue: false);
            log.Line("The ScanRelay icon by the clock will no longer start at sign-in, as asked.");
        }
    }

    public static void RemoveEverydayUse(SetupLog log)
    {
        if (File.Exists(DesktopShortcutPath)) File.Delete(DesktopShortcutPath);
        using var hklm = OpenMachineKey();
        using var run = hklm.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(RunValue, throwOnMissingValue: false);
        log.Line("Removed the ScanRelay desktop icon and its start at sign-in.");
    }

    /// <summary>
    /// Closes every other copy of setup running from the program folder. Those are the icons by the
    /// clock in people's sessions and, when setup itself was started from that folder, the copy that
    /// asked Windows for an administrator and is only waiting for this one. While any of them runs,
    /// its file cannot be replaced or removed.
    /// </summary>
    public static void CloseTrayIcons(SetupLog log, string installFolder)
    {
        var closed = 0;
        foreach (var process in RunningFrom(Path.Combine(installFolder, SetupExe)))
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId && TryEnd(process)) closed++;
            }
        }

        if (closed > 0) log.Line($"Closed {closed} ScanRelay icon(s) by the clock so the program files can be changed. They come back at the next sign-in, or when the desktop icon is used.");
    }

    /// <summary>Makes a Windows shortcut. Windows only offers this through its scripting component.</summary>
    private static void CreateShortcut(string path, string target, string arguments, string startIn, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available on this server.");
        var shell = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows Script Host could not be started.");
        object? shortcut = null;
        try
        {
            const System.Reflection.BindingFlags call = System.Reflection.BindingFlags.InvokeMethod;
            const System.Reflection.BindingFlags set = System.Reflection.BindingFlags.SetProperty;
            shortcut = type.InvokeMember("CreateShortcut", call, null, shell, [path])!;
            var link = shortcut.GetType();
            link.InvokeMember("TargetPath", set, null, shortcut, [target]);
            link.InvokeMember("Arguments", set, null, shortcut, [arguments]);
            link.InvokeMember("WorkingDirectory", set, null, shortcut, [startIn]);
            link.InvokeMember("Description", set, null, shortcut, [description]);
            link.InvokeMember("IconLocation", set, null, shortcut, [target + ",0"]);
            link.InvokeMember("Save", call, null, shortcut, null);
        }
        finally
        {
            if (shortcut is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    // ---- Installed apps, and the link that opens setup at its Modules step ----

    public const string SetupLinkScheme = "scanrelay-setup";
    public const string SetupLinkKey = @"SOFTWARE\Classes\" + SetupLinkScheme;

    public static void RegisterInstalledApp(SetupLog log, string installFolder, string version)
    {
        var setup = Path.Combine(installFolder, SetupExe);
        long bytes = 0;
        foreach (var file in new DirectoryInfo(installFolder).EnumerateFiles("*", SearchOption.AllDirectories)) bytes += file.Length;

        using var hklm = OpenMachineKey();
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
        using var hklm = OpenMachineKey();
        hklm.DeleteSubKeyTree(InstalledState.UninstallKey, throwOnMissingSubKey: false);
        hklm.DeleteSubKeyTree(SetupLinkKey, throwOnMissingSubKey: false);
        log.Line("Removed ScanRelay from Windows installed apps.");
    }
}
