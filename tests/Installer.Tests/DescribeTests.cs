namespace ScanRelay.Installer.Tests;

public class DescribeTests
{
    private static InstalledState Installed(string version = "0.1.0", Action<SetupAnswers>? change = null)
    {
        var answers = AnswersTests.Good();
        change?.Invoke(answers);
        return new InstalledState
        {
            Version = version,
            Answers = answers,
            GraphCertThumbprint = new string('1', 40),
            WebCertThumbprint = new string('2', 40),
            WebCertCreatedBySetup = true,
        };
    }

    private static string Describe(SetupAnswers answers, InstalledState? existing = null) =>
        SetupEngine.Describe(answers, existing, "0.1.0", "SERVER01");

    [Fact]
    public void A_new_install_names_everything_it_will_create()
    {
        var text = Describe(AnswersTests.Good());

        Assert.Contains("Setup will install ScanRelay 0.1.0", text);
        Assert.Contains("Nothing is changed until you confirm.", text);
        Assert.Contains(@"Copy the program to C:\Program Files\ScanRelay", text);
        Assert.Contains(@"Create the hidden data folder C:\ProgramData\ScanRelay with Inbox, Store, Logs, Keys and Backups inside.", text);
        Assert.Contains("Only the service and administrators can open it.", text);
        Assert.Contains("Create the local account \"scanrelay-printer\"", text);
        Assert.Contains("cannot sign in at the server or through Remote Desktop", text);
        Assert.Contains(@"Share C:\ProgramData\ScanRelay\Inbox as \\SERVER01\Scans$.", text);
        Assert.Contains("Only \"scanrelay-printer\" can use the share.", text);
        Assert.Contains("Create a self-signed certificate for the admin screen, for the names scans.example.com, server01 and localhost", text);
        Assert.Contains("Create the certificate ScanRelay signs in to Microsoft 365 with", text);
        Assert.Contains(@"Register the service ""ScanRelay"", running as its own low-privilege account NT SERVICE\ScanRelay.", text);
        Assert.Contains("Add the inbound rule \"ScanRelay admin screen\" for TCP port 8443.", text);
        Assert.Contains("https://scans.example.com:8443", text);
        Assert.Contains("No printer modules are installed or removed.", text);
        Assert.Contains("may check GitHub once a day", text);
    }

    [Fact]
    public void The_summary_follows_the_answers()
    {
        var answers = AnswersTests.Good();
        answers.Port = 9443;
        answers.AddFirewallRule = false;
        answers.UpdateCheck = false;
        answers.WebCertThumbprint = "ab cd " + new string('0', 36);
        answers.PrinterAccount = "mfp-drop";
        answers.ShareName = "Drop$";

        var text = Describe(answers);

        Assert.Contains("No firewall rule is added.", text);
        Assert.DoesNotContain("Add the inbound rule", text);
        Assert.Contains("will not check GitHub for updates", text);
        Assert.Contains("Use the certificate already on this server with thumbprint ABCD" + new string('0', 36), text);
        Assert.DoesNotContain("Create a self-signed certificate for the admin screen", text);
        Assert.Contains("\"mfp-drop\"", text);
        Assert.Contains(@"\\SERVER01\Drop$", text);
        Assert.Contains("https://scans.example.com:9443", text);
    }

    [Fact]
    public void Modules_are_listed_by_where_they_come_from()
    {
        var answers = AnswersTests.Good();
        answers.Modules = ["kyocera-taskalfa", "brother-mfc"];
        answers.OfflineModuleFiles = [@"C:\Temp\canon-ir.srmod"];
        answers.CustomModuleFiles = [@"C:\Temp\in-house.srmod"];
        answers.AcceptCustomModules = true;
        answers.RemoveModules = ["old-module"];

        var text = Describe(answers);

        Assert.Contains("From the project's list on GitHub (echoots1/ScanRelay), checked against the project's signature: kyocera-taskalfa, brother-mfc.", text);
        Assert.Contains(@"Official modules from files, checked against the project's signature: C:\Temp\canon-ir.srmod.", text);
        Assert.Contains(@"CUSTOM modules, which are not from the project and have not been checked by it: C:\Temp\in-house.srmod.", text);
        Assert.Contains("Remove: old-module.", text);
        Assert.DoesNotContain("No printer modules are installed or removed.", text);
    }

