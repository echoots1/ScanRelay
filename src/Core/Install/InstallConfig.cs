namespace ScanRelay.Core.Install;

/// <summary>
/// What the installer decided. These need administrator rights to change, so they
/// are written by the installer to <c>install.json</c> in the program folder, which
/// the service can read but not write. Everything else is a setting in the database.
/// </summary>
public sealed class InstallConfig
{
    public const string FileName = "install.json";

    /// <summary>The hidden data folder.</summary>
    public string? DataFolder { get; set; }

    /// <summary>The admin screen's address, such as https://server.example:8443.</summary>
    public string Url { get; set; } = "https://localhost:8443";

    /// <summary>Thumbprint of the web certificate in the machine store.</summary>
    public string? WebCertThumbprint { get; set; }

    /// <summary>Thumbprint of the Graph certificate in the machine store.</summary>
    public string? GraphCertThumbprint { get; set; }

    /// <summary>The local account printers use to write to the share.</summary>
    public string? PrinterAccount { get; set; }

    /// <summary>The printer account's password, protected for the machine. Used to build address book entries.</summary>
    public string? PrinterPasswordProtected { get; set; }

    /// <summary>The hidden share's name, such as Scans$.</summary>
    public string? ShareName { get; set; }

    /// <summary>The name printers use to reach this server.</summary>
    public string? ServerName { get; set; }

    /// <summary>Where installed modules live. Read-only to the service.</summary>
    public string? ModulesFolder { get; set; }

    /// <summary>Whether the app may check GitHub for updates. Also a setting; the installer sets the first value.</summary>
    public bool UpdateCheck { get; set; } = true;

    /// <summary>The repo that publishes releases and the module catalog. A fork points this at itself.</summary>
    public string Repository { get; set; } = "echoots1/ScanRelay";

    /// <summary>Set by an installer Repair to reopen the local-only setup screen.</summary>
    public bool ReopenSetup { get; set; }

    public CloudEndpoints Cloud { get; set; } = new();

    /// <summary>The admin screen's address as opened on the server itself.</summary>
    public string LocalUrl()
    {
        var uri = new Uri(Url);
        return $"{uri.Scheme}://localhost:{uri.Port}";
    }
}

/// <summary>Where Microsoft's services are. Only changed for national clouds and tests.</summary>
public sealed class CloudEndpoints
{
    public string Login { get; set; } = "https://login.microsoftonline.com";
    public string Graph { get; set; } = "https://graph.microsoft.com/v1.0";
    public string GraphScope { get; set; } = "https://graph.microsoft.com/.default";
    public string GitHubApi { get; set; } = "https://api.github.com";
    public string GitHubDownload { get; set; } = "https://github.com";
}
