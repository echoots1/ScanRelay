using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using ScanRelay.Core.Install;

namespace ScanRelay.Installer;

/// <summary>The certificates for the admin screen and for signing in to Microsoft 365.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsSteps
{
    // ---- Certificates ----

    public static X509Certificate2? FindCertificate(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return null;
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, SetupAnswers.NormaliseThumbprint(thumbprint), validOnly: false);
        return found.Count > 0 ? found[0] : null;
    }

    /// <summary>The certificates an admin could pick for the admin screen: those in the machine store with a private key, leaving out the two setup makes itself.</summary>
    public static IReadOnlyList<X509Certificate2> ListWebCertificateChoices()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return [.. store.Certificates.Where(c => c.HasPrivateKey && c.FriendlyName is not (InstalledState.GraphCertFriendlyName or InstalledState.WebCertFriendlyName))];
    }

    /// <summary>Puts a new self-signed certificate in the machine store. Its key stays on this machine and cannot be exported.</summary>
    public static X509Certificate2 AddSelfSigned(SetupLog log, string subject, DateTimeOffset now, IEnumerable<string>? dnsNames, string friendlyName)
    {
        using var made = Core.Cloud.GraphCertificate.CreateSelfSigned(subject, now, years: 2, dnsNames);

        // Round-tripped through a PFX because that is how a key is made to persist in the
        // machine store. The password only guards the bytes inside this method.
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var pfx = made.Export(X509ContentType.Pfx, password);
        var keyFilesBefore = ListKeyFiles();
        var certificate = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        certificate.FriendlyName = friendlyName;
        using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);
        }

        log.Line($"Created the certificate {subject} ({friendlyName}), thumbprint {certificate.Thumbprint}, valid until {certificate.NotAfter:yyyy-MM-dd}, in the machine store. Its key cannot be exported.");

        // If Windows will not name the key's file, it is the one file that appeared while the key was being stored.
        var appeared = ListKeyFiles().Except(keyFilesBefore, StringComparer.OrdinalIgnoreCase).ToList();
        if (!GrantKeyRead(log, certificate, appeared.Count == 1 ? appeared[0] : null))
        {
            throw new SetupException($"Setup created the certificate {certificate.Thumbprint} but could not find its private key file under {CryptoFolder}, so it could not let the service read it.");
        }

        return certificate;
    }

    /// <summary>Removes a certificate and its key from the machine store.</summary>
    public static void RemoveCertificate(SetupLog log, string? thumbprint)
    {
        using var certificate = FindCertificate(thumbprint);
        if (certificate is null)
        {
            if (!string.IsNullOrWhiteSpace(thumbprint)) log.Line($"The certificate {thumbprint} was already gone.");
            return;
        }

        var keyFile = FindKeyFile(certificate);
        using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);
            store.Remove(certificate);
        }

        if (keyFile is not null)
        {
            try
            {
                File.Delete(keyFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Warn($"The certificate {certificate.Thumbprint} was removed, but its key file {keyFile} could not be deleted: {e.Message}");
            }
        }

        log.Line($"Removed the certificate {certificate.Subject} ({certificate.Thumbprint}) and its key.");
    }

    /// <summary>Lets the service use a certificate's private key. Returns false when the key is not a file setup can find.</summary>
    public static bool GrantKeyRead(SetupLog log, X509Certificate2 certificate, string? knownKeyFile = null)
    {
        var keyFile = FindKeyFile(certificate) ?? knownKeyFile;
        if (keyFile is null) return false;
        var info = new FileInfo(keyFile);
        var security = info.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(ServiceSid, FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(security);
        log.Line($"Gave {ServiceAccount.AccountName} read access to the private key of {certificate.Thumbprint} ({keyFile}).");
        return true;
    }

    /// <summary>The file that holds a machine certificate's private key, found from the key's unique name.</summary>
    public static string? FindKeyFile(X509Certificate2 certificate)
    {
        string? unique = null;
        try
        {
            using var rsa = certificate.GetRSAPrivateKey();
            unique = rsa switch
            {
                RSACng cng => cng.Key.UniqueName,
                RSACryptoServiceProvider csp => csp.CspKeyContainerInfo.UniqueKeyContainerName,
                _ => null,
            };
            if (unique is null)
            {
                using var ecdsa = certificate.GetECDsaPrivateKey();
                if (ecdsa is ECDsaCng ec) unique = ec.Key.UniqueName;
            }
        }
        catch (CryptographicException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(unique)) return null;
        return KeyFolders.Select(folder => Path.Combine(CryptoFolder, folder, unique)).FirstOrDefault(File.Exists);
    }

    /// <summary>Where Windows keeps the private keys of machine certificates.</summary>
    private static string CryptoFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Crypto");

    private static readonly string[] KeyFolders = ["Keys", @"RSA\MachineKeys", "SystemKeys", @"DSS\MachineKeys"];

    private static List<string> ListKeyFiles() =>
        [.. KeyFolders.Select(folder => Path.Combine(CryptoFolder, folder)).Where(Directory.Exists).SelectMany(Directory.EnumerateFiles)];
}
