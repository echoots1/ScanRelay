using ScanRelay.Core.Modules;
using ScanRelay.ModuleSdk;

namespace ScanRelay.Installer;

/// <summary>What to fetch from the catalog, what to warn about and what cannot be done.</summary>
/// <param name="Downloads">Catalog entries to download and install, replacing what is there.</param>
/// <param name="Warnings">Things the admin should know that do not stop setup.</param>
/// <param name="Problems">Requests that cannot be met. Setup stops on these.</param>
public sealed record ModulePlan(IReadOnlyList<CatalogEntry> Downloads, IReadOnlyList<string> Warnings, IReadOnlyList<string> Problems)
{
    /// <summary>
    /// Works out the catalog downloads. A requested module is brought to the newest version
    /// that works with this app; on an app update every installed official module is too.
    /// </summary>
    /// <param name="damaged">Installed modules whose file is missing or changed, which are fetched again.</param>
    /// <param name="fromFiles">Modules being installed from files in this run, which the catalog must not then overwrite.</param>
    public static ModulePlan Create(
        IReadOnlyList<InstalledModule> installed,
        IReadOnlySet<string> damaged,
        CatalogIndex catalog,
        IReadOnlyCollection<string> requested,
        IReadOnlyCollection<string> removing,
        IReadOnlyCollection<string> fromFiles,
        bool appUpdate,
        Version app)
    {
        var downloads = new List<CatalogEntry>();
        var warnings = new List<string>();
        var problems = new List<string>();

        var targets = requested
            .Concat(appUpdate ? installed.Where(m => m.Status == "official").Select(m => m.Id) : [])
            .Except(removing)
            .Except(fromFiles)
            .Distinct()
            .Order(StringComparer.Ordinal);

        foreach (var id in targets)
        {
            var have = installed.FirstOrDefault(m => m.Id == id);
            var listed = catalog.Modules.Where(m => m.Id == id).ToList();
            var best = listed.Where(m => Works(m, app)).OrderByDescending(m => ParseVersion(m.Version)).FirstOrDefault();

            if (best is null)
            {
                if (have is not null)
                {
                    warnings.Add(NoVersionWarning(id, have.Version, app));
                }
                else if (listed.Count == 0)
                {
                    problems.Add($"The module \"{id}\" is not in the project's module list.");
                }
                else
                {
                    problems.Add($"The module \"{id}\" has no version that works with ScanRelay {app}.");
                }

                continue;
            }

            var current = have is not null && have.Status == "official" && !damaged.Contains(id)
                && ParseVersion(have.Version) >= ParseVersion(best.Version);
            if (!current) downloads.Add(best);
        }

        return new ModulePlan(downloads, warnings, problems);
    }

    public static string NoVersionWarning(string id, string installedVersion, Version app) =>
        $"The module \"{id}\" (installed: {installedVersion}) has no version for ScanRelay {app}. "
        + "If you continue, pushes to its printers are paused until a matching module is released.";

    /// <summary>The warning for an installed custom module that this app will not load, or null when it is fine.</summary>
    public static string? CustomWarning(ModuleData data, Version app)
    {
        if (data.SdkVersion != ModuleSdkInfo.Version)
        {
            return $"The custom module \"{data.Id}\" was built for module SDK {data.SdkVersion}; this version of ScanRelay uses {ModuleSdkInfo.Version}. "
                + "Pushes to its printers are paused until its author provides a new build.";
        }

        return data.WorksWithApp(app)
            ? null
            : $"The custom module \"{data.Id}\" says it does not work with ScanRelay {app}. Pushes to its printers are paused until its author provides a new build.";
    }

    private static bool Works(CatalogEntry entry, Version app)
    {
        try
        {
            return entry.WorksWithApp(app);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
        {
            // A badly written entry is treated as not usable rather than stopping setup.
            return false;
        }
    }

    private static Version ParseVersion(string text) => Version.TryParse(text, out var v) ? v : new Version(0, 0);
}
