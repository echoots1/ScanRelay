using System.Text.Json;
using ScanRelay.Core.Install;

namespace ScanRelay.Installer.Tests;

public class InstallFileTests
{
    [Fact]
    public void Install_json_reads_back_as_the_services_install_config()
    {
        var answers = AnswersTests.Good();
        answers.Port = 9443;
        answers.UpdateCheck = false;
        answers.Repository = "someone/fork";
        answers.ReopenSetup = true;

        var json = InstallFile.Serialize(InstallFile.Build(answers, null, "GRAPH", "WEB", "protected-password"));

        // Read exactly as the service would, with no special options.
        var config = JsonSerializer.Deserialize<InstallConfig>(json)!;
        Assert.Equal(@"C:\ProgramData\ScanRelay", config.DataFolder);
        Assert.Equal("https://scans.example.com:9443", config.Url);
        Assert.Equal("WEB", config.WebCertThumbprint);
        Assert.Equal("GRAPH", config.GraphCertThumbprint);
        Assert.Equal("scanrelay-printer", config.PrinterAccount);
        Assert.Equal("protected-password", config.PrinterPasswordProtected);
        Assert.Equal("Scans$", config.ShareName);
        Assert.Equal("scans.example.com", config.ServerName);
        Assert.Equal(@"C:\Program Files\ScanRelay\Modules", config.ModulesFolder);
        Assert.False(config.UpdateCheck);
        Assert.Equal("someone/fork", config.Repository);
        Assert.True(config.ReopenSetup);
        Assert.Equal("https://graph.microsoft.com/v1.0", config.Cloud.Graph);
    }

    [Fact]
    public void What_setup_does_not_own_is_carried_over()
    {
        var existing = new InstallConfig { PrinterAccount = "old", ReopenSetup = true };
        existing.Cloud.Login = "https://login.example.test";
        existing.Cloud.GitHubDownload = "https://downloads.example.test";

        var config = InstallFile.Build(AnswersTests.Good(), existing, "G", "W", "p");

        Assert.Equal("https://login.example.test", config.Cloud.Login);
        Assert.Equal("https://downloads.example.test", config.Cloud.GitHubDownload);
        Assert.Equal("scanrelay-printer", config.PrinterAccount);
        Assert.False(config.ReopenSetup);

        // The file that was read is not changed behind the caller's back.
        Assert.Equal("old", existing.PrinterAccount);
    }

    [Fact]
    public void The_file_is_written_and_read_from_the_program_folder()
    {
        var folder = Directory.CreateTempSubdirectory("scanrelay-setup-").FullName;
        try
        {
            Assert.Null(InstallFile.Read(folder));
            File.WriteAllText(Path.Combine(folder, InstallConfig.FileName), InstallFile.Serialize(InstallFile.Build(AnswersTests.Good(), null, "G", "W", "p")));
            Assert.Equal("G", InstallFile.Read(folder)!.GraphCertThumbprint);

            File.WriteAllText(Path.Combine(folder, InstallConfig.FileName), "{ damaged");
            Assert.Null(InstallFile.Read(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void State_round_trips_and_carries_no_password()
    {
        var folder = Directory.CreateTempSubdirectory("scanrelay-setup-").FullName;
        try
        {
            Assert.Null(InstalledState.ReadFrom(folder));
            var state = new InstalledState
            {
                Version = "0.1.0",
                Answers = AnswersTests.Good(),
                GraphCertThumbprint = "G",
                WebCertThumbprint = "W",
                WebCertCreatedBySetup = true,
                InstalledUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            };
            state.WriteTo(folder);

            var back = InstalledState.ReadFrom(folder)!;
            Assert.Equal("0.1.0", back.Version);
            Assert.Equal("scans.example.com", back.Answers.HostName);
            Assert.True(back.WebCertCreatedBySetup);
            Assert.Equal(state.InstalledUtc, back.InstalledUtc);
            Assert.DoesNotContain("password", File.ReadAllText(Path.Combine(folder, InstalledState.FileName)), StringComparison.OrdinalIgnoreCase);

            File.WriteAllText(Path.Combine(folder, InstalledState.FileName), "{ damaged");
            Assert.Null(InstalledState.ReadFrom(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_lost_state_file_is_rebuilt_from_install_json()
    {
        var answers = AnswersTests.Good();
        answers.Port = 9443;
        answers.PrinterAccount = "mfp-drop";
        var config = InstallFile.Build(answers, null, "G", "W", "p");

        var state = InstalledState.FromInstallConfig(config, @"D:\Apps\ScanRelay");

        Assert.Equal("", state.Version);
        Assert.Equal(@"D:\Apps\ScanRelay", state.Answers.InstallFolder);
        Assert.Equal(@"C:\ProgramData\ScanRelay", state.Answers.DataFolder);
        Assert.Equal("scans.example.com", state.Answers.HostName);
        Assert.Equal(9443, state.Answers.Port);
        Assert.Equal("mfp-drop", state.Answers.PrinterAccount);
        Assert.Equal("G", state.GraphCertThumbprint);
        Assert.Equal("W", state.WebCertThumbprint);
        Assert.False(state.WebCertCreatedBySetup);
    }
}
