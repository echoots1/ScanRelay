using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ScanRelay.Installer;

/// <summary>
/// NTFS access bits, with the same values Windows uses. Kept as our own type so the
/// rights mapping can be worked out and tested on any machine.
/// </summary>
[Flags]
public enum NtfsRights
{
    None = 0,

    /// <summary>On a file: read its contents. On a folder: list what is in it.</summary>
    ReadDataOrListFolder = 0x1,

    /// <summary>On a file: write to it. On a folder: create files in it.</summary>
    WriteDataOrCreateFiles = 0x2,

    /// <summary>On a file: append to it. On a folder: create folders in it.</summary>
    AppendDataOrCreateFolders = 0x4,
    ReadExtendedAttributes = 0x8,
    WriteExtendedAttributes = 0x10,
    Traverse = 0x20,
    DeleteSubfoldersAndFiles = 0x40,
    ReadAttributes = 0x80,
    WriteAttributes = 0x100,
    Delete = 0x10000,
    Synchronize = 0x100000,
}

/// <summary>
/// What the printer account may do in <c>Inbox</c>. Folders and files get separate
/// entries because "read a file" and "list a folder" are the same bit in Windows.
/// </summary>
public sealed record InboxRights(NtfsRights Folders, NtfsRights Files)
{
    /// <summary>Drop a file and nothing else: no listing, no reading back, no deleting.</summary>
    public static InboxRights Strict { get; } = new(
        NtfsRights.WriteDataOrCreateFiles | NtfsRights.AppendDataOrCreateFolders | NtfsRights.WriteAttributes | NtfsRights.WriteExtendedAttributes
            | NtfsRights.ReadAttributes | NtfsRights.Traverse | NtfsRights.Synchronize,
        NtfsRights.WriteDataOrCreateFiles | NtfsRights.AppendDataOrCreateFolders | NtfsRights.WriteAttributes | NtfsRights.WriteExtendedAttributes
            | NtfsRights.ReadAttributes | NtfsRights.Synchronize);

    /// <summary>
    /// The strict rights, widened only by what some installed module says its printers
    /// need (<c>scan.share_rights</c> in its module data).
    /// </summary>
    public static InboxRights For(IEnumerable<IEnumerable<string>> shareRightsOfInstalledModules)
    {
        var asked = shareRightsOfInstalledModules.SelectMany(r => r).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = Strict.Folders;
        var files = Strict.Files;
        if (asked.Contains("read")) files |= NtfsRights.ReadDataOrListFolder;
        if (asked.Contains("list")) folders |= NtfsRights.ReadDataOrListFolder;
        if (asked.Contains("delete"))
        {
            folders |= NtfsRights.Delete | NtfsRights.DeleteSubfoldersAndFiles;
            files |= NtfsRights.Delete;
        }

        return new InboxRights(folders, files);
    }

    /// <summary>The extras in plain words, for the log and the summary.</summary>
    public string Describe()
    {
        var extras = new List<string>();
        if (Files.HasFlag(NtfsRights.ReadDataOrListFolder)) extras.Add("read files");
        if (Folders.HasFlag(NtfsRights.ReadDataOrListFolder)) extras.Add("list folders");
        if (Files.HasFlag(NtfsRights.Delete)) extras.Add("delete");
        return extras.Count == 0
            ? "create and write only"
            : "create and write, plus " + string.Join(", ", extras) + " because an installed module needs it";
    }
}

/// <summary>The Windows service and the account it runs as.</summary>
public static class ServiceAccount
{
    public const string ServiceName = "ScanRelay";
    public const string AccountName = @"NT SERVICE\ScanRelay";
    public const string Description = "Moves scans from printers into each person's OneDrive, and serves the ScanRelay admin screen.";

    /// <summary>
    /// The service account's SID. Windows derives it from the service name alone, so it
    /// can be put on folders before the service exists and Windows could look it up.
    /// </summary>
    public static string Sid(string serviceName = ServiceName)
    {
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
        var parts = Enumerable.Range(0, 5).Select(i => BitConverter.ToUInt32(hash, i * 4).ToString(CultureInfo.InvariantCulture));
        return "S-1-5-80-" + string.Join('-', parts);
    }
}

/// <summary>The copy of the database kept before an update changes it.</summary>
public static class DatabaseBackup
{
    public const string DatabaseName = "scanrelay.db";

    /// <summary>The SQLite files that together are the database.</summary>
    public static readonly IReadOnlyList<string> Suffixes = ["", "-wal", "-shm"];

    /// <summary>A backup is taken whenever the installed version is not the one being installed, including when it is unknown.</summary>
    public static bool IsNeeded(string? installedVersion, string newVersion) =>
        !string.Equals(installedVersion?.Trim(), newVersion, StringComparison.OrdinalIgnoreCase);

    public static string FileName(string? oldVersion, DateTime nowUtc)
    {
        var version = new string([.. (oldVersion ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')]);
        if (version.Length == 0) version = "unknown";
        return $"scanrelay-{version}-{nowUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.db";
    }
}

/// <summary>Makes the printer account's password.</summary>
public static class PrinterPassword
{
    public const int Length = 32;

    // Letters and digits only: every printer's address book can hold them, and 32 of
    // them are far beyond guessing. Look-alikes are left out in case one is ever read aloud.
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";
    private const string Digits = "23456789";

    public static string Generate()
    {
        while (true)
        {
            var password = RandomNumberGenerator.GetString(Upper + Lower + Digits, Length);

            // Windows' complexity rule wants three kinds of character.
            if (password.Any(Upper.Contains) && password.Any(Lower.Contains) && password.Any(Digits.Contains)) return password;
        }
    }
}
