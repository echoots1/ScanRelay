using ScanRelay.CanonProbe;
using ScanRelay.MockDevices;

namespace ScanRelay.Core.Tests;

/// <summary>The probe reads its PIN from an environment variable, so it never runs beside another test that sets one.</summary>
[Collection("Environment variables")]
public sealed class CanonProbeTests : IAsyncLifetime
{
    private readonly string _folder = Directory.CreateTempSubdirectory("canon-probe-").FullName;
    private MockCanon _printer = null!;

    public async Task InitializeAsync()
    {
        _printer = await MockCanon.StartAsync();
        _printer.Entries.Add(new CanonMockEntry("ScanRelay", "scanserver", "Scans$", "scanrelay-printer", "the-share-password"));
        Environment.SetEnvironmentVariable("CANON_PIN", _printer.ManagerPin);
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CANON_PIN", null);
        await _printer.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private Task<int> RunAsync(params string[] more) =>
        CanonProbeProgram.Main([_printer.Address.Replace("http://", ""), "--plain", "--id", _printer.ManagerId, "--out", _folder, .. more]);

    [Fact]
    public async Task A_load_is_built_from_the_saved_copy_and_never_starts_with_an_export()
    {
        var folder = "Scans$\\" + Guid.NewGuid();
        Assert.Equal(1, await RunAsync("--add", "Someone", "--folder", folder));   // nothing saved yet
        Assert.Equal(0, _printer.Imports);

        Assert.Equal(0, await RunAsync("--save"));
        var (exports, before) = (_printer.Exports, _printer.Entries.ToList());
        Assert.Equal(0, await RunAsync("--add", "Someone", "--folder", folder));

        Assert.Equal(exports, _printer.Exports);
        var added = Assert.Single(_printer.Entries, e => e.Name == "Someone");
        Assert.Equal(("the-share-password", "scanrelay-printer"), (added.Password, added.UserName));
        Assert.Equal(before, _printer.Entries.Where(e => e.Name != "Someone"));

        Assert.Equal(0, await RunAsync("--restore"));
        Assert.Equal(before, _printer.Entries);
        Assert.Equal(exports, _printer.Exports);
        Assert.DoesNotContain("the-share-password", await File.ReadAllTextAsync(Path.Combine(_folder, "canon-probe-report.txt")));
    }
}
