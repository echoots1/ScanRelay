using ScanRelay.Core.Install;
using ScanRelay.Core.Modules;

namespace ScanRelay.Installer;

/// <summary>
/// Puts modules into the program's <c>Modules</c> folder and takes them out. Only setup
/// does this: it has administrator rights, and the service can read the folder but not write to it.
/// </summary>
public sealed class ModuleInstaller(string modulesFolder, TrustedKeys keys, SetupLog log, TimeProvider time)
{
    private readonly ModuleStore _store = new(modulesFolder, keys);

    public IReadOnlyList<InstalledModule> Installed => _store.Read();

    /// <summary>Removes modules by id. Asking to remove one that is not there is not an error.</summary>
    public void Remove(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            log.Line(_store.Remove(id) ? $"Removed the module {id}." : $"The module {id} was not installed, so there was nothing to remove.");
        }
    }

    /// <summary>Installs official modules from files, each checked against the <c>.sig</c> beside it. Returns their ids.</summary>
    public IReadOnlyList<string> InstallOfficialFiles(IEnumerable<string> files)
    {
        var ids = new List<string>();
        foreach (var file in files)
        {
            if (!File.Exists(file)) throw new SetupException($"The module file {file} was not found.");
            var signatureFile = file + ModulePackage.SignatureExtension;
            if (!File.Exists(signatureFile))
            {
                throw new SetupException($"The official module file {file} needs its signature file, {Path.GetFileName(signatureFile)}, beside it. Download both from the project's releases page.");
            }

            if (!keys.Any)
            {
                throw new SetupException("This copy of setup was built without the project's signing key, so it cannot check official modules.");
            }

            var module = Guarded(file, () => _store.Install(file, File.ReadAllText(signatureFile), allowCustom: false, time.GetUtcNow().UtcDateTime));
            log.Line($"Installed the module {module.Id} {module.Version} from {file}. Its signature is the project's.");
            ids.Add(module.Id);
        }

        return ids;
    }

    /// <summary>Installs modules the project has not checked. Only ever called with the admin's explicit agreement.</summary>
    public IReadOnlyList<string> InstallCustomFiles(IEnumerable<string> files, bool accepted)
    {
        var ids = new List<string>();
        foreach (var file in files)
        {
            if (!accepted)
            {
                throw new SetupException($"The custom module {file} was not installed. Custom modules are not checked by the project and must be accepted first.");
            }

            if (!File.Exists(file)) throw new SetupException($"The module file {file} was not found.");
            var module = Guarded(file, () => _store.Install(file, null, allowCustom: true, time.GetUtcNow().UtcDateTime));
            log.Line($"Installed the module {module.Id} {module.Version} from {file} as {module.Status}."
                + (module.Status == "official" ? "" : " It is not from the project and has not been checked by it."));
            ids.Add(module.Id);
        }

        return ids;
    }

    /// <summary>
    /// Brings catalog modules to the newest version that works with this app. GitHub is
    /// only contacted when there is something to ask it for.
    /// </summary>
    public async Task InstallFromCatalogAsync(
        HttpClient http,
        InstallConfig source,
        IReadOnlyCollection<string> requested,
        IReadOnlyCollection<string> removing,
        IReadOnlyCollection<string> fromFiles,
        bool appUpdate,
        Version app,
        CancellationToken ct)
    {
        var installed = Installed;
        var damaged = FindDamaged(installed);
        if (requested.Count == 0 && !(appUpdate && installed.Any(m => m.Status == "official"))) return;

        var missing = requested.Except(fromFiles).Where(id => damaged.Contains(id) || installed.All(m => m.Id != id)).ToList();
        var catalog = new ModuleCatalog(http, source, keys);
        CatalogIndex index;
        try
        {
            index = await catalog.FetchAsync(ct);
        }
        catch (Exception e) when (e is ModuleException or HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var reason = e is OperationCanceledException ? "GitHub did not answer in time." : e.Message;
            if (missing.Count > 0)
            {
                throw new SetupException(
                    $"The module list could not be fetched from GitHub ({source.Repository}), so {string.Join(", ", missing)} cannot be installed. {reason} "
                    + "Check this server's internet access, or leave the modules out and add them later from files.");
            }

            var note = $"The module list could not be fetched from GitHub, so the installed modules were left as they are. {reason}";
            if (appUpdate) log.Warn(note);
            else log.Line(note);
            return;
        }

        var plan = ModulePlan.Create(installed, damaged, index, requested, removing, fromFiles, appUpdate, app);
        if (plan.Problems.Count > 0) throw new SetupException(string.Join(" ", plan.Problems));
        foreach (var warning in plan.Warnings) log.Warn(warning);

        var downloads = Path.Combine(Path.GetTempPath(), "scanrelay-setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var entry in plan.Downloads)
            {
                log.Line($"Downloading the module {entry.Id} {entry.Version}.");
                try
                {
                    var (path, signature) = await catalog.DownloadPackageAsync(entry, downloads, ct);
                    var module = _store.Install(path, signature, allowCustom: false, time.GetUtcNow().UtcDateTime);
                    log.Line($"Installed the module {module.Id} {module.Version}. Its signature is the project's.");
                }
                catch (Exception e) when (e is ModuleException or HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    throw new SetupException($"The module {entry.Id} {entry.Version} could not be downloaded and installed. {e.Message}", e);
                }
            }
        }
        finally
        {
            if (Directory.Exists(downloads)) Directory.Delete(downloads, recursive: true);
        }
    }

    /// <summary>What an update would mean for the installed modules, without changing anything.</summary>
    public async Task<IReadOnlyList<string>> PreviewUpdateAsync(HttpClient http, InstallConfig source, IReadOnlyCollection<string> requested, Version app, CancellationToken ct)
    {
        var warnings = new List<string>();
        var installed = Installed;
        if (installed.Any(m => m.Status == "official"))
        {
            try
            {
                var index = await new ModuleCatalog(http, source, keys).FetchAsync(ct);
                warnings.AddRange(ModulePlan.Create(installed, FindDamaged(installed), index, requested, [], [], appUpdate: true, app).Warnings);
            }
            catch (Exception e) when (e is ModuleException or HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                warnings.Add("The module list could not be fetched from GitHub, so setup cannot tell whether the installed modules have versions for this release. "
                    + "If you continue, the installed modules are left as they are. " + (e is OperationCanceledException ? "" : e.Message));
            }
        }

        warnings.AddRange(CustomWarnings(app));
        return warnings;
    }

    /// <summary>Warnings for installed custom modules that this app will not load.</summary>
    public IReadOnlyList<string> CustomWarnings(Version app)
    {
        var warnings = new List<string>();
        foreach (var module in Installed.Where(m => m.Status != "official"))
        {
            try
            {
                if (ModulePlan.CustomWarning(ModulePackage.ReadData(Path.Combine(modulesFolder, module.File)), app) is { } warning) warnings.Add(warning);
            }
            catch (Exception e) when (e is ModuleException or IOException)
            {
                warnings.Add($"The custom module \"{module.Id}\" could not be read: {e.Message}");
            }
        }

        return warnings;
    }

    /// <summary>What each installed module's printers need on the share (<c>scan.share_rights</c>).</summary>
    public IReadOnlyList<IReadOnlyList<string>> ShareRights()
    {
        var all = new List<IReadOnlyList<string>>();
        foreach (var module in Installed)
        {
            try
            {
                all.Add(ModulePackage.ReadData(Path.Combine(modulesFolder, module.File)).Scan.ShareRights);
            }
            catch (Exception e) when (e is ModuleException or IOException)
            {
                // A module that cannot be read is not loaded by the service either, so it widens nothing.
                log.Warn($"The module \"{module.Id}\" could not be read, so its share rights were not applied: {e.Message}");
            }
        }

        return all;
    }

    private HashSet<string> FindDamaged(IReadOnlyList<InstalledModule> installed)
    {
        var damaged = new HashSet<string>();
        foreach (var module in installed)
        {
            var path = Path.Combine(modulesFolder, module.File);
            if (!File.Exists(path) || !ModulePackage.Sha256(path).Equals(module.Sha256, StringComparison.OrdinalIgnoreCase)) damaged.Add(module.Id);
        }

        return damaged;
    }

    private static InstalledModule Guarded(string file, Func<InstalledModule> install)
    {
        try
        {
            return install();
        }
        catch (ModuleException e)
        {
            throw new SetupException($"The module file {file} was not installed. {e.Message}", e);
        }
    }
}
