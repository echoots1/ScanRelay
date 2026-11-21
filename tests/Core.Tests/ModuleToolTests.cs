using ScanRelay.Core.Modules;
using ScanRelay.ModuleTool;

namespace ScanRelay.Core.Tests;

/// <summary>The release pipeline's steps, run the way the pipeline runs them.</summary>
[Collection("Environment variables")]
public sealed class ModuleToolTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "scanrelay-tool-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _output = new();

    public ModuleToolTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MODULE_SIGNING_KEY", null);
        Directory.Delete(_work, recursive: true);
    }

    private int Run(params string[] args) => ToolProgram.Run(args, _output);

    [Fact]
    public void A_release_packs_signs_and_indexes_modules_that_the_app_then_trusts()
    {
        var (privateKey, publicKey) = (Path.Combine(_work, "private.pem"), Path.Combine(_work, "public.pem"));
        var packages = Path.Combine(_work, "packages");
        var source = Path.Combine(_work, "sample");
        Directory.CreateDirectory(source);
        foreach (var file in new[] { "ScanRelay.Modules.Sample.dll", "module.yaml" })
        {
            File.Copy(Path.Combine(SampleModule.BuildOutput(), file), Path.Combine(source, file));
        }

        Assert.Equal(0, Run("keygen", privateKey, publicKey));
        Environment.SetEnvironmentVariable("MODULE_SIGNING_KEY", File.ReadAllText(privateKey).Replace("\n", "\\n"));
        Assert.Equal(0, Run("key-matches", publicKey));
        Assert.Equal(0, Run("check", Path.Combine(source, "module.yaml")));
        Assert.Equal(0, Run("pack", source, packages));
        var package = Path.Combine(packages, "scanrelay-sample-1.0.0.srmod");
        Assert.Equal(0, Run("sign", package));
        Assert.Equal(0, Run("index", packages));
        Assert.Equal(0, Run("verify", package, publicKey));

        var keys = new TrustedKeys([File.ReadAllText(publicKey)]);
        var installed = new ModuleStore(Path.Combine(_work, "installed"), keys).Install(package, File.ReadAllText(package + ".sig"), allowCustom: false, DateTime.UtcNow);
        Assert.Equal("official", installed.Status);
        var index = Path.Combine(packages, ModuleCatalog.IndexName);
        var catalog = new ModuleCatalog(new HttpClient(), new Core.Install.InstallConfig(), keys).Read(File.ReadAllBytes(index), File.ReadAllText(index + ".sig"));
        Assert.Equal(ModulePackage.Sha256(package), Assert.Single(catalog.Modules).Sha256);
    }

    [Fact]
    public void A_different_key_or_an_altered_file_is_caught()
    {
        var (privateKey, publicKey) = (Path.Combine(_work, "private.pem"), Path.Combine(_work, "public.pem"));
        Run("keygen", privateKey, publicKey);
        Environment.SetEnvironmentVariable("MODULE_SIGNING_KEY", ModuleSigning.NewKeyPair().PrivatePem);
        var file = Path.Combine(_work, "file.bin");
        File.WriteAllText(file, "content");

        Assert.Equal(1, Run("key-matches", publicKey));
        Assert.Equal(0, Run("sign", file));
        Assert.Equal(1, Run("verify", file, publicKey));
    }

    [Fact]
    public void Packing_the_same_folder_twice_gives_the_same_bytes()
    {
        var source = Path.Combine(_work, "sample");
        Directory.CreateDirectory(source);
        foreach (var file in new[] { "ScanRelay.Modules.Sample.dll", "module.yaml" })
        {
            File.Copy(Path.Combine(SampleModule.BuildOutput(), file), Path.Combine(source, file));
        }

        Run("pack", source, Path.Combine(_work, "a"));
        Run("pack", source, Path.Combine(_work, "b"));

        Assert.Equal(
            ModulePackage.Sha256(Path.Combine(_work, "a", "scanrelay-sample-1.0.0.srmod")),
            ModulePackage.Sha256(Path.Combine(_work, "b", "scanrelay-sample-1.0.0.srmod")));
    }
}
