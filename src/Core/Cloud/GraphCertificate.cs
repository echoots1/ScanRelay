using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ScanRelay.Core.Install;

namespace ScanRelay.Core.Cloud;

/// <summary>
/// The certificate the service uses to prove who it is to Entra. The installer
/// creates it in the machine store and gives the service read access to its key.
/// Where there is no installer (a development machine), a file in the data folder is used.
/// </summary>
public sealed class GraphCertificate(InstallConfig install, DataFolder dataFolder, TimeProvider time)
{
    private readonly Lock _lock = new();
    private X509Certificate2? _cached;

    public X509Certificate2 Get()
    {
        lock (_lock)
        {
            return _cached ??= Load();
        }
    }

    /// <summary>The public half, as Entra wants it when uploaded by hand or by setup.</summary>
    public string PublicKeyBase64() => Convert.ToBase64String(Get().Export(X509ContentType.Cert));

    public string PublicKeyPem() => Get().ExportCertificatePem();

    private X509Certificate2 Load()
    {
        if (!string.IsNullOrWhiteSpace(install.GraphCertThumbprint))
        {
            return FindInMachineStore(install.GraphCertThumbprint)
                ?? throw new InvalidOperationException(
                    "The Graph certificate chosen by the installer is not in the machine certificate store, or the service cannot read its key. Run the installer's Repair.");
        }

        var path = Path.Combine(dataFolder.Root, "graph-cert.pfx");
        if (File.Exists(path))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.EphemeralKeySet);
        }

        using var created = CreateSelfSigned("CN=ScanRelay Graph", time.GetUtcNow(), years: 2);
        File.WriteAllBytes(path, created.Export(X509ContentType.Pfx));
        return X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.EphemeralKeySet);
    }

    public static X509Certificate2? FindInMachineStore(string thumbprint)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var clean = new string([.. thumbprint.Where(char.IsAsciiHexDigit)]);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, clean, validOnly: false);
        return found.Count > 0 && found[0].HasPrivateKey ? found[0] : null;
    }

    public static X509Certificate2 CreateSelfSigned(string subject, DateTimeOffset now, int years, IEnumerable<string>? dnsNames = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        if (dnsNames is not null)
        {
            var names = new SubjectAlternativeNameBuilder();
            foreach (var name in dnsNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (System.Net.IPAddress.TryParse(name, out var ip)) names.AddIpAddress(ip);
                else names.AddDnsName(name);
            }

            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        }

        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(years));
    }
}
