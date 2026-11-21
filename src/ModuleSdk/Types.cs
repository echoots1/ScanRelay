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
public sealed record Entry(string Key, string DisplayName, ScanDestination Destination);

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
