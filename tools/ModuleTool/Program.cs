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

            case ["publish-set", var packedFolder, var publishedFolder, var appVersion]:
            {
                // For publishing modules between app releases. "published" holds the packages the latest release
                // already offers; "packed" holds a fresh build of every module. What is new, or a higher version,
                // moves across. A module already published at the same version is left exactly as it is, because
                // a file people may have downloaded must never change under them.
                var app = Version.Parse(appVersion);
                Directory.CreateDirectory(publishedFolder);
                var published = Directory.EnumerateFiles(publishedFolder, "*" + ModulePackage.Extension)
                    .Select(path => (Path: path, Data: ModulePackage.ReadData(path)))
                    .GroupBy(p => p.Data.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(p => Version.Parse(p.Data.Version)).First(), StringComparer.OrdinalIgnoreCase);
                var changed = new List<string>();
                foreach (var path in Directory.EnumerateFiles(packedFolder, "*" + ModulePackage.Extension).Order(StringComparer.Ordinal))
                {
                    var data = ModulePackage.ReadData(path);
                    var version = Version.Parse(data.Version);
                    if (published.TryGetValue(data.Id, out var now))
                    {
                        var held = Version.Parse(now.Data.Version);
                        if (version == held)
                        {
                            output.WriteLine($"{data.Id} {data.Version}: already published. Left as it is.");
                            continue;
                        }

                        if (version < held) throw new ModuleException($"{data.Id} is published at {now.Data.Version}, and this build is the older {data.Version}. A module's version only goes up.");
                    }

                    if (Version.Parse(data.AppVersions.Min) > app)
                    {
                        throw new ModuleException($"{data.Id} {data.Version} needs ScanRelay {data.AppVersions.Min} or later, and the latest release is {appVersion}. Publish it with that app release instead: "
                            + "the module list holds one version of each module, so this would take the module away from everyone on the current release.");
                    }

                    if (now.Path is not null)
                    {
                        File.Delete(now.Path);
                        File.Delete(now.Path + ModulePackage.SignatureExtension);
                    }

                    var target = Path.Combine(publishedFolder, Path.GetFileName(path));
                    File.Copy(path, target, overwrite: true);
                    changed.Add(Path.GetFileName(target));
                    output.WriteLine(now.Path is null ? $"{data.Id} {data.Version}: new." : $"{data.Id} {data.Version}: replaces {now.Data.Version}.");
                }

                // The pipeline signs and uploads exactly these.
                File.WriteAllLines(Path.Combine(publishedFolder, "changed.txt"), changed);
                output.WriteLine(changed.Count == 0 ? "Nothing to publish: every module is already out at its current version." : $"{changed.Count} module(s) to publish.");
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
                foreach (var path in new[] { privatePath, publicPath })
                {
                    if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder) Directory.CreateDirectory(folder);
                }

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
                    scanrelay-module publish-set <packed> <published> <app version>
                                                                            Move new and newer modules into the published set
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
