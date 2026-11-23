namespace ScanRelay.Core.Settings;

/// <summary>Every setting on the Settings page, with the spec's defaults.</summary>
public sealed record AppSettings
{
    // Entra connection
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }

    /// <summary>
    /// The address people type when they reach the admin screen through a reverse proxy,
    /// such as https://scan.example.org. Empty when there is no proxy.
    /// </summary>
    public string? ProxyAddress { get; init; }

    // Who can open the admin screen
    public Guid? AdminGroupId { get; init; }
    public string? AdminGroupName { get; init; }
    public Guid? HelpdeskGroupId { get; init; }
    public string? HelpdeskGroupName { get; init; }

    // Who can scan
    public Guid? DefaultSyncGroupId { get; init; }
    public string? DefaultSyncGroupName { get; init; }
    public bool IncludeGuests { get; init; }

    /// <summary>Put each person's email address on their printer entry too, so scan to email works from the same entry.</summary>
    public bool IncludeEmail { get; init; } = true;
    public int SyncIntervalMinutes { get; init; } = 60;
    public int ChangeGuardLimit { get; init; } = 5;

    // Where scans go
    public string OneDriveFolder { get; init; } = "Documents/Scans";
    public string FileNameTemplate { get; init; } = "Scan_{yyyy-MM-dd_HHmmss}";
    public string AllowedFileTypes { get; init; } = "pdf";
    public int FileRetentionDays { get; init; } = 7;
    public int JobHistoryDays { get; init; } = 90;

    // Alerts and updates
    public string? AlertSender { get; init; }
    public bool UpdateCheck { get; init; } = true;

    /// <summary>True once an admin group is chosen and one of its members has signed in.</summary>
    public bool SetupComplete { get; init; }

    /// <summary>The proxy address in its plain form, or null when it is empty or not a usable address.</summary>
    public string? ProxyUrl() => NormalizeAddress(ProxyAddress);

    private static string? NormalizeAddress(string? address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath.Trim('/').Length > 0 || uri.Query.Length > 0 || uri.UserInfo.Length > 0) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public IReadOnlyList<string> AllowedExtensions() =>
        [.. AllowedFileTypes
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.TrimStart('.').ToLowerInvariant())
            .Distinct()];

    /// <summary>Returns problems that stop these settings being saved.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (SyncIntervalMinutes is < 5 or > 1440) problems.Add("Sync interval must be between 5 and 1440 minutes.");
        if (ChangeGuardLimit is < 0 or > 10000) problems.Add("Change guard limit must be between 0 and 10000.");
        if (FileRetentionDays is < 1 or > 365) problems.Add("File retention must be between 1 and 365 days.");
        if (JobHistoryDays is < 1 or > 3650) problems.Add("Job history must be between 1 and 3650 days.");
        if (JobHistoryDays < FileRetentionDays) problems.Add("Job history cannot be shorter than file retention.");
        if (AllowedExtensions().Count == 0) problems.Add("At least one file type must be allowed.");
        if (AllowedExtensions().Any(e => e.Any(c => !char.IsAsciiLetterOrDigit(c)))) problems.Add("File types may only contain letters and digits.");
        if (!string.IsNullOrWhiteSpace(ProxyAddress) && NormalizeAddress(ProxyAddress) is null)
        {
            problems.Add("The proxy address must be a full HTTPS address with no path, such as https://scan.example.org.");
        }

        if (!OneDrivePath.TryParse(OneDriveFolder, out _, out var folderError)) problems.Add(folderError!);
        if (!FileNaming.IsValidTemplate(FileNameTemplate, out var templateError)) problems.Add(templateError!);
        return problems;
    }
}

/// <summary>The OneDrive folder setting, split into where it starts and the folders under it.</summary>
public sealed record OneDrivePath(bool UnderDocuments, IReadOnlyList<string> Segments)
{
    private static readonly char[] Forbidden = ['"', '*', ':', '<', '>', '?', '|', '\\'];

    public static bool TryParse(string? value, out OneDrivePath? path, out string? error)
    {
        path = null;
        error = null;
        var parts = (value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "The OneDrive folder cannot be empty.";
            return false;
        }

        if (parts.Any(p => p.IndexOfAny(Forbidden) >= 0 || p is "." or ".." || p.EndsWith('.')))
        {
            error = "The OneDrive folder contains characters OneDrive does not allow.";
            return false;
        }

        // "Documents" is addressed through Graph's built-in reference, so it also
        // works in tenants where the folder has a translated name.
        var underDocuments = parts[0].Equals("Documents", StringComparison.OrdinalIgnoreCase);
        path = new OneDrivePath(underDocuments, underDocuments ? parts[1..] : parts);
        return true;
    }
}

public static class FileNaming
{
    public static bool IsValidTemplate(string? template, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(template))
        {
            error = "The file name template cannot be empty.";
            return false;
        }

        try
        {
            var name = Render(template, new DateTime(2026, 11, 20, 14, 32, 5));
            if (name.IndexOfAny(['"', '*', ':', '<', '>', '?', '|', '\\', '/']) >= 0)
            {
                error = "The file name template produces characters OneDrive does not allow.";
                return false;
            }
        }
        catch (FormatException)
        {
            error = "The file name template is not valid. Put a date format in braces, such as Scan_{yyyy-MM-dd_HHmmss}.";
            return false;
        }

        return true;
    }

    /// <summary>Fills each {date format} in the template with the given local time. No extension.</summary>
    public static string Render(string template, DateTime time)
    {
        var result = new System.Text.StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{')
            {
                var end = template.IndexOf('}', i);
                if (end < 0) throw new FormatException("Unclosed brace.");
                var format = template[(i + 1)..end];
                if (format.Length == 0) throw new FormatException("Empty braces.");
                result.Append(time.ToString(format, System.Globalization.CultureInfo.InvariantCulture));
                i = end + 1;
            }
            else if (c == '}')
            {
                throw new FormatException("Unopened brace.");
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        return result.ToString().Trim();
    }

    /// <summary>The name to try on the nth attempt: Scan.pdf, Scan (2).pdf, Scan (3).pdf.</summary>
    public static string WithSuffix(string baseName, string extension, int attempt) =>
        attempt <= 1 ? $"{baseName}.{extension}" : $"{baseName} ({attempt}).{extension}";
}
