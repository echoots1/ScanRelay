using System.Text.Json;
using ScanRelay.Core.Install;

namespace ScanRelay.Installer;

/// <summary>Reads and writes <c>install.json</c>, the service's record of what setup decided.</summary>
public static class InstallFile
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Builds the service's settings from the answers. Anything setup does not own,
    /// such as the cloud endpoints, is carried over from the file that was there.
    /// </summary>
    public static InstallConfig Build(SetupAnswers answers, InstallConfig? existing, string? graphThumbprint, string? webThumbprint, string? protectedPassword)
    {
        var config = existing is null ? new InstallConfig() : Parse(Serialize(existing))!;
        config.DataFolder = WinPath.Normalise(answers.DataFolder);
        config.Url = answers.Url;
        config.WebCertThumbprint = webThumbprint;
        config.GraphCertThumbprint = graphThumbprint;
        config.PrinterAccount = answers.PrinterAccount;
        config.PrinterPasswordProtected = protectedPassword;
        config.ShareName = answers.ShareName;
        config.ServerName = answers.HostName;
        config.ModulesFolder = answers.ModulesFolder;
        config.UpdateCheck = answers.UpdateCheck;
        config.Repository = answers.Repository;
        config.ReopenSetup = answers.ReopenSetup;
        return config;
    }

    public static string Serialize(InstallConfig config) => JsonSerializer.Serialize(config, Json);

    public static InstallConfig? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<InstallConfig>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The file in a program folder, or null when it is missing or unreadable.</summary>
    public static InstallConfig? Read(string installFolder)
    {
        var path = Path.Combine(installFolder, InstallConfig.FileName);
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
