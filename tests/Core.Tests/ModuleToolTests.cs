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

    /// <summary>Packs the sample module at a given version, as a build of the repo would.</summary>
    private string Pack(string into, string version, string minApp = "0.1.0", string id = "scanrelay-sample")
    {
        var source = Path.Combine(_work, "src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.Copy(Path.Combine(SampleModule.BuildOutput(), "ScanRelay.Modules.Sample.dll"), Path.Combine(source, "ScanRelay.Modules.Sample.dll"));
        var data = File.ReadAllText(Path.Combine(SampleModule.BuildOutput(), "module.yaml"));
        data = System.Text.RegularExpressions.Regex.Replace(data, @"(?m)^version: .*$", "version: " + version);
        data = System.Text.RegularExpressions.Regex.Replace(data, @"(?m)^id: .*$", "id: " + id);
        data = System.Text.RegularExpressions.Regex.Replace(data, @"(?m)^(\s+)min: .*$", "$1min: " + minApp);
        File.WriteAllText(Path.Combine(source, "module.yaml"), data);
        Assert.Equal(0, Run("pack", source, into));
        return Path.Combine(into, $"{id}-{version}.srmod");
    }

    [Fact]
    public void Between_releases_only_new_and_newer_modules_are_published_and_the_rest_are_left_exactly_as_they_are()
    {
        var (published, packed) = (Path.Combine(_work, "published"), Path.Combine(_work, "packed"));
        var kept = Pack(published, "1.0.0");
        File.WriteAllText(kept + ".sig", "the signature it was published with");
        var older = Pack(published, "1.0.0", id: "another-module");
        File.WriteAllText(older + ".sig", "an old signature");
        var keptBytes = File.ReadAllBytes(kept);

        Pack(packed, "1.0.0");                              // unchanged: already out
        Pack(packed, "1.1.0", id: "another-module");        // a newer version
        Pack(packed, "0.1.0", id: "brand-new");             // never published

        Assert.Equal(0, Run("publish-set", packed, published, "0.2.8"));

        Assert.Equal(["another-module-1.1.0.srmod", "brand-new-0.1.0.srmod"], File.ReadAllLines(Path.Combine(published, "changed.txt")));
        Assert.Equal(keptBytes, File.ReadAllBytes(kept));
        Assert.Equal("the signature it was published with", File.ReadAllText(kept + ".sig"));
        Assert.False(File.Exists(older));
        Assert.False(File.Exists(older + ".sig"));
        Assert.Equal(["another-module-1.1.0.srmod", "brand-new-0.1.0.srmod", "scanrelay-sample-1.0.0.srmod"],
            Directory.GetFiles(published, "*.srmod").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("already published", _output.ToString());
    }

    [Fact]
    public void With_nothing_new_nothing_is_published()
    {
        var (published, packed) = (Path.Combine(_work, "published"), Path.Combine(_work, "packed"));
        Pack(published, "1.0.0");
        Pack(packed, "1.0.0");

        Assert.Equal(0, Run("publish-set", packed, published, "0.2.8"));

        Assert.Empty(File.ReadAllLines(Path.Combine(published, "changed.txt")));
        Assert.Contains("Nothing to publish", _output.ToString());
    }

    [Fact]
    public void A_version_that_goes_down_or_a_module_that_needs_a_newer_app_is_refused()
    {
        var (published, packed) = (Path.Combine(_work, "published"), Path.Combine(_work, "packed"));
        Pack(published, "1.2.0");
        Pack(packed, "1.1.0");
        var down = Assert.Throws<ModuleException>(() => Run("publish-set", packed, published, "0.2.8"));
        Assert.Contains("only goes up", down.Message);

        var (published2, packed2) = (Path.Combine(_work, "published2"), Path.Combine(_work, "packed2"));
        Pack(packed2, "1.0.0", minApp: "0.3.0");
        var tooNew = Assert.Throws<ModuleException>(() => Run("publish-set", packed2, published2, "0.2.8"));
        Assert.Contains("needs ScanRelay 0.3.0 or later", tooNew.Message);
        Assert.Empty(Directory.GetFiles(published2, "*.srmod"));
    }

    [Fact]
    public void The_module_contract_keeps_one_fixed_version_so_a_module_loads_in_older_apps()
    {
        // A module built beside app 0.2.8 was refused by app 0.2.7 when this followed the app's version.
        Assert.Equal(new Version(0, 1, 0, 0), typeof(ScanRelay.ModuleSdk.IDeviceAdapter).Assembly.GetName().Version);
    }

    [Fact]
    public void A_module_built_against_a_newer_contract_than_its_oldest_app_has_is_not_packed()
    {
        var into = Path.Combine(_work, "packed");

        // The sample is built against contract 0.1.0. Saying it works from an app older than that is the mistake being caught.
        var refused = Assert.Throws<ModuleException>(() => Pack(into, "1.0.0", minApp: "0.0.9"));

        Assert.Contains("would not load there", refused.Message);
        Assert.False(Directory.Exists(into) && Directory.GetFiles(into, "*.srmod").Length > 0);
    }
}
