using System.Text;

namespace ScanRelay.Modules.CanonImageClass;

/// <summary>One line of an entry: a name and a value, and whether the file holds the value as Base64.</summary>
public sealed record AbkAttribute(string Name, string Value, bool Encoded);

/// <summary>
/// One address book entry. Every line is kept as it was read, in order, including lines
/// this module does not understand, so an entry that is not ours goes back exactly as it came.
/// </summary>
public sealed class AbkEntry(List<AbkAttribute> attributes)
{
    public List<AbkAttribute> Attributes { get; } = attributes;

    public int Number => int.TryParse(Get("dn"), out var n) ? n : 0;
    public string Name => Get("cn") ?? "";
    public string Protocol => Get("protocol") ?? "";
    public bool IsFolder => Protocol.Equals("smb", StringComparison.OrdinalIgnoreCase);
    public bool IsEmail => Protocol.Equals("smtp", StringComparison.OrdinalIgnoreCase);
    public string Host => Get("url") ?? "";
    public string Path => Get("path") ?? "";
    public string UserName => Get("username") ?? "";
    public string Password => Get("pwd") ?? "";
    public string MailAddress => Get("mailaddress") ?? "";

    public string? Get(string name) => Attributes.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>A shared folder entry, with the lines in the order a Canon imageCLASS MF642C writes them.</summary>
    public static AbkEntry Folder(int number, string name, string host, string path, string userName, string password) => new(
    [
        new("dn", number.ToString(), false),
        new("cn", name, true),
        new("cnread", "", true),
        new("cnshort", "", true),
        new("url", host, true),
        new("path", path, true),
        new("username", userName, true),
        new("pwd", password, false),
        new("protocol", "smb", false),
        new("objectclass", "top", false),
        new("objectclass", "extensibleobject", false),
        new("objectclass", "remotefilesystem", false),
    ]);

    /// <summary>An email entry, with the lines in the order a Canon imageCLASS MF642C writes them.</summary>
    public static AbkEntry Email(int number, string name, string address) => new(
    [
        new("dn", number.ToString(), false),
        new("cn", name, true),
        new("cnread", "", true),
        new("mailaddress", address, false),
        new("protocol", "smtp", false),
        new("objectclass", "top", false),
        new("objectclass", "extensibleobject", false),
        new("objectclass", "email", false),
    ]);
}

/// <summary>
/// A Canon Address Book file (.abk), as exported by the Remote UI. The layout here comes
/// from a real export from an imageCLASS MF642C. Canon does not publish a description of it.
/// </summary>
public sealed class AbkFile(List<string> header, List<AbkEntry> entries)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The comment lines at the top, kept as they were.</summary>
    public List<string> Header { get; } = header;

    public List<AbkEntry> Entries { get; } = entries;

    public static AbkFile Parse(byte[] content) => Parse(Utf8.GetString(content));

    public static AbkFile Parse(string text)
    {
        var header = new List<string>();
        var entries = new List<AbkEntry>();
        List<AbkAttribute>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (current is { Count: > 0 }) entries.Add(new AbkEntry(current));
                current = null;
                continue;
            }

            if (line.StartsWith('#') && current is null && entries.Count == 0)
            {
                header.Add(line);
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0) throw new FormatException($"A line in the address book file is not in the expected form: \"{(line.Length > 40 ? line[..40] + "…" : line)}\"");
            var name = line[..colon];
            var encoded = colon + 1 < line.Length && line[colon + 1] == ':';
            var value = line[(colon + (encoded ? 2 : 1))..].Trim();
            if (encoded && value.Length > 0)
            {
                try
                {
                    value = Utf8.GetString(Convert.FromBase64String(value));
                }
                catch (FormatException)
                {
                    throw new FormatException($"The value of \"{name}\" in the address book file is not valid Base64.");
                }
            }

            (current ??= []).Add(new AbkAttribute(name, value, encoded));
        }

        if (current is { Count: > 0 }) entries.Add(new AbkEntry(current));
        if (header.Count == 0 || !header[0].Contains("Canon AddressBook", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("The file does not start like a Canon Address Book file.");
        }

        return new AbkFile(header, entries);
    }

    /// <summary>Writes the file the way the printer does: Windows line ends, no byte-order mark, a blank line after each entry and one more at the end.</summary>
    public byte[] ToBytes()
    {
        var text = new StringBuilder();
        foreach (var line in Header) text.Append(line).Append("\r\n");
        text.Append("\r\n");
        foreach (var entry in Entries)
        {
            foreach (var a in entry.Attributes)
            {
                text.Append(a.Name).Append(a.Encoded ? ":: " : a.Value.Length > 0 ? ": " : ":");
                if (a.Value.Length > 0) text.Append(a.Encoded ? Convert.ToBase64String(Utf8.GetBytes(a.Value)) : a.Value);
                text.Append("\r\n");
            }

            text.Append("\r\n");
        }

        text.Append("\r\n");
        return Utf8.GetBytes(text.ToString());
    }
}
