using Microsoft.Extensions.DependencyInjection;
using ScanRelay.Core.Modules;

namespace ScanRelay.Core.Tests;

/// <summary>Packages, signs and installs the sample module the way the release pipeline and the installer do.</summary>
public sealed class SampleModule
{
    public (string PrivatePem, string PublicPem) ProjectKey { get; } = ModuleSigning.NewKeyPair();
    public string WorkFolder { get; } = Path.Combine(Path.GetTempPath(), "scanrelay-mod-" + Guid.NewGuid().ToString("N"));
    public string ModulesFolder => Path.Combine(WorkFolder, "installed");
    public TrustedKeys Keys => new([ProjectKey.PublicPem]);

    public static string BuildOutput()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanRelay.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "modules", "scanrelay", "sample", "bin", configuration, "net10.0");
    }

    /// <summary>Builds a package, optionally changing its module data first. Returns the package path and its signature.</summary>
    public (string Path, string Signature) Pack(Func<string, string>? editYaml = null, string? signWith = null)
    {
        var source = Path.Combine(WorkFolder, "src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.Copy(Path.Combine(BuildOutput(), "ScanRelay.Modules.Sample.dll"), Path.Combine(source, "ScanRelay.Modules.Sample.dll"));
        var yaml = File.ReadAllText(Path.Combine(BuildOutput(), "module.yaml"));
        File.WriteAllText(Path.Combine(source, "module.yaml"), editYaml?.Invoke(yaml) ?? yaml);
        var package = Path.Combine(WorkFolder, Guid.NewGuid().ToString("N") + ModulePackage.Extension);
        ModulePackage.Create(source, package);
        return (package, ModuleSigning.Sign(signWith ?? ProjectKey.PrivatePem, File.ReadAllBytes(package)));
    }

    public InstalledModule InstallOfficial(Func<string, string>? editYaml = null)
    {
        var (path, signature) = Pack(editYaml);
        return new ModuleStore(ModulesFolder, Keys).Install(path, signature, allowCustom: false, DateTime.UtcNow);
    }

    public void Register(IServiceCollection services) => services.AddSingleton(Keys);

    public void Cleanup()
    {
        try
        {
            Directory.Delete(WorkFolder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
