using System.IO.Compression;
using ScanRelay.Modules.CanonImageClass;

namespace ScanRelay.Core.Tests;

/// <summary>
/// Works on a real export from an imageCLASS MF642C. The person, the server name and the
/// printer's own identifiers in it were replaced with made-up ones; the structure is untouched.
/// </summary>
public class CanonDcmTests
{
    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "canon-export.dcm"));

    private static byte[] Unzip(byte[] content)
    {
        using var gzip = new GZipStream(new MemoryStream(content), CompressionMode.Decompress);
        using var result = new MemoryStream();
        gzip.CopyTo(result);
        return result.ToArray();
    }

    [Fact]
    public void A_real_export_is_an_archive_of_two_readable_files()
    {
        var file = DcmFile.Read(Fixture());

        Assert.Equal(["dcm.xml", "addrbook.xml"], file.Files.Select(f => f.Name));
        var book = file.AddressBook();
        Assert.Equal(["Pat Example", "ScanRelay test", "ScanRelay import"], book.Entries.Select(e => e.Name));
        var folder = book.Entries[1];
        Assert.True(folder.IsFolder);
        Assert.Equal(("scanserver.example", "Scans$", "TestAccount"), (folder.Host, folder.Path, folder.UserName));
        Assert.StartsWith("X'3A083D34", folder.EncryptedPassword);
        Assert.False(book.Entries[0].IsFolder);
    }

    [Fact]
    public void Writing_back_what_was_read_gives_the_same_archive_byte_for_byte()
    {
        var original = Fixture();

        var written = DcmFile.Read(original).Write();

        Assert.Equal(Unzip(original), Unzip(written));
    }

    [Fact]
    public void The_address_book_alone_also_comes_back_byte_for_byte()
    {
        var file = DcmFile.Read(Fixture());
        var before = file.Files.Single(f => f.Name == DcmFile.AddressBookName).Content;

        file.SetAddressBook(file.AddressBook());

        Assert.Equal(before, file.Files.Single(f => f.Name == DcmFile.AddressBookName).Content);
    }

    [Fact]
    public void An_entry_can_be_added_carrying_another_entrys_encrypted_password()
    {
        var file = DcmFile.Read(Fixture());
        var book = file.AddressBook();
        var template = book.Entries[1];
        book.Entries.Add(CanonEntry.Folder(0, "Ann <A&B>", template.Host, "Scans$\\3f2b8a5e-0000-4000-8000-000000000000", template.UserName, template.EncryptedPassword));
        book.Renumber();
        file.SetAddressBook(book);

        var again = DcmFile.Read(file.Write()).AddressBook();

        Assert.Equal(4, again.Entries.Count);
        var added = again.Entries[3];
        Assert.Equal("4", added.Get("indexId"));
        Assert.Equal("Ann <A&B>", added.Name);
        Assert.Equal(template.EncryptedPassword, added.EncryptedPassword);
        Assert.Equal("Scans$\\3f2b8a5e-0000-4000-8000-000000000000", added.Path);
        Assert.Equal("Pat Example", again.Entries[0].Name);
    }

    [Fact]
    public void Removing_an_entry_closes_the_gap_in_the_numbers_as_the_printer_does()
    {
        var book = DcmFile.Read(Fixture()).AddressBook();
        book.Entries.RemoveAt(1);

        book.Renumber();

        Assert.Equal(["1", "2"], book.Entries.Select(e => e.Get("indexId")));
        Assert.Equal(["Pat Example", "ScanRelay import"], book.Entries.Select(e => e.Name));
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[0])]
    public void Something_that_is_not_a_configuration_file_is_refused(byte[] content)
    {
        Assert.Throws<FormatException>(() => DcmFile.Read(content));
    }
}
