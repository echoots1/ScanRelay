using ScanRelay.KyoceraProbe;

namespace ScanRelay.Core.Tests;

public class KyoceraProbeTests
{
    private const string Response = """
        <SOAP-ENV:Body><kmaddrbook:get_personal_address_listResponse><kmaddrbook:result>ALL_GET_COMPLETE</kmaddrbook:result>
        <kmaddrbook:personal_address><kmaddrbook:name_information><kmaddrbook:name>Real Person</kmaddrbook:name><kmaddrbook:id>7</kmaddrbook:id></kmaddrbook:name_information>
        <kmaddrbook:email_information><kmaddrbook:address>real.person@example.com</kmaddrbook:address></kmaddrbook:email_information>
        <kmaddrbook:smb_information><kmaddrbook:server_name>fileserver</kmaddrbook:server_name><kmaddrbook:file_path>scans\real</kmaddrbook:file_path><kmaddrbook:port_number>445</kmaddrbook:port_number><kmaddrbook:login_name>DOMAIN\svc</kmaddrbook:login_name><kmaddrbook:login_password>RealSecret!</kmaddrbook:login_password></kmaddrbook:smb_information></kmaddrbook:personal_address>
        <kmaddrbook:personal_address><kmaddrbook:name_information><kmaddrbook:name>ScanRelay test</kmaddrbook:name><kmaddrbook:id>900</kmaddrbook:id></kmaddrbook:name_information>
        <kmaddrbook:smb_information><kmaddrbook:server_name>testserver</kmaddrbook:server_name><kmaddrbook:file_path>share\test</kmaddrbook:file_path><kmaddrbook:port_number>445</kmaddrbook:port_number><kmaddrbook:login_name>tester</kmaddrbook:login_name><kmaddrbook:login_password>Throwaway1</kmaddrbook:login_password></kmaddrbook:smb_information></kmaddrbook:personal_address>
        </kmaddrbook:get_personal_address_listResponse></SOAP-ENV:Body>
        """;

    [Fact]
    public void The_report_keeps_the_structure_and_removes_everything_personal()
    {
        var report = Probe.Redact(Response);

        foreach (var personal in new[] { "Real Person", "real.person@example.com", "fileserver", @"scans\real", @"DOMAIN\svc", "RealSecret!" })
        {
            Assert.DoesNotContain(personal, report);
        }

        Assert.Contains("<kmaddrbook:login_password>(removed, 11 characters)</kmaddrbook:login_password>", report);
        Assert.Contains("<kmaddrbook:id>7</kmaddrbook:id>", report);
        Assert.Contains("<kmaddrbook:port_number>445</kmaddrbook:port_number>", report);
        Assert.Contains("ALL_GET_COMPLETE", report);
    }

    [Fact]
    public void A_test_entry_stays_readable_apart_from_its_password()
    {
        var report = Probe.Redact(Response);

        Assert.Contains("ScanRelay test", report);
        Assert.Contains("testserver", report);
        Assert.Contains(@"share\test", report);
        Assert.Contains("<kmaddrbook:login_name>tester</kmaddrbook:login_name>", report);
        Assert.DoesNotContain("Throwaway1", report);
    }

    [Fact]
    public void A_password_outside_an_entry_is_removed_too()
    {
        var report = Probe.Redact("<a:settings><a:admin_password>hunter2</a:admin_password></a:settings>");

        Assert.DoesNotContain("hunter2", report);
    }
}
