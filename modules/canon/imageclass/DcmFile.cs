using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ScanRelay.Modules.CanonImageClass;

/// <summary>
/// A Canon device configuration file (.dcm) as an imageCLASS MF642C exports it with
/// "Address Book" ticked: a gzip-compressed tar archive of XML files. The XML is plain
/// text. Only password values inside it are encrypted. Canon does not publish this layout;
/// it was read from a real export.
/// </summary>
public sealed class DcmFile(List<(string Name, byte[] Content)> files)
{
    public const string AddressBookName = "addrbook.xml";

    /// <summary>The files inside, in order. Files this module does not understand are kept as they are.</summary>
    public List<(string Name, byte[] Content)> Files { get; } = files;

    public static DcmFile Read(byte[] content)
    {
        try
        {
            using var gzip = new GZipStream(new MemoryStream(content), CompressionMode.Decompress);
            using var tar = new MemoryStream();
            gzip.CopyTo(tar);
            var bytes = tar.ToArray();
            var files = new List<(string, byte[])>();

            // Read by hand: the printer writes an old-style archive that is simple enough, and this
            // way exactly what was read can be written back.
            for (var offset = 0; offset + 512 <= bytes.Length && bytes[offset] != 0;)
            {
                var name = Encoding.ASCII.GetString(bytes, offset, 100).TrimEnd('\0');
                var size = Convert.ToInt32(Encoding.ASCII.GetString(bytes, offset + 124, 12).Trim(' ', '\0'), 8);
                if (offset + 512 + size > bytes.Length) throw new FormatException("The configuration file is cut short.");
                files.Add((name, bytes.AsSpan(offset + 512, size).ToArray()));
                offset += 512 + size + ((512 - (size % 512)) % 512);
            }

            if (files.Count == 0) throw new FormatException("The configuration file is empty.");
            return new DcmFile(files);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new FormatException("The file is not a Canon device configuration file.");
        }
    }

    public CanonBook AddressBook() => CanonBook.Parse(
        Files.FirstOrDefault(f => f.Name == AddressBookName).Content ?? throw new FormatException("The configuration file holds no address book."));

    /// <summary>Puts a changed address book in place of the one that was read.</summary>
    public void SetAddressBook(CanonBook book)
    {
        var index = Files.FindIndex(f => f.Name == AddressBookName);
        if (index < 0) throw new FormatException("The configuration file holds no address book.");
        Files[index] = (AddressBookName, book.ToBytes());
    }

    /// <summary>Writes the archive the way the printer does, field for field.</summary>
    public byte[] Write()
    {
        using var tar = new MemoryStream();
        foreach (var (name, content) in Files)
        {
            var header = new byte[512];
            Encoding.ASCII.GetBytes(name, header);
            Field(header, 100, 8, "664");
            Field(header, 108, 8, "177776");
            Field(header, 116, 8, "177776");
            Field(header, 124, 12, Convert.ToString(content.Length, 8));
            Field(header, 136, 12, "0");
            header.AsSpan(148, 8).Fill((byte)' ');
            Field(header, 148, 8, Convert.ToString(header.Sum(b => (int)b), 8));
            tar.Write(header);
            tar.Write(content);
            tar.Write(new byte[(512 - (content.Length % 512)) % 512]);
        }

        tar.Write(new byte[512]);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(tar.GetBuffer(), 0, (int)tar.Length);
        }

        return output.ToArray();
    }

    /// <summary>A number or mode, right-aligned with spaces and ended with a zero byte, as the printer writes them.</summary>
    private static void Field(byte[] header, int offset, int length, string value)
    {
        var text = value.PadLeft(length - 1);
        Encoding.ASCII.GetBytes(text, 0, length - 1, header, offset);
        header[offset + length - 1] = 0;
    }
}

/// <summary>One entry of the address book inside a configuration file. Every attribute is kept, in order.</summary>
public sealed class CanonEntry(List<(string Name, List<string> Values)> attributes)
{
    public List<(string Name, List<string> Values)> Attributes { get; } = attributes;

