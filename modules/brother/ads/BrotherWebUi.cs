using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ScanRelay.Modules.BrotherAds;

public sealed class BrotherException(string message) : Exception(message);

/// <summary>One of the scanner's 25 "Scan to FTP/SFTP/Network/SharePoint" profile slots, as its list shows it.</summary>
/// <param name="Index">Counted from 0, as the scanner counts. The panel and the web page show it from 1.</param>
/// <param name="Type">0 Network (a shared folder), 1 FTP, 2 SFTP, 7 HTTP(S), 8 SharePoint.</param>
public sealed record BrotherSlot(int Index, int Type, string Name)
{
    public const int Network = 0;

    public bool IsNetwork => Type == Network;

    public string TypeName => Type switch { 0 => "Network", 1 => "FTP", 2 => "SFTP", 7 => "HTTP(S)", 8 => "SharePoint", _ => $"type {Type}" };
}

/// <summary>
/// Talks to a Brother ADS scanner's Web Based Management the way its own pages do. Brother does not
/// document this. It follows the scripts of an ADS-4900W (main firmware 3.20): the pages are empty
/// shells, and their scripts read XML from addresses under /ft/ and post settings back to them as
/// "name=value" pairs joined by "&&".
/// </summary>
public sealed partial class BrotherWebUi : IDisposable
{
    public const int SlotCount = 25;

    private readonly HttpClient _http;
    private readonly string _base;

    /// <summary>The values in a profile that must never be written down: passwords and PINs.</summary>
    private static readonly string[] SecretTags = ["FolderPass", "PINCode", "SecPDF_PIN"];

