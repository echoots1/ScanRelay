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

        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, _base + "/cgi/m_import_dev_mng_export.cgi") { Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Item1, f.Item2))) },
            "POST /cgi/m_import_dev_mng_export.cgi (address book, with a file password)", ct);
        if (await TryReadFileAsync(response, ct) is { } direct) return direct;
        var html = await response.Content.ReadAsStringAsync(ct);
        Describe(response, html);
        if (response.RequestMessage?.RequestUri?.Query.Contains("err=") == true || html.Contains("err=1", StringComparison.Ordinal) && html.Length < 400)
        {
            throw new CanonException("The printer would not start the export. Its address book PIN may be needed, or another import or export is running.");
        }

        // The printer prepares the file, then says where the result page is.
        var candidates = new List<string>(Links(html));
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var (status, body, _) = await GetAsync("/json/dcm_export_result.json", ct);
            if (status == 200 && TryJson(body) is { } json)
            {
                var url = json.TryGetProperty("url", out var u) ? u.GetString() : null;
                var result = json.TryGetProperty("result", out var r) ? r.ToString() : "";
                if (!string.IsNullOrEmpty(url))
                {
                    if (url.Contains("m_import_dev_mng_export.html", StringComparison.Ordinal)) throw new CanonException("The printer says the address book PIN is incorrect.");
                    candidates.Insert(0, url + (url.Contains('?') ? "" : "?code=" + result));
                    break;
                }
            }

            await Task.Delay(PollEvery, ct);
        }

        // Follow what the pages point at until one of them is the file.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < candidates.Count && seen.Count < 25; i++)
        {
            var path = candidates[i];
            if (!IsWorthFollowing(path) || !seen.Add(path)) continue;
            using var next = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Absolute(path)), "GET " + path, ct);
            if (await TryReadFileAsync(next, ct) is { } file) return file;
            var text = await next.Content.ReadAsStringAsync(ct);
            Describe(next, text);
            candidates.AddRange(Links(text));
        }

        throw new CanonException("The export finished but the file could not be found. This printer's Remote UI may differ from the one this module was written for.");
    }

    /// <summary>Loads an encrypted configuration file's address book onto the printer. It replaces the whole address book.</summary>
    public async Task ImportAddressBookAsync(byte[] file, string filePassword, string? addressBookPin, CancellationToken ct)
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
        if (status != 200 || start is null || !start.Value.TryGetProperty("status", out var s) || s.ToString() != "0")
        {
            throw new CanonException("The printer would not start the import. Another import or export may be running.");
        }

        if (start.Value.TryGetProperty("redirect", out var refused) && refused.GetString() is { Length: > 0 } back)
        {
            throw new CanonException(back.Contains("err=1", StringComparison.Ordinal) ? "The printer says the address book PIN is incorrect."
                : back.Contains("err=5", StringComparison.Ordinal) ? "The printer wants its address book PIN. Enter it in this printer's settings."
                : "The printer would not start the import: " + back);
        }

        var uploadUrl = start.Value.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(uploadUrl)) throw new CanonException("The printer did not say where to send the file.");

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

    /// <summary>The body, when the answer is the exported file itself and not a page.</summary>
    private async Task<byte[]?> TryReadFileAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        var attachment = response.Content.Headers.ContentDisposition is not null;
        if (!attachment && (type.Contains("html") || type.Contains("json") || type.Contains("text"))) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            Trace?.Invoke($"    -> {(int)response.StatusCode}, the exported file, {bytes.Length} bytes ({type}; {response.Content.Headers.ContentDisposition})");
            return bytes;
        }

        return null;
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
        if (TitlePattern().Match(body) is { Success: true } t) Trace("       title: " + Regex.Replace(WebUtility.HtmlDecode(t.Groups[1].Value), @":[^:]*:", ": (printer name): ", RegexOptions.None, TimeSpan.FromSeconds(1)));
        var interesting = Links(body).Where(IsWorthFollowing).Distinct().Take(20).ToList();
        if (interesting.Count > 0) Trace("       points at: " + string.Join("  ", interesting));
        foreach (Match call in Regex.Matches(body, "createXMLHttpRequest\\(\"(\\w+)\",\\s*\"([^\"]+)\"")) Trace($"       script calls: {call.Groups[1].Value} {call.Groups[2].Value}");
        foreach (Match field in PasswordFieldPattern().Matches(body)) Trace("       password box: " + (field.Groups[1].Success ? field.Groups[1].Value : field.Groups[2].Value));
    }
}
