using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ScanRelay.Core.Modules;

/// <summary>
/// A module package: one zip file holding <c>module.yaml</c> and the adapter's files.
/// An official package has a signature made with the project's key.
/// </summary>
public static class ModulePackage
{
    public const string Extension = ".srmod";
    public const string SignatureExtension = ".sig";

    /// <summary>Reads and checks the module data inside a package.</summary>
    public static ModuleData ReadData(string packagePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(packagePath);
            var entry = zip.GetEntry(ModuleData.FileName) ?? throw new ModuleException("The package has no module.yaml.");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var data = ModuleData.Parse(reader.ReadToEnd());
            if (zip.GetEntry(data.Adapter) is null) throw new ModuleException($"The package does not contain its adapter, {data.Adapter}.");
            return data;
        }
        catch (InvalidDataException)
        {
            throw new ModuleException("The file is not a module package.");
        }
    }

    /// <summary>Builds a package from a folder. The same input always gives the same bytes.</summary>
    public static ModuleData Create(string folder, string packagePath)
    {
        var data = ModuleData.Parse(File.ReadAllText(Path.Combine(folder, ModuleData.FileName)));
        if (!File.Exists(Path.Combine(folder, data.Adapter))) throw new ModuleException($"The folder does not contain the adapter, {data.Adapter}.");
        File.Delete(packagePath);
        using var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var name = Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var target = entry.Open();
            using var source = File.OpenRead(file);
            source.CopyTo(target);
        }

        return data;
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

/// <summary>Signs and checks packages and the catalog index with an ECDSA P-256 key.</summary>
public static class ModuleSigning
{
    public static (string PrivatePem, string PublicPem) NewKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    public static string Sign(string privatePem, byte[] content)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privatePem);
        return Convert.ToBase64String(key.SignData(content, HashAlgorithmName.SHA256));
    }

    public static bool Verify(string publicPem, byte[] content, string signatureBase64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicPem);
            return key.VerifyData(content, Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    public static string PublicFromPrivate(string privatePem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privatePem);
        return key.ExportSubjectPublicKeyInfoPem();
    }
}

/// <summary>The public keys whose signatures count as official. Normally one: the project's.</summary>
public sealed class TrustedKeys(IReadOnlyList<string> publicPems)
{
    public bool Any => publicPems.Count > 0;

    public bool Verify(byte[] content, string? signatureBase64) =>
        !string.IsNullOrWhiteSpace(signatureBase64) && publicPems.Any(pem => ModuleSigning.Verify(pem, content, signatureBase64));

    /// <summary>The key built into this copy of the app. A fork that ships its own installer builds in its own.</summary>
    public static TrustedKeys BuiltIn()
    {
        using var stream = typeof(TrustedKeys).Assembly.GetManifestResourceStream("ScanRelay.Core.signing-key.pub.pem");
        if (stream is null) return new TrustedKeys([]);
        using var reader = new StreamReader(stream);
        var pem = reader.ReadToEnd();
        return new TrustedKeys(pem.Contains("BEGIN PUBLIC KEY") ? [pem] : []);
    }
}
