using ScanRelay.BrotherProbe;
using ScanRelay.MockDevices;
using ScanRelay.Modules.BrotherAds;

namespace ScanRelay.Core.Tests;

/// <summary>The probe reads its password from an environment variable, so it never runs beside another test that sets one.</summary>
[Collection("Environment variables")]
public sealed class BrotherProbeTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Directory.CreateTempSubdirectory("brother-probe-").FullName, "not-made-yet");
    private MockBrother _scanner = null!;

    public async Task InitializeAsync()
    {
        _scanner = await MockBrother.StartAsync();
        _scanner.Profiles[0] = new BrotherMockProfile(0, "Someone Example", @"\\scanserver\Scans$\00000000-0000-4000-8000-000000000001", @"scanserver\scanrelay-printer", "the-share-password");
        _scanner.Profiles[3] = new BrotherMockProfile(1, "An FTP place");
        Environment.SetEnvironmentVariable("BROTHER_PASSWORD", _scanner.Password);
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("BROTHER_PASSWORD", null);
        await _scanner.DisposeAsync();
        Directory.Delete(Path.GetDirectoryName(_folder)!, recursive: true);
    }

    private Task<int> RunAsync() => BrotherProbeProgram.Main([_scanner.Address.Replace("http://", ""), "--plain", "--out", _folder]);

    [Fact]
    public async Task The_probe_reads_the_profiles_and_writes_no_password_or_name_into_its_report()
    {
        Assert.Equal(0, await RunAsync());

        var report = await File.ReadAllTextAsync(Path.Combine(_folder, "brother-probe-report.txt"));
        Assert.Contains("Model: ADS-4900W", report);
        Assert.Contains("25 slots, 2 in use", report);
        Assert.Contains("Profile 1: Network", report);
        Assert.Contains("Profile 4: FTP", report);
        Assert.Contains(@"scanserver\scanrelay-printer", report);
        Assert.DoesNotContain("the-share-password", report);
        Assert.DoesNotContain(_scanner.Password, report);
        Assert.DoesNotContain("Someone Example", report);
        Assert.DoesNotContain("An FTP place", report);
        Assert.Contains("RESULT: worked.", report);
    }

    [Fact]
    public async Task A_wrong_password_is_reported_and_nothing_is_read()
    {
        Environment.SetEnvironmentVariable("BROTHER_PASSWORD", "not-it");

        Assert.Equal(1, await RunAsync());

        var report = await File.ReadAllTextAsync(Path.Combine(_folder, "brother-probe-report.txt"));
        Assert.Contains("refused the Web Based Management password", report);
        Assert.DoesNotContain("not-it", report);
    }

    [Fact]
    public void Passwords_pins_and_profile_names_are_left_out_of_what_is_written_down()
    {
        var safe = BrotherWebUi.WithoutSecrets("<Root><ProfileName>Ann Example</ProfileName><FolderUser>srv\\acct</FolderUser><FolderPass>s3cret!</FolderPass><PINCode>1234</PINCode><SecPDF_PIN></SecPDF_PIN><DuplexScan>1</DuplexScan></Root>");

        Assert.Equal("<Root><ProfileName>(a name, 11 characters)</ProfileName><FolderUser>srv\\acct</FolderUser><FolderPass>(hidden, 7 characters)</FolderPass><PINCode>(hidden, 4 characters)</PINCode><SecPDF_PIN></SecPDF_PIN><DuplexScan>1</DuplexScan></Root>", safe);
    }
}