    [Fact]
    public void A_repair_keeps_what_is_there()
    {
        var text = Describe(AnswersTests.Good(), Installed());

        Assert.Contains("Setup will repair or change ScanRelay 0.1.0.", text);
        Assert.Contains(@"Replace the program files in C:\Program Files\ScanRelay", text);
        Assert.Contains(@"Keep the hidden data folder C:\ProgramData\ScanRelay", text);
        Assert.Contains("Keep the local account \"scanrelay-printer\" and its password.", text);
        Assert.Contains("Keep the certificate ScanRelay signs in to Microsoft 365 with.", text);
        Assert.Contains("Update the service \"ScanRelay\"", text);
        Assert.DoesNotContain("Back up the database", text);
        Assert.DoesNotContain("local setup screen is reopened", text);
    }

    [Fact]
    public void Repair_options_are_spelled_out()
    {
        var answers = AnswersTests.Good();
        answers.ReopenSetup = true;
        answers.RenewGraphCertificate = true;

        var text = Describe(answers, Installed());

        Assert.Contains("The local setup screen is reopened", text);
        Assert.Contains("Replace the certificate ScanRelay signs in to Microsoft 365 with.", text);
        Assert.Contains("until the new certificate is uploaded", text);
    }

    [Fact]
    public void An_update_says_so_and_backs_up_the_database()
    {
        var text = Describe(AnswersTests.Good(), Installed("0.0.9"));

        Assert.Contains("Setup will update ScanRelay from 0.0.9 to 0.1.0.", text);
        Assert.Contains(@"Back up the database to C:\ProgramData\ScanRelay\Backups first.", text);
    }

    [Fact]
    public void Changes_to_folders_account_share_and_firewall_are_called_out()
    {
        var answers = AnswersTests.Good();
        answers.InstallFolder = @"D:\Apps\ScanRelay";
        answers.DataFolder = @"D:\ScanRelayData";
        answers.PrinterAccount = "mfp-drop";
        answers.ShareName = "Drop$";
        answers.AddFirewallRule = false;

        var text = Describe(answers, Installed());

        Assert.Contains(@"Move the installed modules from C:\Program Files\ScanRelay and remove that folder.", text);
        Assert.Contains(@"Move the data folder from C:\ProgramData\ScanRelay to D:\ScanRelayData.", text);
        Assert.Contains("Create the local account \"mfp-drop\"", text);
        Assert.Contains("Delete the old account \"scanrelay-printer\".", text);
        Assert.Contains("Remove the old share Scans$.", text);
        Assert.Contains("the rule \"ScanRelay admin screen\" is removed", text);
    }

    [Fact]
    public void Describing_changes_nothing_and_needs_no_windows()
    {
        var answers = AnswersTests.Good();
        var before = answers.ToJson();
        var first = Describe(answers);
        var second = Describe(answers);
        Assert.Equal(first, second);
        Assert.Equal(before, answers.ToJson());
    }

    [Fact]
    public void Applying_is_refused_off_windows_before_anything_else()
    {
        if (OperatingSystem.IsWindows()) return;
        var engine = new SetupEngine(new SetupEnvironment { AppVersion = new Version(0, 1, 0), SetupExePath = "/tmp/ScanRelaySetup.exe", LogPath = Path.Combine(Path.GetTempPath(), "scanrelay-setup-test.log") });
        Assert.Throws<PlatformNotSupportedException>(() => { _ = engine.ApplyAsync(AnswersTests.Good()); });
    }

    [Fact]
    public void A_setup_without_program_files_says_how_to_fix_it()
    {
        var folder = Directory.CreateTempSubdirectory("scanrelay-setup-").FullName;
        try
        {
            var engine = new SetupEngine(new SetupEnvironment { AppVersion = new Version(0, 1, 0), SetupExePath = Path.Combine(folder, "ScanRelaySetup.exe") });
            var e = Assert.Throws<SetupException>(() => engine.OpenPayload());
            Assert.Contains("development build", e.Message);
            Assert.Contains("payload.zip", e.Message);

            File.WriteAllBytes(Path.Combine(folder, "payload.zip"), [1, 2, 3]);
            using var beside = engine.OpenPayload();
            Assert.Equal(3, beside.Length);

            var embedded = new SetupEngine(new SetupEnvironment
            {
                AppVersion = new Version(0, 1, 0),
                SetupExePath = Path.Combine(folder, "ScanRelaySetup.exe"),
                OpenEmbeddedPayload = () => new MemoryStream([9]),
            });
            using var inside = embedded.OpenPayload();
            Assert.Equal(1, inside.Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
