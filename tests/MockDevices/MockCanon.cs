using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ScanRelay.MockDevices;

/// <summary>One address book entry on the mock Canon. A shared folder has a host; an email entry has a mail address.</summary>
public sealed record CanonMockEntry(string Name, string Host = "", string Path = "", string UserName = "", string Password = "", string Mail = "");

/// <summary>
/// A fake imageCLASS Remote UI. Sign-in, the Export and Import pages and their requests copy a
/// real MF642C, including how the exported file is fetched through the progress page's form.
/// What the import's result page looks like is still a guess.
/// Like the real printer it takes the address book whole, renumbers it, cuts names to 16
/// characters, and keeps passwords in a form that depends on the file's password.
/// </summary>
public sealed partial class MockCanon : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, bool> _sessions = new();
    private string _token = "1000000001";
    private string? _exportPassword;
    private int _exportPolls;
    private string? _downloadKey;
    private string? _uploadKey;
    private int _importPolls;

    private MockCanon(WebApplication app) => _app = app;

    public List<CanonMockEntry> Entries { get; } =
    [
        new("Pat Example", Mail: "pat.example@example.com"),
        new("Shared folder", "fileserver", "public\\scans", "someone", "their-password"),
    ];

    public string ManagerId { get; set; } = "7654321";
    public string ManagerPin { get; set; } = "1357246";
    public string? AddressBookPin { get; set; }
    public int Capacity { get; set; } = 300;

    /// <summary>The real printer refuses a load for some minutes after an export.</summary>
    public TimeSpan BusyAfterExport { get; set; }
    private DateTime _lastExport;

    /// <summary>Set to have a load finish with this message and change nothing, as the real printer does with a file it cannot use.</summary>
    public string? ImportFailure { get; set; }
    public int Imports;
    public int Exports;
    public int Port { get; private set; }
    public string Address => $"http://127.0.0.1:{Port}";

    public static async Task<MockCanon> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var mock = new MockCanon(app);
        app.Run(mock.HandleAsync);
        await app.StartAsync();
        mock.Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        return mock;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    /// <summary>How the mock keeps a password in an exported file: readable only with the same file password.</summary>
    public static string Encrypt(string password, string filePassword) =>
        "X'" + Convert.ToHexString(Encoding.UTF8.GetBytes(filePassword + "\0" + password)) + "'";

    private static string? Decrypt(string stored, string filePassword)
    {
        if (stored.Length < 3) return "";
        var text = Encoding.UTF8.GetString(Convert.FromHexString(stored[2..^1]));
        var cut = text.IndexOf('\0');
        return cut >= 0 && text[..cut] == filePassword ? text[(cut + 1)..] : null;
    }

    private static string Page(string title, string body) =>
        $"<!DOCTYPE html><html><head><title>Remote UI: {title}: Test Printer: MF642C/643C/644C</title></head><body>{body}</body></html>";

    private string ProgressPage() => Page("",
        $"<form name=\"mImportDevMngEProg\" id=\"0\" action=\"/cgi/m_import_dev_mng_e_prog.cgi\" method=\"post\" onsubmit=\"return false;\">\n"
        + $"<input type=\"hidden\" name=\"prog\" id=\"prog\" value=\"prog\" />\n<input type=\"hidden\" name=\"iToken\" value=\"{NewToken()}\" />\n</form>"
        + "<p class=\"MainMessage\">Exporting...</p>");

    private string NewToken() => _token = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var path = request.Path.Value ?? "/";
        async Task Html(string html) { context.Response.ContentType = "text/html"; await context.Response.WriteAsync(html); }
        async Task Json(string json) { context.Response.ContentType = "application/json"; await context.Response.WriteAsync(json); }

        if (path == "/login.html")
        {
            await Html(Page("Login", "<form name=\"loginFrm\" action=\"/checkLogin.cgi\" method=\"post\"></form>"));
            return;
        }

        if (path == "/checkLogin.cgi")
        {
            var form = await request.ReadFormAsync();
            if (form["i0012"] == "1" && form["i0014"] == ManagerId && form["i0016"] == ManagerPin)
            {
                var session = Guid.NewGuid().ToString("N");
                _sessions[session] = true;
                context.Response.Cookies.Append("sessid", session);
                context.Response.Redirect("/portal_top.html");
            }
            else
            {
                context.Response.Redirect("/login.html?err=1");
            }

            return;
        }

        if (!request.Cookies.TryGetValue("sessid", out var id) || !_sessions.ContainsKey(id))
        {
            context.Response.Redirect("/login.html");
            return;
        }

        switch (path)
        {
            case "/portal_top.html":
                await Html(Page("Portal", "<a href=\"/logout.cgi\">Log Out</a>"));
                return;

            case "/logout.cgi":
                _sessions.TryRemove(id, out _);
                context.Response.Redirect("/login.html");
                return;

            case "/m_import_dev_mng_export.html":
            case "/m_import_dev_mng_import.html":
                await Html(Page(path.Contains("export") ? "Export" : "Import", $"<form><input type=\"hidden\" name=\"iToken\" value=\"{NewToken()}\"></form>"));
                return;

            case "/cgi/m_import_dev_mng_export.cgi":
            {
                var form = await request.ReadFormAsync();
                if (form["iToken"] != _token || !form["i2050"].Contains("1") || form["i2082"].ToString().Length == 0 || form["i2082"] != form["i2091"])
                {
                    context.Response.Redirect("/m_import_dev_mng_export.html?err=4");
                    return;
                }

                if (AddressBookPin is not null && form["i2102"] != AddressBookPin)
                {
                    context.Response.Redirect("/m_import_dev_mng_export.html?err=1");
                    return;
                }

                _exportPassword = form["i2082"];
                _exportPolls = 0;
                context.Response.Redirect("/m_import_dev_mng_e_prog.html");
                return;
            }

            case "/m_import_dev_mng_e_prog.html":
                await Html(ProgressPage());
                return;

            case "/cgi/m_import_dev_mng_e_prog.cgi":
            {
                // As on the real printer: the progress page's form is answered with the file once it is
                // ready, and with the progress page again until then.
                var form = await request.ReadFormAsync();
                if (form["iToken"] != _token || form["prog"] != "prog" || _exportPassword is null)
                {
                    await Html(Page("", "<div class=\"Glitch\"><p class=\"MainMessage\">File error. Try the operation again.</p></div>"));
                    return;
                }

                if (++_exportPolls < 2)
                {
                    await Html(ProgressPage());
                    return;
                }

                // Ready: the Export page again, now carrying the address the file is fetched from.
                _downloadKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                await Html(Page("Export",
                    $"<form><input type=\"hidden\" name=\"iToken\" value=\"{NewToken()}\"></form><script>document.location=\"{Address}/elf/dcm/export?pass={_downloadKey}&target=1&onError=/m_import_dev_mng_e_result.html\";</script>"));
                return;
            }

            case "/elf/dcm/export":
                if (_exportPassword is null || _downloadKey is null || request.Query["pass"] != _downloadKey || request.Query["target"] != "1")
                {
                    context.Response.Redirect("/m_import_dev_mng_e_result.html?code=2");
                    return;
                }

                Interlocked.Increment(ref Exports);
                _lastExport = DateTime.UtcNow;
                context.Response.ContentType = "application/octet-stream";
                context.Response.Headers.ContentDisposition = "attachment; filename=\"compact.dcm\"";
                await context.Response.Body.WriteAsync(BuildExport(_exportPassword));
                _exportPassword = null;
                _downloadKey = null;
                return;

            case "/json/dcm_export_result.json":
                // Asking here before the file has been fetched is what a first version of the module did. The real printer answers "File error".
                await Json("{\n\"result\":\"2\",\n\"url\":\"/m_import_dev_mng_e_result.html\",\n\"status\":\"0\"\n}");
                return;

            case "/m_import_dev_mng_e_result.html":
                await Html(Page("", "<div class=\"Glitch\"><p class=\"MainMessage\">File error. Try the operation again.</p></div>"));
                return;

            case "/cgi/m_import_dev_mng_import.cgi":
            {
                var form = await request.ReadFormAsync();
                if (form["iToken"] != _token || !form["i2070"].Contains("1") || form["i2052"].ToString().Length == 0)
                {
                    await Json("{\"status\":0,\"redirect\":\"/m_import_dev_mng_import.html?err=4\"}");
                    return;
                }

                if (AddressBookPin is not null && form["i2092"] != AddressBookPin)
                {
                    await Json("{\"status\":0,\"redirect\":\"/m_import_dev_mng_import.html?err=1\"}");
                    return;
                }

                if (DateTime.UtcNow - _lastExport < BusyAfterExport)
                {
                    await Json("{\"status\":0,\"redirect\":\"/m_import_dev_mng_import.html?err=3\"}");
                    return;
                }

                context.Response.Cookies.Append("importpw", Convert.ToBase64String(Encoding.UTF8.GetBytes(form["i2052"].ToString())));
                NewToken();
                _uploadKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                await Json($"{{\n\"url\":\"{Address}/elf/dcm/import?pass={_uploadKey}&target=1&onError=/json/dcm_import_result.json\",\n\"status\":\"0\"\n}}");
                return;
            }

            case "/elf/dcm/import":
            {
                // As the real printer answers: a file it cannot use goes to the result address with a code, which names the result page.
                // That it wants the form written the way a browser writes it is a guess, from a file .NET's own form sent being refused.
                var browserLike = !(request.ContentType ?? "").Contains("boundary=\"", StringComparison.Ordinal);
                if (_uploadKey is null || request.Query["pass"] != _uploadKey || !browserLike)
                {
                    context.Response.Redirect("/json/dcm_import_result.json?code=9");
                    return;
                }

                _uploadKey = null;
                var form = await request.ReadFormAsync();
                var file = form.Files["importData"];
                if (file is null || !file.ContentDisposition.Contains("name=\"importData\"", StringComparison.Ordinal) || file.ContentDisposition.Contains("filename*", StringComparison.Ordinal))
                {
                    context.Response.Redirect("/json/dcm_import_result.json?code=9");
                    return;
                }

                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer);
                var password = Encoding.UTF8.GetString(Convert.FromBase64String(request.Cookies["importpw"] ?? ""));
                if (ImportFailure is not null)
                {
                    context.Response.Redirect("/json/dcm_import_result.json?code=9");
                    return;
                }

                Load(buffer.ToArray(), password);
                _importPolls = 0;
                await Json("{\n\"status\":\"0\"\n}");
                return;
            }

            case "/json/heartbeat.json":
                await Json("{\"status\":0}");
                return;

            case "/json/dcm_import_result.json":
                // With a code: the result page for it. Without: empty while working, then the page that says it is complete.
                // The real printer also answers "complete" here when nothing was loaded at all, so this alone proves nothing.
                if (request.Query["code"].ToString() is { Length: > 0 } code) await Json($"{{\n\"redirect\":\"/m_import_dev_mng_i_result.html?code={code}\",\n\"status\":\"0\"\n}}");
                else await Json(++_importPolls < 2 ? "{\n\"redirect\":\"\",\n\"status\":\"0\"\n}" : "{\n\"redirect\":\"/m_import_dev_mng_i_result.html?code=1\",\n\"status\":\"0\"\n}");
                return;

            case "/m_import_dev_mng_i_result.html":
                await Html(Page("", request.Query["code"] == "1"
                    ? "<div class=\"Normal\"><p class=\"MainMessage\">Import is complete.<br>Restarting the device...<br>Wait a moment, then access the device again.</p></div>"
                    : $"<div class=\"Glitch\"><p class=\"MainMessage\">{ImportFailure ?? "Could not import the file."}</p></div>"));
                return;

            default:
                context.Response.StatusCode = 404;
                return;
        }
    }

    private byte[] BuildExport(string filePassword)
    {
        static string Attr(string name, params string[] values) =>
            $"<attr name=\"{name}\">{string.Concat(values.Select(v => $"<value>{System.Net.WebUtility.HtmlEncode(v)}</value>"))}</attr>";
        var book = new StringBuilder("<!-- <Version>2</Version> --><!-- <Attribute>pwd</Attribute> -->\n<?xml version=\"1.0\" encoding=\"UTF-8\" ?>")
            .Append("<importExportRequest xmlns:wsdl=\"http://schemas.xmlsoap.org/wsdl/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns=\"http://www.canon.com/ns/active/ImportAddressBook\"><importRequest>");
        lock (Entries)
        {
            for (var i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                book.Append("<entry>");
                if (e.Mail.Length > 0)
                {
                    book.Append(Attr("objectclass", "top", "person")).Append(Attr("subdbid", "1")).Append(Attr("indexId", (i + 1).ToString()))
                        .Append(Attr("cn", e.Name)).Append(Attr("mailaddress", e.Mail)).Append(Attr("protocol", "smtp"));
                }
                else
                {
                    book.Append(Attr("objectclass", "top", "remotefilesystem")).Append(Attr("subdbid", "1")).Append(Attr("indexId", (i + 1).ToString()))
                        .Append(Attr("cn", e.Name)).Append(Attr("url", e.Host)).Append(Attr("path", e.Path)).Append(Attr("username", e.UserName));
                    if (e.Password.Length > 0) book.Append(Attr("pwd", Encrypt(e.Password, filePassword)));
                    book.Append(Attr("protocol", "smb"));
                }

                book.Append("</entry>");
            }
        }

        book.Append("</importRequest></importExportRequest>");
        var settings = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?><device_configuration><dcm_settings><password crypted=\"true\">" + Encrypt("", filePassword) + "</password></dcm_settings></device_configuration>";

        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.V7, leaveOpen: true))
        {
            foreach (var (name, text) in new[] { ("dcm.xml", settings), ("addrbook.xml", book.ToString()) })
            {
                writer.WriteEntry(new V7TarEntry(TarEntryType.V7RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
            }
        }

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(tar.ToArray());
        return output.ToArray();
    }

    [GeneratedRegex("<entry>(.*?)</entry>", RegexOptions.Singleline)]
    private static partial Regex EntryPattern();

    /// <summary>Takes a loaded file the way the printer does: the whole book is replaced, in file order, names cut to 16.</summary>
    private void Load(byte[] file, string filePassword)
    {
        using var gzip = new GZipStream(new MemoryStream(file), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        string? book = null;
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.Name != "addrbook.xml" || entry.DataStream is null) continue;
            using var text = new StreamReader(entry.DataStream, Encoding.UTF8);
            book = text.ReadToEnd();
        }

        if (book is null) return;
        static string Value(string entry, string name) =>
            Regex.Match(entry, $"<attr name=\"{name}\"><value>(.*?)</value>", RegexOptions.Singleline) is { Success: true } m ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : "";
        var loaded = EntryPattern().Matches(book).Select(m => m.Groups[1].Value).Take(Capacity).Select(e =>
        {
            var name = Value(e, "cn");
            if (name.Length > 16) name = name[..16];
            return Value(e, "protocol") == "smtp"
                ? new CanonMockEntry(name, Mail: Value(e, "mailaddress"))
                : new CanonMockEntry(name, Value(e, "url"), Value(e, "path"), Value(e, "username"),

                    // A password stored under another file password cannot be read back, as on the real printer.
                    Decrypt(Value(e, "pwd"), filePassword) ?? "(unreadable)");
        }).ToList();
        lock (Entries)
        {
            Entries.Clear();
            Entries.AddRange(loaded);
        }

        Interlocked.Increment(ref Imports);
    }
}
