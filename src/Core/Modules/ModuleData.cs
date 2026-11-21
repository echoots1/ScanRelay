using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ScanRelay.Core.Modules;

/// <summary>The model data every module carries in <c>module.yaml</c>.</summary>
public sealed partial class ModuleData
{
    public const string FileName = "module.yaml";

    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Family { get; set; } = "";

    /// <summary>Exact model names, added as each is tested.</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>Model names as Windows reports them, used to match installed printers. A trailing * matches any ending.</summary>
    public List<string> WindowsNames { get; set; } = [];

    /// <summary>official or custom.</summary>
    public string Status { get; set; } = "custom";

    /// <summary>The module SDK version it was built for.</summary>
    public int SdkVersion { get; set; } = 1;

    /// <summary>The app versions it works with.</summary>
    public AppVersionRange AppVersions { get; set; } = new();

    /// <summary>The adapter assembly inside the package.</summary>
    public string Adapter { get; set; } = "";

    /// <summary>True when the method is not documented by the vendor.</summary>
    public bool Unofficial { get; set; }

    /// <summary>Plain words on where the method stands, shown in the admin screen.</summary>
    public string? Method { get; set; }

    public ScanData Scan { get; set; } = new();
    public AddressBookData AddressBook { get; set; } = new();

    /// <summary>Anything the admin must install first, such as a vendor tool.</summary>
    public List<string> Prerequisites { get; set; } = [];
    public List<string> TestedFirmware { get; set; } = [];

    /// <summary>Per-printer values and their defaults, shown when a printer is set up.</summary>
    public Dictionary<string, string> Options { get; set; } = [];

    public sealed class AppVersionRange
    {
        public string Min { get; set; } = "0.1.0";
        public string? Max { get; set; }
    }

    public sealed class ScanData
    {
        public List<string> Protocols { get; set; } = [];
        public List<string> SmbVersions { get; set; } = [];
        public List<string> ShareRights { get; set; } = ["create", "write"];
    }

    public sealed class AddressBookData
    {
        public int? MaxEntries { get; set; }
        public List<int>? ManagedRange { get; set; }
        public bool OneTouch { get; set; }
    }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionPattern();

    public static ModuleData Parse(string yaml)
    {
        ModuleData? data;
        try
        {
            data = Yaml.Deserialize<ModuleData>(yaml);
        }
        catch (YamlDotNet.Core.YamlException e)
        {
            throw new ModuleException("The module data could not be read: " + (e.InnerException?.Message ?? e.Message));
        }

        if (data is null) throw new ModuleException("The module data is empty.");
        var problems = data.Validate();
        if (problems.Count > 0) throw new ModuleException("The module data is not valid. " + string.Join(" ", problems));
        return data;
    }

    /// <summary>The schema every module's data is checked against.</summary>
    public IReadOnlyList<string> Validate()
    {
        var p = new List<string>();
        if (!IdPattern().IsMatch(Id)) p.Add("id must be lower-case letters, digits and hyphens.");
        if (!VersionPattern().IsMatch(Version)) p.Add("version must look like 1.0.0.");
        if (string.IsNullOrWhiteSpace(Vendor)) p.Add("vendor is required.");
        if (string.IsNullOrWhiteSpace(Family)) p.Add("family is required.");
        if (Status is not ("official" or "custom")) p.Add("status must be official or custom.");
        if (SdkVersion < 1) p.Add("sdk_version must be 1 or more.");
        if (!VersionPattern().IsMatch(AppVersions.Min)) p.Add("app_versions.min must look like 1.0.0.");
        if (AppVersions.Max is not null && !VersionPattern().IsMatch(AppVersions.Max)) p.Add("app_versions.max must look like 1.0.0.");
        if (!Adapter.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || Adapter.IndexOfAny(['/', '\\']) >= 0) p.Add("adapter must name a .dll in the package.");
        if (Scan.Protocols.Count == 0 || Scan.Protocols.Any(x => x is not ("smb" or "ftp" or "sftp"))) p.Add("scan.protocols must list smb, ftp or sftp.");
        if (Scan.SmbVersions.Any(v => v.StartsWith('1'))) p.Add("scan.smb_versions cannot include SMB 1.");
        if (Scan.Protocols.Contains("smb") && Scan.SmbVersions.Count == 0) p.Add("scan.smb_versions is required when smb is a protocol.");
        if (Scan.ShareRights.Any(r => r is not ("create" or "write" or "read" or "list" or "delete"))) p.Add("scan.share_rights may only list create, write, read, list and delete.");
        if (!Scan.ShareRights.Contains("create") || !Scan.ShareRights.Contains("write")) p.Add("scan.share_rights must include create and write.");
        if (AddressBook.MaxEntries is <= 0) p.Add("address_book.max_entries must be above zero, or left out.");
        if (AddressBook.ManagedRange is { } r && (r.Count != 2 || r[0] > r[1] || r[0] < 0)) p.Add("address_book.managed_range must be two numbers, low then high.");
        if (Unofficial && string.IsNullOrWhiteSpace(Method)) p.Add("method must say where an unofficial method stands.");
        return p;
    }

    /// <summary>How many people this module can put on one printer.</summary>
    public int? EntryLimit()
    {
        int? range = AddressBook.ManagedRange is [var low, var high] ? high - low + 1 : null;
        return (AddressBook.MaxEntries, range) switch
        {
            ({ } max, { } r) => Math.Min(max, r),
            ({ } max, null) => max,
            (null, { } r) => r,
            _ => null,
        };
    }

    public bool MatchesWindowsName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return WindowsNames.Concat(Models).Any(pattern =>
            pattern.EndsWith('*')
                ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                : name.Equals(pattern, StringComparison.OrdinalIgnoreCase));
    }

    public bool WorksWithApp(Version app) =>
        System.Version.Parse(AppVersions.Min) <= app && (AppVersions.Max is null || app <= System.Version.Parse(AppVersions.Max));
}

public sealed class ModuleException(string message) : Exception(message);
