using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScanRelay.Modules.CanonImageClass;

public sealed class CanonException(string message) : Exception(message);

/// <summary>
/// Signs in to an imageCLASS Remote UI as System Manager and uses its Export and Import
/// pages, the way a person does in a browser. Canon does not document this. The requests
/// follow the Remote UI pages of a real MF642C (main controller 10.05).
/// </summary>
public sealed partial class CanonRemoteUi : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;

    public CanonRemoteUi(string address, bool secure = true, HttpMessageHandler? handler = null)
    {
        _base = address.Contains("://", StringComparison.Ordinal) ? address.TrimEnd('/') : $"{(secure ? "https" : "http")}://{address}";

        // The printer presents a certificate it made itself, so it cannot be checked against anything.
        // A cookie jar per connection keeps one printer's session apart from another's.
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    /// <summary>Every request and answer, with secrets left out. The probe tool prints it; the service does not keep it.</summary>
    public Action<string>? Trace { get; init; }

    /// <summary>How long to wait between asking the printer whether it has finished.</summary>
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromMilliseconds(1500);

    [GeneratedRegex("name=\"iToken\"\\s+value=\"([^\"]*)\"")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("<title>([^<]*)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitlePattern();

    [GeneratedRegex("(?:href|action|src)=\"([^\"]+)\"|location(?:\\.href)?\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    [GeneratedRegex("<form[^>]*action=\"(?<action>[^\"]+)\"[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FormPattern();

    [GeneratedRegex("<input type=\"hidden\" name=\"([^\"]+)\"[^>]*value=\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenPattern();

    [GeneratedRegex("(?:https?://[^\"'\\s<>]+)?/elf/dcm/export\\?[^\"'\\s<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadPattern();

    [GeneratedRegex("<p class=\"MainMessage\">([^<]*)</p>", RegexOptions.IgnoreCase)]
    private static partial Regex MessagePattern();

    [GeneratedRegex("name=\"(i\\d+)\"[^>]*type=\"password\"|type=\"password\"[^>]*name=\"(i\\d+)\"")]
    private static partial Regex PasswordFieldPattern();

    /// <summary>The model names from the sign-in page's title, such as "MF642C/643C/644C". Needs no sign-in.</summary>
    public async Task<string?> ReadModelAsync(CancellationToken ct)
    {
        var (_, html, _) = await GetAsync("/login.html", ct);
        var title = TitlePattern().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : "";
        var last = title.LastIndexOf(':');
        return last >= 0 && title.Contains("Remote UI", StringComparison.OrdinalIgnoreCase) ? title[(last + 1)..].Trim() : null;
    }

    public async Task SignInAsync(string managerId, string managerPin, CancellationToken ct)
    {
        var (status, _, final) = await PostFormAsync("/checkLogin.cgi",
            [("i0012", "1"), ("i0014", managerId), ("i0016", managerPin)], ct, secret: "i0016");
        if (final.Contains("login.html", StringComparison.OrdinalIgnoreCase) || status >= 400)
        {
            var reason = final.Contains("err=3", StringComparison.Ordinal)
                ? "The printer does not allow System Manager sign-in while its System Manager PIN is unset or still the factory value. Set an ID and PIN on the printer first."
                : "The printer refused the System Manager ID and PIN.";
            throw new CanonException(reason);
        }
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        try
        {
            await GetAsync("/logout.cgi", ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // The session ends by itself after a while.
        }
    }

    /// <summary>Exports the address book as an encrypted configuration file, protected with the given password.</summary>
    public async Task<byte[]> ExportAddressBookAsync(string filePassword, string? addressBookPin, CancellationToken ct)
    {
        var (_, page, _) = await GetAsync("/m_import_dev_mng_export.html", ct);
        var fields = new List<(string, string)>
        {
            ("iToken", Token(page, "Export")),
            ("i2130", "0"),
            ("i2050", "0"), ("i2050", "1"),
            ("i2060", "0"),
            ("i2140", "0"),
            ("i2082", filePassword),
            ("i2091", filePassword),
        };
        if (!string.IsNullOrEmpty(addressBookPin)) fields.Add(("i2102", addressBookPin));

        var (file, html, where) = await SendForFileAsync(
            new HttpRequestMessage(HttpMethod.Post, _base + "/cgi/m_import_dev_mng_export.cgi") { Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Item1, f.Item2))) },
            "POST /cgi/m_import_dev_mng_export.cgi (address book, with a file password)", ct);

        // The printer answers with an "Exporting..." page holding a small form, which the browser sends
        // after a moment. The answer to that form is the file, or the same page again while the printer
        // is still working. This is what a real MF642C does.
        for (var attempt = 0; attempt < 60 && file is null; attempt++)
        {
            // When the file is ready the printer shows the Export page again, this time carrying the
            // address the file is fetched from.
            if (DownloadPattern().Match(html) is { Success: true } ready)
            {
                var address = WebUtility.HtmlDecode(ready.Value);
                (file, html, where) = await SendForFileAsync(
                    new HttpRequestMessage(HttpMethod.Get, Absolute(address)), "GET " + address[..address.IndexOf('?')] + " (the address the Export page gives for the file)", ct);
                if (file is null)
                {
                    var said = MessagePattern().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "nothing this module knows";
                    throw new CanonException($"The printer prepared the export but did not hand over the file. It said: \"{said}\"");
                }

                break;
            }

            if (where.Contains("m_import_dev_mng_export.html", StringComparison.Ordinal))
            {
                throw new CanonException(ExplainRefusal(where, "export"));
            }

            var form = FormPattern().Match(html);
            if (!form.Success || !form.Groups["action"].Value.Contains("e_prog", StringComparison.Ordinal))
            {
                var said = MessagePattern().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
                throw new CanonException(said is null
                    ? "The export did not produce a file, and the printer's page was not one this module knows. This printer's Remote UI may differ from the one the module was written for."
                    : $"The printer could not export its address book. It said: \"{said}\"");
            }

            await Task.Delay(PollEvery, ct);
            var hidden = HiddenPattern().Matches(form.Value).Select(h => KeyValuePair.Create(h.Groups[1].Value, WebUtility.HtmlDecode(h.Groups[2].Value)));
            var action = form.Groups["action"].Value;
            (file, html, where) = await SendForFileAsync(
                new HttpRequestMessage(HttpMethod.Post, Absolute(action)) { Content = new FormUrlEncodedContent(hidden) }, "POST " + action + " (the progress page's own form)", ct);
        }

        return file ?? throw new CanonException("The printer did not finish preparing the export in time.");
    }

    /// <summary>Loads an encrypted configuration file's address book onto the printer. It replaces the whole address book.</summary>
    /// <summary>How long a load keeps asking while the printer says it is busy, as it does for some minutes after an export.</summary>
    public TimeSpan WaitWhileBusy { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan BusyRetryEvery { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Called each time a load has to wait.</summary>
    public Action? Waiting { get; init; }

    public async Task ImportAddressBookAsync(byte[] file, string filePassword, string? addressBookPin, CancellationToken ct)
    {
        // The printer refuses a load for some minutes after an export. Ask again until it agrees.
        var until = DateTime.UtcNow + WaitWhileBusy;
        string? uploadUrl = null;
        while (uploadUrl is null)
        {
            var (_, page, _) = await GetAsync("/m_import_dev_mng_import.html", ct);
            var fields = new List<(string, string)>
            {
                ("iToken", Token(page, "Import")),
                ("i2052", filePassword),
                ("i2070", "0"), ("i2070", "1"),
                ("i2080", "0"),
                ("i2140", "0"),
            };
            if (!string.IsNullOrEmpty(addressBookPin)) fields.Add(("i2092", addressBookPin));

            var (status, body, _) = await PostFormAsync("/cgi/m_import_dev_mng_import.cgi", fields, ct, secret: "i2052");
            var start = TryJson(body);
            string refusal;
            if (status != 200 || start is null || !start.Value.TryGetProperty("status", out var s) || s.ToString() != "0")
            {
                refusal = "The printer would not start the import. Another import or export may be running.";
            }
            else if (start.Value.TryGetProperty("redirect", out var refused) && refused.GetString() is { Length: > 0 } back)
            {
                // A wrong or missing PIN, or nothing selected, will not get better by waiting.
                if (Regex.IsMatch(back, "err=[145]")) throw new CanonException(ExplainRefusal(back, "import"));
                refusal = ExplainRefusal(back, "import");
            }
            else
            {
                uploadUrl = start.Value.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(uploadUrl)) throw new CanonException("The printer did not say where to send the file.");
                break;
            }

            if (DateTime.UtcNow >= until) throw new CanonException(refusal);
            Trace?.Invoke($"       not yet: waiting {BusyRetryEvery.TotalSeconds:0} seconds and asking again.");
            Waiting?.Invoke();
            await Task.Delay(BusyRetryEvery, ct);
            using (await SendAsync(new HttpRequestMessage(HttpMethod.Post, _base + "/json/heartbeat.json"), null, ct))
            {
            }
        }

        using var upload = new MultipartFormDataContent();
        var part = new ByteArrayContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        upload.Add(part, "importData", "compact.dcm");
        using (var sent = await SendAsync(new HttpRequestMessage(HttpMethod.Post, Absolute(uploadUrl)) { Content = upload }, $"POST {uploadUrl} (the file, {file.Length} bytes)", ct))
        {
            Describe(sent, await sent.Content.ReadAsStringAsync(ct));
        }

        for (var attempt = 0; attempt < 80; attempt++)
        {
            // The page keeps the session awake while the printer works, so this does too.
            using (var beat = await SendAsync(new HttpRequestMessage(HttpMethod.Post, _base + "/json/heartbeat.json"), null, ct))
            {
            }

            var (code, result, _) = await GetAsync("/json/dcm_import_result.json", ct);
            if (code == 200 && TryJson(result) is { } json && json.TryGetProperty("redirect", out var done) && done.GetString() is { Length: > 0 } where)
            {
                await GetAsync(where, ct);
                return;
            }

            await Task.Delay(PollEvery, ct);
        }

        throw new CanonException("The printer did not report that the import had finished.");
    }

    public void Dispose() => _http.Dispose();

    private static string Token(string html, string page) =>
        TokenPattern().Match(html) is { Success: true } m
            ? m.Groups[1].Value
            : throw new CanonException($"The printer's {page} page was not what this module expects. It may need a newer module, or the sign-in may have ended.");

    private static IEnumerable<string> Links(string html) =>
        LinkPattern().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));

    private static bool IsWorthFollowing(string path)
    {
        var p = path.ToLowerInvariant();
        if (p.StartsWith("javascript", StringComparison.Ordinal) || p.StartsWith("mailto", StringComparison.Ordinal) || p.StartsWith('#')) return false;
        if (p.Contains("logout") || p.Contains("restart") || p.Contains("import_dev_mng_import")) return false;
        return p.Contains(".dcm") || p.Contains("download") || p.Contains("export") || p.Contains("e_result") || p.Contains("dcm");
    }

    private string Absolute(string path) => path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : _base + (path.StartsWith('/') ? path : "/" + path);

    private static JsonElement? TryJson(string text)
    {
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sends a request whose answer may be the exported file or may be a page. Returns whichever it was.</summary>
    private async Task<(byte[]? File, string Html, string FinalUrl)> SendForFileAsync(HttpRequestMessage request, string what, CancellationToken ct)
    {
        using var response = await SendAsync(request, what, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var final = response.RequestMessage?.RequestUri?.PathAndQuery ?? "";

        // The file is a gzip archive whatever the printer calls its type, so look at the bytes.
        if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            Trace?.Invoke($"    -> {(int)response.StatusCode}, the exported file, {bytes.Length} bytes ({response.Content.Headers.ContentType?.MediaType}; {response.Content.Headers.ContentDisposition})");
            return (bytes, "", final);
        }

        var html = System.Text.Encoding.UTF8.GetString(bytes);
        Describe(response, html);
        return (null, html, final);
    }

    private async Task<(int Status, string Body, string FinalUrl)> GetAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Absolute(path)), "GET " + path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Describe(response, body);
        return ((int)response.StatusCode, body, response.RequestMessage?.RequestUri?.PathAndQuery ?? path);
    }

    private async Task<(int Status, string Body, string FinalUrl)> PostFormAsync(string path, List<(string Name, string Value)> fields, CancellationToken ct, string? secret = null)
    {
        var shown = string.Join("&", fields.Select(f => f.Name + "=" + (f.Name == secret || f.Name is "i2082" or "i2091" or "i2052" or "i2092" or "i2102" or "i0014" ? "(hidden)" : f.Value)));
        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, _base + path) { Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Name, f.Value))) },
            $"POST {path}  {shown}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Describe(response, body);
        return ((int)response.StatusCode, body, response.RequestMessage?.RequestUri?.PathAndQuery ?? path);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string? what, CancellationToken ct)
    {
        if (what is not null) Trace?.Invoke(what);

        // What a browser sends along with a form. Some Remote UI firmware checks where a request came from.
        if (request.Method == HttpMethod.Post)
        {
            request.Headers.Referrer ??= new Uri(_base + "/login.html");
            request.Headers.TryAddWithoutValidation("Origin", _base);
        }

        try
        {
            return await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new CanonException("Could not reach the printer's Remote UI: " + (e.InnerException?.Message ?? e.Message));
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>The printer's own reasons for sending someone back to the Export or Import page, by the code in the address.</summary>
    public static string ExplainRefusal(string address, string doing)
    {
        var code = Regex.Match(address, @"err=(\d+)") is { Success: true } m ? m.Groups[1].Value : "";
        return code switch
        {
            "1" => "The printer says the address book PIN is incorrect. Check it in this printer's settings.",
            "2" => $"The printer would not start the {doing} because it says an import is still being performed. Wait a few minutes and try again. If it stays that way, restart the printer.",
            "3" => $"The printer would not start the {doing} because it says an export is still being performed. An earlier export that was not finished can leave it like this. Wait a few minutes and try again. If it stays that way, restart the printer.",
            "4" => $"The printer says nothing was selected to {doing}. This printer's Remote UI may differ from the one this module was written for.",
            "5" => "The printer wants its address book PIN. Enter it in this printer's settings.",
            _ => $"The printer would not start the {doing} and sent the request back without a reason this module knows ({address}).",
        };
    }

    /// <summary>A whole page with the owner's name for the printer taken out of the two places it appears.</summary>
    public static string WithoutPrinterNames(string html)
    {
        html = Regex.Replace(html, "(<span id=\"deviceName\">)[^<]*(</span>)", "$1(printer name)$2", RegexOptions.None, TimeSpan.FromSeconds(1));
        return TitlePattern().Replace(html, m => "<title>" + WithoutPrinterName(WebUtility.HtmlDecode(m.Groups[1].Value)) + "</title>");
    }

    /// <summary>A page title is "Remote UI: page: the printer's own name: models". The name is whatever its owner called it, so it is left out.</summary>
    public static string WithoutPrinterName(string title)
    {
        var parts = title.Split(':').Select(p => p.Trim()).ToArray();
        return parts.Length < 4 ? title : $"{parts[0]}: {parts[1]}: (printer name): {parts[^1]}";
    }

    private void Describe(HttpResponseMessage response, string body)
    {
        if (Trace is null) return;
        var final = response.RequestMessage?.RequestUri?.PathAndQuery;
        var type = response.Content.Headers.ContentType?.MediaType;
        Trace($"    -> {(int)response.StatusCode} {type}, {body.Length} characters, ended at {final}");
        if (type is not null && type.Contains("json"))
        {
            Trace("       " + body.Trim());
            return;
        }

        // Of a page, only what matters for finding the way: its title, its links and its scripts' addresses.
        if (TitlePattern().Match(body) is { Success: true } t) Trace("       title: " + WithoutPrinterName(WebUtility.HtmlDecode(t.Groups[1].Value)));
        var interesting = Links(body).Where(IsWorthFollowing).Distinct().Take(20).ToList();
        if (interesting.Count > 0) Trace("       points at: " + string.Join("  ", interesting.Select(i => i.Contains("pass=", StringComparison.Ordinal) ? i[..i.IndexOf('?')] + "?(left out)" : i)));
        foreach (Match call in Regex.Matches(body, "createXMLHttpRequest\\(\"(\\w+)\",\\s*\"([^\"]+)\"")) Trace($"       script calls: {call.Groups[1].Value} {call.Groups[2].Value}");
        foreach (Match field in PasswordFieldPattern().Matches(body)) Trace("       password box: " + (field.Groups[1].Success ? field.Groups[1].Value : field.Groups[2].Value));

        // A short page is one of the in-between pages (progress, result). Those carry no address book
        // data, and how they are written is exactly what needs to be seen, so they are recorded whole.
        if (body.Length <= 4000 && final is not null && (final.Contains("_prog") || final.Contains("_result")))
        {
            Trace("       page, with the printer's name left out:");
            foreach (var line in WithoutPrinterNames(body).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Trace("         | " + line);
        }
    }
}
