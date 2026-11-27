using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using ScanRelay.Core.Install;

namespace ScanRelay.Installer;

/// <summary>
/// What setup did last time, kept beside the program files so Modify, Repair, Update
/// and Uninstall know what is theirs to change or remove.
/// </summary>
public sealed class InstalledState
{
    public const string FileName = "setup-state.json";
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ScanRelay";

    /// <summary>Marks the web certificate as one setup made, so uninstall never removes a certificate the admin picked.</summary>
    public const string WebCertFriendlyName = "ScanRelay web certificate";
    public const string GraphCertFriendlyName = "ScanRelay Graph certificate";

    /// <summary>The installed app version, such as 0.1.0.</summary>
    public string Version { get; set; } = "";

    /// <summary>The answers last used, without one-off requests. There are no secrets in them.</summary>
    public SetupAnswers Answers { get; set; } = new();

    public string? GraphCertThumbprint { get; set; }

    public string? WebCertThumbprint { get; set; }

    /// <summary>True when setup made the web certificate, so setup may also replace or remove it.</summary>
    public bool WebCertCreatedBySetup { get; set; }

    public DateTime InstalledUtc { get; set; }

    public DateTime ChangedUtc { get; set; }

    // Lenient on purpose: an older setup must still read a newer state file well enough to uninstall.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads the state from a program folder, or null when there is none or it is unreadable.</summary>
    public static InstalledState? ReadFrom(string installFolder)
    {
        var path = Path.Combine(installFolder, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<InstalledState>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void WriteTo(string installFolder) => File.WriteAllText(Path.Combine(installFolder, FileName), ToJson());

    /// <summary>
    /// Rebuilds what can be known from <c>install.json</c> alone, for an install whose
    /// state file was lost. The version is unknown, so the next run is treated as an update.
    /// </summary>
    public static InstalledState FromInstallConfig(InstallConfig config, string installFolder)
    {
        var answers = new SetupAnswers { InstallFolder = installFolder };
        if (!string.IsNullOrWhiteSpace(config.DataFolder)) answers.DataFolder = config.DataFolder;
        if (Uri.TryCreate(config.Url, UriKind.Absolute, out var url))
        {
            answers.HostName = url.Host;
            answers.Port = url.Port;
        }

        if (!string.IsNullOrWhiteSpace(config.PrinterAccount)) answers.PrinterAccount = config.PrinterAccount;
        if (!string.IsNullOrWhiteSpace(config.ShareName)) answers.ShareName = config.ShareName;
        answers.UpdateCheck = config.UpdateCheck;
        answers.Repository = config.Repository;
        return new InstalledState
        {
            Version = "",
            Answers = answers,
            GraphCertThumbprint = config.GraphCertThumbprint,
            WebCertThumbprint = config.WebCertThumbprint,
        };
    }

    /// <summary>Finds the current install through the Windows "Installed apps" entry.</summary>
    [SupportedOSPlatform("windows")]
    public static InstalledState? Detect()
    {
        using var hklm = WindowsSteps.OpenMachineKey();
        using var key = hklm.OpenSubKey(UninstallKey);
        if (key?.GetValue("InstallLocation") is not string folder || !Directory.Exists(folder)) return null;

        var state = ReadFrom(folder);
        if (state is null)
        {
            var config = InstallFile.Read(folder);
            if (config is null) return null;
            state = FromInstallConfig(config, folder);
            state.Version = key.GetValue("DisplayVersion") as string ?? "";
        }

        // The folder that was found is the truth, whatever the file inside it says.
        state.Answers.InstallFolder = folder;
        return state;
    }
}
