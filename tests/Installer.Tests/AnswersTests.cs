using System.Text.Json;

namespace ScanRelay.Installer.Tests;

public class AnswersTests
{
    /// <summary>Answers as a Windows server would default them, whatever machine the tests run on.</summary>
    public static SetupAnswers Good() => new()
    {
        InstallFolder = @"C:\Program Files\ScanRelay",
        DataFolder = @"C:\ProgramData\ScanRelay",
        HostName = "scans.example.com",
    };

    [Fact]
    public void Defaults_are_valid()
    {
        var answers = new SetupAnswers { HostName = "server01" };
        Assert.Empty(answers.Validate());
        Assert.EndsWith(@"\ScanRelay", answers.InstallFolder);
        Assert.EndsWith(@"\ScanRelay", answers.DataFolder);
        Assert.Equal(8443, answers.Port);
        Assert.Equal("scanrelay-printer", answers.PrinterAccount);
        Assert.Equal("Scans$", answers.ShareName);
        Assert.Equal("echoots1/ScanRelay", answers.Repository);
        Assert.True(answers.UpdateCheck);
        Assert.True(answers.AddFirewallRule);
        Assert.Null(answers.WebCertThumbprint);
    }

    [Fact]
    public void Url_is_built_from_host_and_port()
    {
        var answers = Good();
        answers.Port = 9443;
        Assert.Equal("https://scans.example.com:9443", answers.Url);
        Assert.Equal("https://localhost:9443", answers.LocalUrl);
        Assert.Equal(@"C:\ProgramData\ScanRelay\Inbox", answers.InboxFolder);
        Assert.Equal(@"C:\Program Files\ScanRelay\Modules", answers.ModulesFolder);
    }

