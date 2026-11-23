namespace ScanRelay.ModuleSdk;

/// <summary>A printer as the service hands it to a module.</summary>
/// <param name="Address">Host name or IP address.</param>
/// <param name="AdminUser">The printer's admin sign-in name, if it has one.</param>
/// <param name="AdminPassword">The printer's admin password. Never log this.</param>
/// <param name="Options">Per-printer values from the Printers page, overriding the module data.</param>
public sealed record Device(
    string Address,
    string? AdminUser,
    string? AdminPassword,
    IReadOnlyDictionary<string, string> Options);

/// <summary>One address book entry: a person and the folder their scans are written to.</summary>
/// <param name="Key">Stable id for the entry, the person's directory user id.</param>
/// <param name="DisplayName">The name shown on the printer panel.</param>
/// <param name="Destination">Where the printer writes this person's scans.</param>
/// <param name="Email">The person's email address, so the same entry also works for scan to email. Null when it should have none.</param>
public sealed record Entry(string Key, string DisplayName, ScanDestination Destination, string? Email = null);

/// <summary>A scan-to-folder destination and the account the printer uses to reach it.</summary>
public sealed record ScanDestination(
    ScanProtocol Protocol,
    string Host,
    string Path,
    string UserName,
    string Password);

public enum ScanProtocol
{
    Smb,
    Ftp,
    Sftp,
}

public sealed record ProbeResult(bool Reachable, string? Model, string? Firmware, string? Error);

public sealed record ApplyResult(bool Succeeded, int Added, int Updated, int Removed, string? Error);

public sealed record ImportFile(string FileName, string ContentType, byte[] Content);

/// <summary>
/// What the service gives an adapter when it loads. An adapter takes this in its
/// constructor, or has a constructor with no parameters.
/// </summary>
/// <param name="ModuleId">The module's id from its module data.</param>
/// <param name="Http">A shared client for talking to printers over HTTP.</param>
/// <param name="Log">Writes a line to the service log. Never log passwords or file contents.</param>
public sealed record ModuleContext(string ModuleId, HttpClient Http, Action<string> Log);

public static class ModuleSdkInfo
{
    /// <summary>Raised when the contract changes in a way old modules cannot meet.</summary>
    public const int Version = 1;
}
