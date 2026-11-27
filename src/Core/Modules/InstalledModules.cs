using System.Text.Json;

namespace ScanRelay.Core.Modules;

/// <summary>One line of the installer's record of what it put in the modules folder.</summary>
public sealed record InstalledModule(string Id, string Version, string File, string Sha256, string Status, DateTime InstalledUtc);

/// <summary>
/// The modules folder. Only the installer writes here: the service account can read
/// it but not change it, so the service trusts the installer's record of what was put there.
/// </summary>
public sealed class ModuleStore(string folder, TrustedKeys keys)
{
    public const string ManifestName = "modules.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Folder { get; } = folder;

    public IReadOnlyList<InstalledModule> Read()
    {
        var path = Path.Combine(Folder, ManifestName);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<InstalledModule>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException)
        {
            throw new ModuleException("The record of installed modules is damaged. Run the installer's Repair.");
        }
    }

    /// <summary>
    /// Checks a package before it is installed or loaded. Official packages need a good
    /// signature; anything else is custom and is only accepted when the caller allows it.
    /// </summary>
    public (ModuleData Data, string Status) Check(string packagePath, string? signatureBase64, bool allowCustom)
    {
        var data = ModulePackage.ReadData(packagePath);
        var official = keys.Verify(File.ReadAllBytes(packagePath), signatureBase64);
        if (official) return (data, ModuleStatus.Official);
        if (signatureBase64 is not null && !allowCustom)
        {
            throw new ModuleException($"The package for {data.Id} has been altered or is not signed by this project. It was not installed.");
        }

        if (!allowCustom) throw new ModuleException($"The package for {data.Id} is not signed by this project.");
        return (data, ModuleStatus.Custom);
    }

    /// <summary>Installs or replaces a module. Called by the installer, which has administrator rights.</summary>
    public InstalledModule Install(string packagePath, string? signatureBase64, bool allowCustom, DateTime nowUtc)
    {
        var (data, status) = Check(packagePath, signatureBase64, allowCustom);
        Directory.CreateDirectory(Folder);
        var fileName = data.Id + ModulePackage.Extension;
        var target = Path.Combine(Folder, fileName);
        if (!Path.GetFullPath(packagePath).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(packagePath, target, overwrite: true);
        }

        var sigPath = target + ModulePackage.SignatureExtension;
        if (status == ModuleStatus.Official) File.WriteAllText(sigPath, signatureBase64);
        else File.Delete(sigPath);

        var record = new InstalledModule(data.Id, data.Version, fileName, ModulePackage.Sha256(target), status, nowUtc);
        Write([.. Read().Where(m => m.Id != data.Id), record]);
        return record;
    }

    public bool Remove(string id)
    {
        var all = Read();
        var found = all.FirstOrDefault(m => m.Id == id);
        if (found is null) return false;
        File.Delete(Path.Combine(Folder, found.File));
        File.Delete(Path.Combine(Folder, found.File + ModulePackage.SignatureExtension));
        Write([.. all.Where(m => m.Id != id)]);
        return true;
    }

    private void Write(IEnumerable<InstalledModule> modules) =>
        File.WriteAllText(Path.Combine(Folder, ManifestName), JsonSerializer.Serialize(modules.OrderBy(m => m.Id).ToList(), Json));
}