    public BrotherWebUi(string address, bool secure = true, HttpMessageHandler? handler = null)
    {
        _base = address.Contains("://", StringComparison.Ordinal) ? address.TrimEnd('/') : $"{(secure ? "https" : "http")}://{address}";

        // The scanner presents a certificate it made itself, so it cannot be checked against anything.
        // A cookie jar per connection keeps one scanner's session apart from another's.
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    /// <summary>Receives a line for each request and answer, with secrets left out. For the probe tool and the service log.</summary>
    public Action<string>? Trace { get; init; }

    public void Dispose() => _http.Dispose();

    /// <summary>The model name the scanner gives itself, such as "ADS-4900W". Needs no sign-in.</summary>
    public async Task<string?> ReadModelAsync(CancellationToken ct)
    {
        var (status, body) = await GetAsync("/ft/get_common_caption", ct);
        if (status != 200) return null;
        return TryXml(body)?.Descendants("ModelName").FirstOrDefault()?.Value.Trim() is { Length: > 0 } model ? model : null;
    }

    /// <summary>Signs in with the Web Based Management password. The scanner only takes this over https.</summary>
    public async Task SignInAsync(string password, CancellationToken ct)
    {
        // As the page's own script sends it: the password with "&" written as "&amp;", then a marker.
        var body = "PASS=" + password.Replace("&", "&amp;", StringComparison.Ordinal) + "&&Admin_Password_Save";
        var status = await PostAsync("/auth/login", body, "POST /auth/login  PASS=(hidden)", ct);
        if (status == 200) return;
        throw new BrotherException(status switch
        {
            403 => "The scanner is refusing sign-in for now. It does this after several wrong passwords. Wait a few minutes and try again.",
            401 => "The scanner refused the Web Based Management password.",
            _ when !_base.StartsWith("https", StringComparison.OrdinalIgnoreCase) => $"The scanner answered the sign-in with {status}. It only takes a sign-in over https.",
            _ => $"The scanner answered the sign-in with {status}, which this module does not know.",
        });
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        try
        {
            await PostAsync("/auth/logout", "", "POST /auth/logout", ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // The session ends by itself after a while.
        }
    }

    /// <summary>The 25 profile slots: what kind each is and what it is called.</summary>
    public async Task<IReadOnlyList<BrotherSlot>> ReadSlotsAsync(CancellationToken ct) => (await ReadSlotListAsync(ct)).Slots;

    private async Task<(IReadOnlyList<BrotherSlot> Slots, string SendToMyFolder)> ReadSlotListAsync(CancellationToken ct)
    {
        var xml = await GetXmlAsync("/ft/scan_profile", ct);
        var types = xml.Descendants("Profile_Type").Select(e => int.TryParse(e.Value.Trim(), out var t) ? t : -1).ToList();
        var names = xml.Descendants("Profile_Name").Select(e => e.Value).ToList();
        if (types.Count == 0) throw new BrotherException("The scanner's profile list was not in the form this module expects. It may need a newer module, or the sign-in may have ended.");
        return ([.. types.Select((type, i) => new BrotherSlot(i, type, i < names.Count ? names[i] : ""))], xml.Element("my_folder")?.Value.Trim() is "1" ? "1" : "0");
    }

    /// <summary>
    /// Makes the given slots Network profiles. The scanner takes the kind of all its slots in one go,
    /// so the others are sent back as they are, and so is its "Send to My Folder" setting.
    /// </summary>
    public async Task MakeNetworkAsync(IReadOnlyCollection<int> indexes, CancellationToken ct)
    {
        var (slots, myFolder) = await ReadSlotListAsync(ct);
        var pairs = slots.Select(s => $"profile{s.Index}={(indexes.Contains(s.Index) ? BrotherSlot.Network : s.Type)}").Append("SendtoMyFolder=" + myFolder).Append("Scanprofilelist_Save");
        var status = await PostAsync("/ft/scan_profile", string.Join("&&", pairs), $"POST /ft/scan_profile  (the kind of every slot; {string.Join(", ", indexes.Select(i => i + 1))} to Network)", ct);
        if (status != 200) throw new BrotherException(ExplainRefusal(status, "change the kind of a profile"));
    }

    /// <summary>
    /// Saves one Network profile. The scanner wants every setting of the profile each time, so the scan
    /// settings it holds now (as just read) go back unchanged, and only the name, the folder and the
    /// account are set. All four empty clears the profile.
    /// </summary>
    public async Task SaveNetworkProfileAsync(int index, XElement current, string name, string folderPath, string user, string password, CancellationToken ct)
    {
        string Now(string tag, string fallback = "0") => current.Element(tag)?.Value.Trim() is { Length: > 0 } v ? v : fallback;

        // The names and the order are those of the scanner's own page script.
        var pairs = new List<string>
        {
            "ScanType=" + Now("ScanType", "2"),
        };
        foreach (var tag in new[] { "Resolution_Color", "Resolution_BW", "Resolution_Gray", "Resolution_Auto" }) pairs.Add($"{tag}={Now(tag, "300")}");
        foreach (var tag in new[] { "FileType_Color", "FileType_BW", "FileType_Gray", "FileType_Auto", "FileSize_Color", "FileSize_Gray", "FileSize_Auto" }) pairs.Add($"{tag}={Now(tag, "1")}");
        foreach (var tag in new[] { "DocumentSize", "PaperWidth", "PaperHeight", "MarginLeft", "MarginTop", "MarginRight", "MarginBottom" }) pairs.Add($"{tag}={Now(tag)}");
        pairs.Add("AutoDeskew=0");
        foreach (var tag in new[] { "SkipBlankPage", "DuplexScan", "ContinuousScan", "AutoColorDetectSensitivity", "SkipBlankPageSensitivity" }) pairs.Add($"{tag}={Now(tag)}");
        pairs.Add("Brightness=" + Now("Brightness", "50"));
        pairs.Add("Contrast=" + Now("Contrast", "50"));
        pairs.Add("FileNameIdx=" + Now("FilenameIdx"));
        pairs.Add("Multifeed=" + Now("Multifeed"));
        pairs.Add("FileSplit=" + Now("FileSplit"));
        pairs.Add("FileSplit_Cnt_Doc=" + Now("FileSplit_Cnt_Doc", "1"));
        pairs.Add("FileSplit_Cnt_Page=" + Now("FileSplit_Cnt_Page", "1"));
        pairs.Add("CG_Tiff_Compress=" + Now("CG_Tiff_Compress"));
        pairs.Add("BW_Tiff_Compress=" + Now("BW_Tiff_Compress", "1"));
        pairs.Add("ScanPreview=" + Now("ScanPreview"));
        pairs.Add("SecPDF_PIN=" + (current.Element("SecPDF_PIN")?.Value ?? ""));
        pairs.Add("ProfileName=" + Escape(name));
        pairs.Add("UsePIN=" + Now("EnablePIN"));
        pairs.Add("PINCode=" + (current.Element("PINCode")?.Value ?? ""));
        pairs.Add("NetworkPath=" + Escape(folderPath));
        pairs.Add("NetworkUser=" + Escape(user));
        pairs.Add("NetworkPassword=" + Escape(password));
        pairs.Add("ProfileIndex=" + index);
        pairs.Add("Auth_Method=2");   // NTLMv2, the only one the page offers
        pairs.Add("Kerberos_Addr=" + (current.Element("Kerberos_Addr")?.Value ?? ""));
        pairs.Add("Network_command=SaveOnly");
        pairs.Add("ScantoShareFolder_Save");

        var status = await PostAsync(ProfileAddress(index), string.Join("&&", pairs),
            $"POST {ProfileAddress(index)}  ({pairs.Count} values; the name, folder, account and password are not shown)", ct);
        if (status != 200) throw new BrotherException(ExplainRefusal(status, name.Length == 0 ? "clear a profile" : "save a profile"));
    }

    /// <summary>As the page's own script writes a value: the three characters that would break its XML.</summary>
    private static string Escape(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>The scanner says why with a status code. These are the ones its own pages explain.</summary>
    public static string ExplainRefusal(int status, string doing) => status switch
    {
        401 => "The scanner says the sign-in has ended.",
        520 => $"The scanner would not {doing} because it is in use. Try again when nobody is scanning.",
        533 => $"The scanner would not {doing} while its Auto Start Scan mode is on. Turn that off under General, Auto Start Scan Mode, in its Web Based Management.",
        _ => $"The scanner would not {doing} (it answered {status}).",
    };

    /// <summary>Everything the scanner holds for one Network profile, as the names and values its page reads.</summary>
    public async Task<XElement> ReadNetworkProfileAsync(int index, CancellationToken ct) => await GetXmlAsync(ProfileAddress(index), ct);

    /// <summary>The scanner's own address for one Network profile. The misspelling is the scanner's.</summary>
    public static string ProfileAddress(int index) => $"/ft/scan_tosharefoler={index}&&";

    /// <summary>Fetches a file of the scanner's own web pages, such as a script. For the probe tool.</summary>
    public async Task<(int Status, string Body)> GetPageAsync(string path, CancellationToken ct) => await GetAsync(path, ct, describe: false);

    private async Task<XElement> GetXmlAsync(string path, CancellationToken ct)
    {
        var (status, body) = await GetAsync(path, ct);
        if (status == 401) throw new BrotherException("The scanner says the sign-in has ended.");
        if (status != 200) throw new BrotherException($"The scanner answered {status} for {path}.");
        return TryXml(body) ?? throw new BrotherException($"The scanner's answer for {path} was not the XML this module expects.");
    }

    private async Task<(int Status, string Body)> GetAsync(string path, CancellationToken ct, bool describe = true)
    {
        Trace?.Invoke("GET " + path);
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, _base + path), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Trace?.Invoke($"    -> {(int)response.StatusCode} {response.Content.Headers.ContentType?.MediaType}, {body.Length} characters");
        if (describe && response.IsSuccessStatusCode) foreach (var line in WithoutSecrets(body).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Trace?.Invoke("       | " + line);
        return ((int)response.StatusCode, body);
    }

    private async Task<int> PostAsync(string path, string body, string what, CancellationToken ct)
    {
        Trace?.Invoke(what);
        using var request = new HttpRequestMessage(HttpMethod.Post, _base + path) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

        // Exactly the header the page's own script sets, with nothing added to it.
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");
        using var response = await SendAsync(request, ct);
        Trace?.Invoke($"    -> {(int)response.StatusCode}");
        return (int)response.StatusCode;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new BrotherException($"Could not reach the scanner's Web Based Management at {_base}: {e.InnerException?.Message ?? e.Message}");
        }
    }

    private static XElement? TryXml(string body)
    {
        try
        {
            return XElement.Parse(body.TrimStart('﻿', ' ', '\r', '\n'));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<(?<tag>\w+)>(?<value>[^<]*)</\k<tag>>")]
    private static partial Regex ElementPattern();

    /// <summary>
    /// An answer made safe to write down: passwords and PINs are replaced by their length, and profile
    /// names, which may be people's names, likewise. Everything else is the scanner's settings.
    /// </summary>
    public static string WithoutSecrets(string xml) => ElementPattern().Replace(xml, m =>
    {
        var (tag, value) = (m.Groups["tag"].Value, m.Groups["value"].Value);
        if (value.Length == 0) return m.Value;
        if (SecretTags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return $"<{tag}>(hidden, {value.Length} characters)</{tag}>";
        if (tag is "ProfileName" or "Profile_Name") return $"<{tag}>(a name, {value.Length} characters)</{tag}>";
        return m.Value;
    });
}
