using System.Text;
using ScanRelay.Modules.CanonImageClass;

namespace ScanRelay.Core.Tests;

public class CanonAbkTests
{
    // The layout of a real export from an imageCLASS MF642C, with a made-up person in place of the real one.
    private const string Export =
        "# Canon AddressBook version: 1\r\n# CharSet: UTF8\r\n# dn: fixed\r\n# DB Version:\r\n\r\n" +
        "dn: 1\r\ncn:: UGF0IEV4YW1wbGU=\r\ncnread:: \r\nmailaddress: pat.example@example.com\r\nprotocol: smtp\r\nobjectclass: top\r\nobjectclass: extensibleobject\r\nobjectclass: email\r\n\r\n" +
        "dn: 2\r\ncn:: U2NhblJlbGF5IHRlc3Q=\r\ncnread:: \r\ncnshort:: \r\nurl:: c2NhbnNlcnZlci5leGFtcGxl\r\npath:: U2NhbnMk\r\nusername:: VGVzdEFjY291bnQ=\r\npwd:\r\nprotocol: smb\r\nobjectclass: top\r\nobjectclass: extensibleobject\r\nobjectclass: remotefilesystem\r\n\r\n\r\n";

    [Fact]
    public void A_real_export_is_read()
    {
        var file = AbkFile.Parse(Export);

        Assert.Equal(4, file.Header.Count);
        Assert.Equal(2, file.Entries.Count);
        var email = file.Entries[0];
        Assert.Equal((1, "Pat Example", true, "pat.example@example.com"), (email.Number, email.Name, email.IsEmail, email.MailAddress));
        var folder = file.Entries[1];
        Assert.Equal((2, "ScanRelay test", true), (folder.Number, folder.Name, folder.IsFolder));
        Assert.Equal(("scanserver.example", "Scans$", "TestAccount", ""), (folder.Host, folder.Path, folder.UserName, folder.Password));
    }

    [Fact]
    public void Writing_back_what_was_read_gives_the_same_bytes()
    {
        var bytes = Encoding.UTF8.GetBytes(Export);

        Assert.Equal(bytes, AbkFile.Parse(bytes).ToBytes());
    }

    [Fact]
    public void Entries_made_here_are_written_the_way_the_printer_writes_them()
    {
        var file = AbkFile.Parse(Export);
        file.Entries.Clear();
        file.Entries.Add(AbkEntry.Email(1, "Pat Example", "pat.example@example.com"));
        file.Entries.Add(AbkEntry.Folder(2, "ScanRelay test", "scanserver.example", "Scans$", "TestAccount", ""));

        Assert.Equal(Export, Encoding.UTF8.GetString(file.ToBytes()));
    }

    [Fact]
    public void Names_in_any_language_and_lines_this_module_does_not_know_survive()
    {
        var file = AbkFile.Parse(Export);
        file.Entries[0].Attributes.Add(new AbkAttribute("somethingnew", "kept", false));
        file.Entries.Add(AbkEntry.Folder(3, "Zoë Łukasz 山田", "server", "Scans$\\id", "srv\\acct", "p w:d"));

        var again = AbkFile.Parse(file.ToBytes());

        Assert.Equal("kept", again.Entries[0].Get("somethingnew"));
        Assert.Equal("Zoë Łukasz 山田", again.Entries[2].Name);
        Assert.Equal("p w:d", again.Entries[2].Password);
        Assert.Equal("Scans$\\id", again.Entries[2].Path);
    }

    [Theory]
    [InlineData("not an address book")]
    [InlineData("# Canon AddressBook version: 1\r\n\r\ndn 1\r\n")]
    [InlineData("# Canon AddressBook version: 1\r\n\r\ncn:: !!!notbase64\r\n")]
    public void A_file_that_is_not_an_address_book_is_refused(string text)
    {
        Assert.Throws<FormatException>(() => AbkFile.Parse(text));
    }
}
