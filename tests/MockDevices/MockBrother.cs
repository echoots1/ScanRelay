using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ScanRelay.MockDevices;

/// <summary>One of the mock scanner's profile slots.</summary>
public sealed record BrotherMockProfile(int Type = 0, string Name = "", string FolderPath = "", string FolderUser = "", string FolderPass = "");

/// <summary>
/// A stand-in for a Brother ADS-4900W's Web Based Management, written from that scanner's own page
/// scripts (main firmware 3.20): sign in at /auth/login, the profile list at /ft/scan_profile, one
/// Network profile at /ft/scan_tosharefoler=N&&. The XML is in the shape a real scanner returned.
/// What the real scanner does with a save is still a guess: it has only been read so far.
/// </summary>
public sealed class MockBrother : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HashSet<string> _sessions = [];

    private MockBrother(WebApplication app) => _app = app;

    public BrotherMockProfile[] Profiles { get; } = [.. Enumerable.Range(0, 25).Select(_ => new BrotherMockProfile())];
    public string Password { get; set; } = "the-scanner-password";
    public string Model { get; set; } = "ADS-4900W";
    public int Port { get; private set; }
    public string Address => $"http://127.0.0.1:{Port}";

    public static async Task<MockBrother> StartAsync()
    {
        var app = MockHost.Create();
        var mock = new MockBrother(app);
        app.Run(mock.HandleAsync);
        mock.Port = await MockHost.StartAsync(app);
        return mock;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    /// <summary>Set to have every save refused with this status, as the real scanner does while in use (520) or in Auto Start Scan mode (533).</summary>
    public int? BusyStatus { get; set; }
    public int Saves;
    public int TypeChanges;

    private static readonly string[] RequiredOnSave =
    [
        "ScanType", "Resolution_Color", "Resolution_BW", "Resolution_Gray", "Resolution_Auto", "FileType_Color", "FileType_BW", "FileType_Gray", "FileType_Auto",
        "FileSize_Color", "FileSize_Gray", "FileSize_Auto", "DocumentSize", "PaperWidth", "PaperHeight", "MarginLeft", "MarginTop", "MarginRight", "MarginBottom",
        "AutoDeskew", "SkipBlankPage", "DuplexScan", "ContinuousScan", "AutoColorDetectSensitivity", "SkipBlankPageSensitivity", "Brightness", "Contrast", "FileNameIdx",
        "Multifeed", "FileSplit", "FileSplit_Cnt_Doc", "FileSplit_Cnt_Page", "CG_Tiff_Compress", "BW_Tiff_Compress", "ScanPreview", "SecPDF_PIN", "ProfileName", "UsePIN",
        "PINCode", "NetworkPath", "NetworkUser", "NetworkPassword", "ProfileIndex", "Auth_Method", "Kerberos_Addr", "Network_command", "ScantoShareFolder_Save",
    ];

    /// <summary>The body the scanner's pages send: "name=value" pairs joined by "&&", the last a bare marker.</summary>
    private static async Task<Dictionary<string, string>> ReadPairsAsync(HttpRequest request)
    {
        // The real page sets this header and nothing more.
        if (request.ContentType != "application/x-www-form-urlencoded") return [];
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var pairs = new Dictionary<string, string>();
        foreach (var part in (await reader.ReadToEndAsync()).Split("&&"))
        {
            var cut = part.IndexOf('=');
            pairs[cut < 0 ? part : part[..cut]] = cut < 0 ? "" : part[(cut + 1)..];
        }

        return pairs;
    }

    private static string Unescape(string value) => value.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var path = WebUtility.UrlDecode(request.Path.Value ?? "/");
        async Task Raw(string xml)
        {
            context.Response.ContentType = "text/xml";
            await context.Response.WriteAsync(xml);
        }

        var signedIn = request.Cookies["SessionID"] is { } id && _sessions.Contains(id);

        if (path == "/ft/get_common_caption")
        {
            await Raw($"<?xml version=\"1.0\" encoding=\"utf-8\"?><CommonCaption><Menu_Status>63</Menu_Status><Manufacturer>Brother</Manufacturer><ModelName>{Model}</ModelName><YearRange>2025</YearRange><CCode>73</CCode></CommonCaption>");
            return;
        }

        if (path == "/auth/login" && request.Method == "POST")
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            if (request.ContentType != "application/x-www-form-urlencoded" || body != "PASS=" + Password.Replace("&", "&amp;") + "&&Admin_Password_Save")
            {
                context.Response.StatusCode = 401;
                return;
            }

            var session = Guid.NewGuid().ToString("N");
            _sessions.Add(session);
            context.Response.Cookies.Append("SessionID", session);
            return;
        }

        if (path == "/auth/logout" && request.Method == "POST")
        {
            if (request.Cookies["SessionID"] is { } gone) _sessions.Remove(gone);
            return;
        }

        if (!signedIn)
        {
            context.Response.StatusCode = 401;
            return;
        }

        if (path == "/ft/scan_profile" && request.Method == "POST")
        {
            // The kind of every slot at once, as the real page sends it.
            var sent = await ReadPairsAsync(request);
            if (BusyStatus is { } busy) { context.Response.StatusCode = busy; return; }
            if (!sent.ContainsKey("Scanprofilelist_Save") || !sent.ContainsKey("SendtoMyFolder") || Enumerable.Range(0, 25).Any(i => !sent.ContainsKey("profile" + i)))
            {
                context.Response.StatusCode = 400;
                return;
            }

            for (var i = 0; i < 25; i++)
            {
                var kind = int.Parse(sent["profile" + i]);
                if (kind != Profiles[i].Type) Profiles[i] = new BrotherMockProfile(kind);   // changing the kind empties the slot
            }

            TypeChanges++;
            return;
        }

        if (path == "/ft/scan_profile")
        {
            var list = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><Scan_Profile><Menu_Status>30</Menu_Status><my_folder_cap>1</my_folder_cap><my_folder>0</my_folder>");
            foreach (var p in Profiles) list.Append($"<Profile_Type>{p.Type}</Profile_Type><Profile_Name>{WebUtility.HtmlEncode(p.Name)}</Profile_Name>");
            await Raw(list.Append("</Scan_Profile>").ToString());
            return;
        }

        const string profile = "/ft/scan_tosharefoler=";
        if (path.StartsWith(profile, StringComparison.Ordinal) && int.TryParse(path[profile.Length..].TrimEnd('&'), out var index) && index is >= 0 and < 25 && request.Method == "GET")
        {
            var p = Profiles[index];
            await Raw("<?xml version=\"1.0\" encoding=\"utf-8\"?><Scan_toShareFolder><Menu_Status>63</Menu_Status>"
                + $"<ProfileName>{WebUtility.HtmlEncode(p.Name)}</ProfileName><FolderPath>{WebUtility.HtmlEncode(p.FolderPath)}</FolderPath><FolderUser>{WebUtility.HtmlEncode(p.FolderUser)}</FolderUser>"
                + $"<FolderPass>{WebUtility.HtmlEncode(p.FolderPass)}</FolderPass><EnablePIN>0</EnablePIN><PINCode></PINCode><ScanType>2</ScanType><FileType_BW>1</FileType_BW><FileType_Gray>1</FileType_Gray>"
                + "<FileType_Color>1</FileType_Color><FileType_Auto>1</FileType_Auto><FileSize_Gray>1</FileSize_Gray><FileSize_Color>1</FileSize_Color><FileSize_Auto>1</FileSize_Auto><Resolution_BW>300</Resolution_BW>"
                + "<Resolution_Gray>300</Resolution_Gray><Resolution_Color>300</Resolution_Color><Resolution_Auto>300</Resolution_Auto><DocumentSize>25</DocumentSize><PaperWidth>2550</PaperWidth><PaperHeight>4200</PaperHeight>"
                + "<MarginLeft>0</MarginLeft><MarginTop>0</MarginTop><MarginRight>0</MarginRight><MarginBottom>0</MarginBottom><AutoDeskew>0</AutoDeskew><SkipBlankPage>1</SkipBlankPage><DuplexScan>1</DuplexScan>"
                + "<ContinuousScan>0</ContinuousScan><AutoColorDetectSensitivity>3</AutoColorDetectSensitivity><SkipBlankPageSensitivity>2</SkipBlankPageSensitivity><Brightness>50</Brightness><Contrast>50</Contrast>"
                + "                    <NWFilename1>BRW000000000000</NWFilename1><NWFilename2>Estimate</NWFilename2><FilenameIdx>0</FilenameIdx>                    <Multifeed>1</Multifeed><FileSplit_Cap>3</FileSplit_Cap>"
                + "<FileSplit>0</FileSplit><FileSplit_Cnt_Doc>1</FileSplit_Cnt_Doc><FileSplit_Cnt_Page>1</FileSplit_Cnt_Page>                    <CG_Tiff_Compress>0</CG_Tiff_Compress><BW_Tiff_Compress>1</BW_Tiff_Compress>"
                + "                    <ScanPreview>0</ScanPreview><Platform>ht</Platform><MFD_Cap>1</MFD_Cap><CS_Cap>1</CS_Cap><Preview_Cap>1</Preview_Cap><SecPDF_PIN_UI>0</SecPDF_PIN_UI><SecPDF_PIN></SecPDF_PIN>"
                + "<Auth_Method>0</Auth_Method><Kerberos_Addr></Kerberos_Addr></Scan_toShareFolder>");
            return;
        }

        if (path.StartsWith(profile, StringComparison.Ordinal) && int.TryParse(path[profile.Length..].TrimEnd('&'), out var saved) && saved is >= 0 and < 25 && request.Method == "POST")
        {
            var sent = await ReadPairsAsync(request);
            if (BusyStatus is { } busy) { context.Response.StatusCode = busy; return; }

            // Every value the real page sends must be there, the slot must be a Network one, and the index must agree.
            var missing = RequiredOnSave.Where(k => !sent.ContainsKey(k)).ToList();
            if (missing.Count > 0 || Profiles[saved].Type != 0 || sent["ProfileIndex"] != saved.ToString() || sent["Network_command"] != "SaveOnly" || sent["Auth_Method"] != "2")
            {
                context.Response.StatusCode = 400;
                return;
            }

            Profiles[saved] = new BrotherMockProfile(0, Unescape(sent["ProfileName"]), Unescape(sent["NetworkPath"]), Unescape(sent["NetworkUser"]), Unescape(sent["NetworkPassword"]));
            Saves++;
            return;
        }

        if (path.StartsWith("/EWS/", StringComparison.Ordinal))
        {
            context.Response.ContentType = path.EndsWith(".js", StringComparison.Ordinal) ? "application/javascript" : "text/html";
            await context.Response.WriteAsync("// the scanner's own page: " + path);
            return;
        }

        context.Response.StatusCode = 404;
    }
}
