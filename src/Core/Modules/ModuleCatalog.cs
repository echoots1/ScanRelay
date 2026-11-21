using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScanRelay.Core.Install;

namespace ScanRelay.Core.Modules;

/// <summary>One module as the repo's index lists it.</summary>
public sealed record CatalogEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("vendor")] string Vendor,
    [property: JsonPropertyName("family")] string Family,
    [property: JsonPropertyName("models")] List<string> Models,
    [property: JsonPropertyName("windows_names")] List<string> WindowsNames,
    [property: JsonPropertyName("min_app")] string MinApp,
    [property: JsonPropertyName("max_app")] string? MaxApp,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("unofficial")] bool Unofficial,
    [property: JsonPropertyName("prerequisites")] List<string> Prerequisites)
{
    public bool WorksWithApp(Version app) =>
        System.Version.Parse(MinApp) <= app && (MaxApp is null || app <= System.Version.Parse(MaxApp));

    public bool Covers(string? windowsModelName) =>
        !string.IsNullOrWhiteSpace(windowsModelName) && WindowsNames.Concat(Models).Any(pattern =>
            pattern.EndsWith('*')
                ? windowsModelName.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                : windowsModelName.Equals(pattern, StringComparison.OrdinalIgnoreCase));
}

public sealed record CatalogIndex(
    [property: JsonPropertyName("generated")] DateTime Generated,
    [property: JsonPropertyName("modules")] List<CatalogEntry> Modules);

/// <summary>The signed index of every module, published with each release.</summary>
public sealed class ModuleCatalog(HttpClient http, InstallConfig install, TrustedKeys keys)
{
    public const string IndexName = "modules-index.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Builds the index for a folder of packages. Run by the release pipeline.</summary>
    public static string Build(string packagesFolder, DateTime nowUtc)
    {
        var modules = new List<CatalogEntry>();
        foreach (var path in Directory.EnumerateFiles(packagesFolder, "*" + ModulePackage.Extension).Order(StringComparer.Ordinal))
        {
            var d = ModulePackage.ReadData(path);
            modules.Add(new CatalogEntry(
                d.Id, d.Version, d.Vendor, d.Family, d.Models, d.WindowsNames, d.AppVersions.Min, d.AppVersions.Max,
                Path.GetFileName(path), ModulePackage.Sha256(path), d.Unofficial, d.Prerequisites));
        }

        return JsonSerializer.Serialize(new CatalogIndex(nowUtc, modules), Json);
    }

    /// <summary>Reads an index, refusing one that is not signed by the project.</summary>
    public CatalogIndex Read(byte[] indexBytes, string? signature)
    {
        if (!keys.Verify(indexBytes, signature)) throw new ModuleException("The module list is not signed by this project. It was ignored.");
        try
        {
            return JsonSerializer.Deserialize<CatalogIndex>(indexBytes) ?? throw new ModuleException("The module list is empty.");
        }
        catch (JsonException)
        {
            throw new ModuleException("The module list could not be read.");
        }
    }

    /// <summary>Downloads the index from the latest release.</summary>
    public async Task<CatalogIndex> FetchAsync(CancellationToken ct = default)
    {
        var index = await DownloadAsync(IndexName, ct);
        var signature = Encoding.UTF8.GetString(await DownloadAsync(IndexName + ModulePackage.SignatureExtension, ct));
        return Read(index, signature);
    }

    /// <summary>Downloads one package and its signature into a folder, and checks the file hash against the index.</summary>
    public async Task<(string Path, string Signature)> DownloadPackageAsync(CatalogEntry entry, string folder, CancellationToken ct = default)
    {
        if (entry.File.IndexOfAny(['/', '\\']) >= 0 || !entry.File.EndsWith(ModulePackage.Extension, StringComparison.Ordinal))
        {
            throw new ModuleException($"The module list names an unexpected file for {entry.Id}.");
        }

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, entry.File);
        await File.WriteAllBytesAsync(path, await DownloadAsync(entry.File, ct), ct);
        if (!ModulePackage.Sha256(path).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new ModuleException($"The download for {entry.Id} does not match the module list. It was not installed.");
        }

        var signature = Encoding.UTF8.GetString(await DownloadAsync(entry.File + ModulePackage.SignatureExtension, ct));
        return (path, signature);
    }

    private async Task<byte[]> DownloadAsync(string asset, CancellationToken ct)
    {
        var url = $"{install.Cloud.GitHubDownload.TrimEnd('/')}/{install.Repository}/releases/latest/download/{asset}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("ScanRelay");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new ModuleException($"GitHub answered {(int)response.StatusCode} for {asset}.");
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (HttpRequestException e)
        {
            throw new ModuleException("Could not reach GitHub: " + e.Message);
        }
    }
}
