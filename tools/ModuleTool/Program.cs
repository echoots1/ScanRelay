using System.Text;
using ScanRelay.Core.Modules;

namespace ScanRelay.ModuleTool;

/// <summary>
/// Packages and signs modules. The release pipeline uses it for official modules;
/// someone writing a custom module uses "pack" and installs the result through the installer.
/// </summary>
public static class ToolProgram
{
    private const string KeyVariable = "MODULE_SIGNING_KEY";

    public static int Main(string[] args)
    {
        try
        {
            return Run(args, Console.Out);
        }
        catch (Exception e) when (e is ModuleException or IOException or UnauthorizedAccessException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            Console.Error.WriteLine("Error: " + e.Message);
            return 1;
        }
    }

    public static int Run(string[] args, TextWriter output)
    {
        switch (args)
        {
            case ["pack", var folder, var outputFolder]:
            {
                // Build to a temporary name first, because the final name comes from the module data.
                Directory.CreateDirectory(outputFolder);
                var temporary = Path.Combine(outputFolder, Guid.NewGuid().ToString("N") + ".tmp");
                var data = ModulePackage.Create(folder, temporary);
                var final = Path.Combine(outputFolder, $"{data.Id}-{data.Version}{ModulePackage.Extension}");
                File.Move(temporary, final, overwrite: true);
                output.WriteLine($"Packed {data.Id} {data.Version} ({data.Status}) to {final}");
                return 0;
            }

            case ["check", var file]:
            {
                var data = file.EndsWith(ModulePackage.Extension, StringComparison.OrdinalIgnoreCase)
                    ? ModulePackage.ReadData(file)
                    : ModuleData.Parse(File.ReadAllText(file));
                output.WriteLine($"{data.Id} {data.Version}: the module data is valid.");
                return 0;
            }

            case ["keygen", var privatePath, var publicPath]:
            {
                var (privatePem, publicPem) = ModuleSigning.NewKeyPair();
                File.WriteAllText(privatePath, privatePem);
                File.WriteAllText(publicPath, publicPem);
                output.WriteLine($"Wrote the private key to {privatePath}. Store it as the {KeyVariable} secret and delete the file.");
                output.WriteLine($"Wrote the public key to {publicPath}. Commit it as catalog/signing-key.pub.pem.");
                return 0;
            }

            case ["sign", var file]:
            {
                File.WriteAllText(file + ModulePackage.SignatureExtension, ModuleSigning.Sign(PrivateKey(), File.ReadAllBytes(file)));
                output.WriteLine($"Signed {file}");
                return 0;
            }

            case ["index", var packagesFolder]:
            {
                var index = Path.Combine(packagesFolder, ModuleCatalog.IndexName);
                File.WriteAllText(index, ModuleCatalog.Build(packagesFolder, DateTime.UtcNow), new UTF8Encoding(false));
                File.WriteAllText(index + ModulePackage.SignatureExtension, ModuleSigning.Sign(PrivateKey(), File.ReadAllBytes(index)));
                output.WriteLine($"Wrote and signed {index}");
                return 0;
            }

            case ["verify", var file, var publicKeyFile]:
            {
                var signature = File.ReadAllText(file + ModulePackage.SignatureExtension);
                var ok = ModuleSigning.Verify(File.ReadAllText(publicKeyFile), File.ReadAllBytes(file), signature);
                output.WriteLine(ok ? $"{file}: the signature is good." : $"{file}: the signature does NOT match.");
                return ok ? 0 : 1;
            }

            case ["key-matches", var publicKeyFile]:
            {
                // The release pipeline checks the secret is the key the app was built to trust.
                var same = Normalise(ModuleSigning.PublicFromPrivate(PrivateKey())) == Normalise(File.ReadAllText(publicKeyFile));
                output.WriteLine(same ? "The signing key matches the committed public key." : "The signing key does NOT match the committed public key.");
                return same ? 0 : 1;
            }

            default:
                output.WriteLine("""
                    scanrelay-module pack <module folder> <output folder>   Build a module package (.srmod)
                    scanrelay-module check <module.yaml or .srmod>          Check module data against the schema
                    scanrelay-module keygen <private.pem> <public.pem>      Make a new signing key pair
                    scanrelay-module sign <file>                            Sign a file with the key in MODULE_SIGNING_KEY
                    scanrelay-module index <packages folder>                Write and sign modules-index.json
                    scanrelay-module verify <file> <public.pem>             Check a file's signature
                    scanrelay-module key-matches <public.pem>               Check MODULE_SIGNING_KEY against a public key
                    """);
                return args.Length == 0 ? 0 : 1;
        }
    }

    private static string PrivateKey()
    {
        var key = Environment.GetEnvironmentVariable(KeyVariable);
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException($"The {KeyVariable} environment variable is not set.");
        return key.Replace("\\n", "\n");
    }

    private static string Normalise(string pem) => new([.. pem.Where(c => !char.IsWhiteSpace(c))]);
}