    public string Name => Get("cn");
    public string Protocol => Get("protocol");
    public bool IsFolder => Protocol.Equals("smb", StringComparison.OrdinalIgnoreCase);
    public string Host => Get("url");
    public string Path => Get("path");
    public string UserName => Get("username");

    /// <summary>The password as the printer stores it in the file: an encrypted value this module cannot read, only carry.</summary>
    public string EncryptedPassword => Get("pwd");

    public string Get(string name) => Attributes.FirstOrDefault(a => a.Name == name).Values?.FirstOrDefault() ?? "";

    public void Set(string name, string value)
    {
        var index = Attributes.FindIndex(a => a.Name == name);
        if (index >= 0) Attributes[index] = (name, [value]);
        else Attributes.Add((name, [value]));
    }

    /// <summary>A shared folder entry, with the attributes in the order an imageCLASS MF642C writes them.</summary>
    public static CanonEntry Folder(int number, string name, string host, string path, string userName, string encryptedPassword) => new(
    [
        ("objectclass", ["top", "remotefilesystem"]),
        ("subdbid", ["1"]),
        ("indexId", [number.ToString()]),
        ("cn", [name]),
        ("url", [host]),
        ("path", [path]),
        ("username", [userName]),
        ("pwd", [encryptedPassword]),
        ("protocol", ["smb"]),
    ]);
}

public sealed partial class CanonBook(string preamble, string openingTags, List<CanonEntry> entries, string closingTags)
{
    /// <summary>Names longer than this are cut by the printer.</summary>
    public const int MaxNameLength = 16;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public List<CanonEntry> Entries { get; } = entries;

    [GeneratedRegex("<entry>(.*?)</entry>", RegexOptions.Singleline)]
    private static partial Regex EntryPattern();

    [GeneratedRegex("<attr name=\"([^\"]*)\">(.*?)</attr>", RegexOptions.Singleline)]
    private static partial Regex AttributePattern();

    [GeneratedRegex("<value>(.*?)</value>", RegexOptions.Singleline)]
    private static partial Regex ValuePattern();

    public static CanonBook Parse(byte[] content)
    {
        // The printer puts two comments before the XML declaration, so this is not read as XML proper.
        var text = Utf8.GetString(content);
        var start = text.IndexOf("<importRequest>", StringComparison.Ordinal);
        var end = text.LastIndexOf("</importRequest>", StringComparison.Ordinal);
        if (start < 0 || end < start) throw new FormatException("The address book in the configuration file is not in the expected form.");
        var bodyStart = start + "<importRequest>".Length;
        var entries = EntryPattern().Matches(text[bodyStart..end]).Select(e => new CanonEntry(
            [.. AttributePattern().Matches(e.Groups[1].Value).Select(a => (
                a.Groups[1].Value,
                ValuePattern().Matches(a.Groups[2].Value).Select(v => WebUtility.HtmlDecode(v.Groups[1].Value)).ToList()))])).ToList();
        var newline = text.IndexOf('\n');
        return new CanonBook(newline >= 0 && newline < start ? text[..(newline + 1)] : "", text[(newline + 1)..bodyStart], entries, text[end..]);
    }

    /// <summary>Numbers the entries 1, 2, 3 in order, as the printer does when it loads a file.</summary>
    public void Renumber()
    {
        for (var i = 0; i < Entries.Count; i++) Entries[i].Set("indexId", (i + 1).ToString());
    }

    public byte[] ToBytes()
    {
        var text = new StringBuilder(preamble).Append(openingTags);
        foreach (var entry in Entries)
        {
            text.Append("<entry>");
            foreach (var (name, values) in entry.Attributes)
            {
                text.Append("<attr name=\"").Append(name).Append("\">");
                foreach (var value in values) text.Append("<value>").Append(Escape(value)).Append("</value>");
                text.Append("</attr>");
            }

            text.Append("</entry>");
        }

        return Utf8.GetBytes(text.Append(closingTags).ToString());
    }

    private static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
