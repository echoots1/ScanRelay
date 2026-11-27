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
/// Network profile at /ft/scan_tosharefoler=N&&. What the real XML looks like beyond the names the
/// scripts read is a guess until a real scanner has been read.
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
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var mock = new MockBrother(app);
        app.Run(mock.HandleAsync);
        await app.StartAsync();
        mock.Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        return mock;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var path = WebUtility.UrlDecode(request.Path.Value ?? "/");
        async Task Xml(string inner)
        {
            context.Response.ContentType = "text/xml";
            await context.Response.WriteAsync("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Root>\n" + inner + "</Root>\n");
        }

        var signedIn = request.Cookies["SessionID"] is { } id && _sessions.Contains(id);

        if (path == "/ft/get_common_caption")
        {
            await Xml($"<Manufacturer>Brother</Manufacturer>\n<ModelName>{Model}</ModelName>\n<YearRange>2025</YearRange>\n<CCode>1</CCode>\n");
            return;
        }

        if (path == "/auth/login" && request.Method == "POST")
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            if (body != "PASS=" + Password.Replace("&", "&amp;") + "&&Admin_Password_Save")
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

        if (path == "/ft/scan_profile")
        {
            var list = new StringBuilder("<Menu_Status>1</Menu_Status>\n");
            foreach (var p in Profiles) list.Append($"<Profile_Type>{p.Type}</Profile_Type>\n<Profile_Name>{WebUtility.HtmlEncode(p.Name)}</Profile_Name>\n");
            await Xml(list.ToString());
            return;
        }

        const string profile = "/ft/scan_tosharefoler=";
        if (path.StartsWith(profile, StringComparison.Ordinal) && int.TryParse(path[profile.Length..].TrimEnd('&'), out var index) && index is >= 0 and < 25 && request.Method == "GET")
        {
            var p = Profiles[index];
            await Xml("<Platform>1</Platform>\n<ScanType>2</ScanType>\n<Resolution_Color>300</Resolution_Color>\n<Resolution_BW>300</Resolution_BW>\n<Resolution_Gray>300</Resolution_Gray>\n<Resolution_Auto>300</Resolution_Auto>\n"
                + "<FileType_Color>1</FileType_Color>\n<FileType_BW>1</FileType_BW>\n<FileType_Gray>1</FileType_Gray>\n<FileType_Auto>1</FileType_Auto>\n<DocumentSize>25</DocumentSize>\n<DuplexScan>1</DuplexScan>\n"
                + $"<ProfileName>{WebUtility.HtmlEncode(p.Name)}</ProfileName>\n<FolderPath>{WebUtility.HtmlEncode(p.FolderPath)}</FolderPath>\n<FolderUser>{WebUtility.HtmlEncode(p.FolderUser)}</FolderUser>\n"
                + $"<FolderPass>{WebUtility.HtmlEncode(p.FolderPass)}</FolderPass>\n<EnablePIN>0</EnablePIN>\n<PINCode></PINCode>\n<SecPDF_PIN></SecPDF_PIN>\n<Kerberos_Addr></Kerberos_Addr>\n");
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
