namespace ScanRelay.Core.Modules;

/// <summary>The two questions asked of a module wherever it is described: in its own data, and in the list on GitHub.</summary>
public static class ModuleRules
{
    /// <summary>Whether a printer's name, as Windows gives it, matches one of the patterns. A trailing * matches any ending.</summary>
    public static bool NameMatches(IEnumerable<string> patterns, string? name) =>
        !string.IsNullOrWhiteSpace(name) && patterns.Any(pattern =>
            pattern.EndsWith('*')
                ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                : name.Equals(pattern, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether an app version lies within the range a module names. No upper end means every later version.</summary>
    public static bool AppInRange(string min, string? max, Version app) =>
        Version.Parse(min) <= app && (max is null || app <= Version.Parse(max));
}