    [Theory]
    [InlineData("ScanRelay")]
    [InlineData(@"Program Files\ScanRelay")]
    [InlineData(@"\\server\share\ScanRelay")]
    [InlineData(@"C:ScanRelay")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Scan|Relay")]
    [InlineData(@"C:\Apps\..\Windows")]
    [InlineData("")]
    public void Folders_must_be_full_local_paths(string folder)
    {
        var install = Good();
        install.InstallFolder = folder;
        Assert.Contains(install.Validate(), p => p.StartsWith("The install folder"));

        var data = Good();
        data.DataFolder = folder;
        Assert.Contains(data.Validate(), p => p.StartsWith("The data folder"));
    }

    [Theory]
    [InlineData(@"C:\Apps\ScanRelay", @"C:\Apps\ScanRelay\Data", "cannot be inside the install folder")]
    [InlineData(@"C:\Apps\ScanRelay", @"c:\apps\scanrelay\", "cannot be inside the install folder")]
    [InlineData(@"D:\Data\ScanRelay\Program", @"D:\Data\ScanRelay", "cannot be inside the data folder")]
    public void Folders_cannot_be_inside_each_other(string install, string data, string expected)
    {
        var answers = Good();
        answers.InstallFolder = install;
        answers.DataFolder = data;
        Assert.Contains(answers.Validate(), p => p.Contains(expected));
    }

    [Fact]
    public void Folders_that_only_share_the_start_of_a_name_are_fine()
    {
        var answers = Good();
        answers.InstallFolder = @"C:\ScanRelay";
        answers.DataFolder = @"C:\ScanRelayData";
        Assert.Empty(answers.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Port_must_be_in_range(int port)
    {
        var answers = Good();
        answers.Port = port;
        Assert.Contains(answers.Validate(), p => p.Contains("port"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(443)]
    [InlineData(65535)]
    public void Any_real_port_is_accepted(int port)
    {
        var answers = Good();
        answers.Port = port;
        Assert.Empty(answers.Validate());
    }

    [Theory]
    [InlineData("Scans")]
    [InlineData("$")]
    [InlineData("Sc/ans$")]
    [InlineData("Sc\\ans$")]
    [InlineData("Scans:1$")]
    [InlineData("Sc*ans$")]
    [InlineData("ADMIN$")]
    [InlineData("C$")]
    [InlineData("")]
    public void Share_name_must_be_hidden_and_valid(string share)
    {
        var answers = Good();
        answers.ShareName = share;
        Assert.Contains(answers.Validate(), p => p.Contains("share name", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("this-name-is-21-chars")]
    [InlineData("scan/relay")]
    [InlineData("scan\\relay")]
    [InlineData("scan[relay]")]
    [InlineData("scan:relay")]
    [InlineData("scan;relay")]
    [InlineData("scan|relay")]
    [InlineData("scan=relay")]
    [InlineData("scan,relay")]
    [InlineData("scan+relay")]
    [InlineData("scan*relay")]
    [InlineData("scan?relay")]
    [InlineData("scan<relay>")]
    [InlineData("scan\"relay")]
    [InlineData("scanrelay.")]
    [InlineData(" scanrelay")]
    public void Printer_account_must_be_a_valid_local_account_name(string account)
    {
        var answers = Good();
        answers.PrinterAccount = account;
        Assert.Contains(answers.Validate(), p => p.Contains("printer account name"));
    }

    [Theory]
    [InlineData("scanrelay-printer")]
    [InlineData("this-name-is-20-char")]
    [InlineData("svc.scan_relay")]
    public void Ordinary_account_names_are_accepted(string account)
    {
        var answers = Good();
        answers.PrinterAccount = account;
        Assert.Empty(answers.Validate());
    }

    [Theory]
    [InlineData("https://scans.example.com")]
    [InlineData("scans.example.com:8443")]
    [InlineData("scans example")]
    [InlineData("-scans")]
    [InlineData("")]
    public void Host_name_is_a_bare_name(string host)
    {
        var answers = Good();
        answers.HostName = host;
        Assert.Contains(answers.Validate(), p => p.Contains("host name"));
    }

    [Fact]
    public void Web_certificate_thumbprint_is_forty_hex_digits_with_or_without_spaces()
    {
        var answers = Good();
        answers.WebCertThumbprint = "12 34 56 78 9a bc de f0 12 34 56 78 9a bc de f0 12 34 56 78";
        Assert.Empty(answers.Validate());
        Assert.Equal("123456789ABCDEF0123456789ABCDEF012345678", SetupAnswers.NormaliseThumbprint(answers.WebCertThumbprint));

        answers.WebCertThumbprint = "1234";
        Assert.Contains(answers.Validate(), p => p.Contains("thumbprint"));
    }

    [Fact]
    public void Custom_modules_need_to_be_accepted()
    {
        var answers = Good();
        answers.CustomModuleFiles = [@"C:\Temp\vendor-x.srmod"];
        Assert.Contains(answers.Validate(), p => p.Contains("AcceptCustomModules"));

        answers.AcceptCustomModules = true;
        Assert.Empty(answers.Validate());
    }

    [Fact]
    public void Module_lists_are_checked()
    {
        var answers = Good();
        answers.Modules = ["kyocera-taskalfa", "Not An Id"];
        answers.RemoveModules = ["kyocera-taskalfa"];
        answers.OfflineModuleFiles = [@"C:\Temp\module.zip"];
        var problems = answers.Validate();
        Assert.Contains(problems, p => p.Contains("\"Not An Id\" is not a module id"));
        Assert.Contains(problems, p => p.Contains("both to install and to remove"));
        Assert.Contains(problems, p => p.Contains("is not a module file"));
    }

    [Fact]
    public void Repository_looks_like_owner_and_name()
    {
        var answers = Good();
        answers.Repository = "https://github.com/someone/fork";
        Assert.Contains(answers.Validate(), p => p.Contains("repository"));
        answers.Repository = "someone/fork";
        Assert.Empty(answers.Validate());
    }

    [Fact]
    public void Answers_round_trip_through_json()
    {
        var answers = Good();
        answers.Port = 9000;
        answers.WebCertThumbprint = new string('A', 40);
        answers.Modules = ["scanrelay-sample"];
        answers.AddFirewallRule = false;
        answers.ReopenSetup = true;

        var back = SetupAnswers.FromJson(answers.ToJson());

        Assert.Equal(answers.ToJson(), back.ToJson());
        Assert.Equal(9000, back.Port);
        Assert.Equal(["scanrelay-sample"], back.Modules);
        Assert.False(back.AddFirewallRule);
        Assert.True(back.ReopenSetup);
    }

    [Fact]
    public void An_answers_file_may_leave_things_out_and_use_any_letter_case()
    {
        var answers = SetupAnswers.FromJson("""
            {
              // Only what differs from the defaults.
              "port": 9443,
              "hostName": "scans.example.com",
              "addFirewallRule": false,
            }
            """);
        Assert.Equal(9443, answers.Port);
        Assert.Equal("scans.example.com", answers.HostName);
        Assert.False(answers.AddFirewallRule);
        Assert.Equal("Scans$", answers.ShareName);
    }

    [Fact]
    public void A_mistyped_name_in_an_answers_file_is_an_error()
    {
        var e = Assert.Throws<SetupException>(() => SetupAnswers.FromJson("""{ "Prot": 9443 }"""));
        Assert.Contains("Prot", e.Message);
        Assert.Throws<SetupException>(() => SetupAnswers.FromJson("not json"));
    }

    [Fact]
    public void Answers_hold_no_password_or_other_secret()
    {
        var json = Good().ToJson();
        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(SetupAnswers).GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));

        // Computed values are not written either, so a hand-edited file cannot disagree with itself.
        Assert.DoesNotContain("Url", names);
        Assert.DoesNotContain("InboxFolder", names);
    }

    [Fact]
    public void Saved_answers_drop_the_one_off_requests()
    {
        var answers = Good();
        answers.Modules = ["wanted-but-failed"];
        answers.CustomModuleFiles = [@"C:\Temp\x.srmod"];
        answers.AcceptCustomModules = true;
        answers.OfflineModuleFiles = [@"C:\Temp\y.srmod"];
        answers.RemoveModules = ["old-one"];
        answers.ReopenSetup = true;
        answers.RenewGraphCertificate = true;

        var saved = answers.ForSaving(["zeta", "alpha"]);

        Assert.Equal(["alpha", "zeta"], saved.Modules);
        Assert.Empty(saved.CustomModuleFiles);
        Assert.Empty(saved.OfflineModuleFiles);
        Assert.Empty(saved.RemoveModules);
        Assert.False(saved.AcceptCustomModules);
        Assert.False(saved.ReopenSetup);
        Assert.False(saved.RenewGraphCertificate);
        Assert.Equal(answers.DataFolder, saved.DataFolder);
        Assert.True(answers.ReopenSetup);
    }
}
