using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScanRelay.Core.Audit;
using ScanRelay.Core.Cloud;
using ScanRelay.Core.Data;
using ScanRelay.Core.Settings;
using ScanRelay.MockCloud;

namespace ScanRelay.Service.Tests;

/// <summary>The real service, in memory, signing people in through the mock Entra.</summary>
public sealed class ServiceHostTests : IAsyncLifetime
{
    private readonly string _dataFolder = Path.Combine(Path.GetTempPath(), "scanrelay-test-" + Guid.NewGuid().ToString("N"));
    private MockCloudServer _cloud = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private IPAddress _remoteAddress = IPAddress.Loopback;
    private MockUser _admin = null!;
    private MockUser _helper = null!;
    private MockUser _stranger = null!;

    public async Task InitializeAsync()
    {
        _cloud = await MockCloudServer.StartAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataFolder", _dataFolder);
            builder.UseSetting("Url", "https://localhost");
            builder.UseSetting("Cloud:Login", _cloud.LoginUrl);
            builder.UseSetting("Cloud:Graph", _cloud.GraphUrl);
            builder.UseSetting("UpdateCheck", "false");
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(() => _remoteAddress)));
        });
        _admin = _cloud.State.AddUser("Alex Admin");
        _helper = _cloud.State.AddUser("Harper Helpdesk");
        _stranger = _cloud.State.AddUser("Sam Stranger");
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _cloud.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataFolder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private T Get<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    private HttpClient Client() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

    /// <summary>Does what setup does: creates the registration, approves it and chooses the groups.</summary>
    private async Task ConnectAsync()
    {
        _ = Client();
        var flow = Get<DeviceCodeFlow>();
        var started = await flow.StartAsync(null, TimeProvider.System);
        var token = (await flow.PollAsync(null, started.DeviceCode)).AccessToken!;
        var result = await Get<AppRegistration>().CreateOrRepairAsync(token);
        _cloud.State.Applications.Values.Single().ConsentGranted = true;
        var admins = _cloud.State.AddGroup("Admins", _admin);
        var helpdesk = _cloud.State.AddGroup("Helpdesk", _helper);
        await Get<SettingsStore>().UpdateAsync(
            s => s with { TenantId = result.TenantId, ClientId = result.ClientId, AdminGroupId = admins.Id, HelpdeskGroupId = helpdesk.Id },
            Actor.Setup);
    }

    /// <summary>Signs in through the mock Entra the way a browser would. Returns where the person ends up.</summary>
    private async Task<string> SignInAsync(HttpClient client, MockUser user, string returnUrl = "/")
    {
        _cloud.State.SignInAs = user.Id;
        var challenge = await client.GetAsync("/signin?returnUrl=" + Uri.EscapeDataString(returnUrl));
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var authorize = challenge.Headers.Location!;
        Assert.StartsWith(_cloud.LoginUrl, authorize.ToString());

        using var browser = new HttpClient();
        var form = await browser.GetStringAsync(authorize);
        var fields = Regex.Matches(form, "name=\"(\\w+)\" value=\"([^\"]*)\"")
            .ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));
        var callback = await client.PostAsync("/signin-oidc", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return callback.Headers.Location!.ToString();
    }

    [Fact]
    public async Task Health_reports_ok_once_the_database_is_ready()
    {
        var response = await Client().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(Core.Modules.ModuleHost.AppVersion.ToString(3), body.RootElement.GetProperty("version").GetString());
        Assert.True(Directory.Exists(Path.Combine(_dataFolder, "Inbox")));
        Assert.True(File.Exists(Path.Combine(_dataFolder, "scanrelay.db")));
    }

    [Fact]
    public async Task Before_setup_the_screen_opens_without_sign_in_on_the_server_itself()
    {
        var client = Client();

        var page = await client.GetAsync("/setup");
        var script = await client.GetAsync("/_framework/blazor.web.js");
        var style = await client.GetAsync("/_content/ScanRelay.Admin/app.css");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("_framework/blazor.web.js", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal(HttpStatusCode.OK, style.StatusCode);
    }

    [Theory]
    [InlineData("203.0.113.9", false)]
    [InlineData("127.0.0.1", true)]
    public async Task Before_setup_the_screen_does_not_answer_other_computers_or_proxies(string address, bool viaProxy)
    {
        var client = Client();
        _remoteAddress = IPAddress.Parse(address);
        if (viaProxy) client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");

        var page = await client.GetAsync("/setup");
        var file = await client.GetAsync("/files/settings-export");
        var health = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Contains("on the server itself", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, file.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", false, HttpStatusCode.Redirect)]
    [InlineData("127.0.0.1", true, HttpStatusCode.Forbidden)]
    [InlineData("203.0.113.9", false, HttpStatusCode.Forbidden)]
    public async Task Setup_for_modules_only_opens_from_a_browser_on_the_server(string address, bool viaProxy, HttpStatusCode expected)
    {
        var client = Client();
        _remoteAddress = IPAddress.Parse(address);
        if (viaProxy) client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");

        var answer = await client.GetAsync("/local/modules-setup");

        Assert.Equal(expected, answer.StatusCode);
        if (expected == HttpStatusCode.Redirect) Assert.Equal("scanrelay-setup:modules", answer.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task The_first_admin_sign_in_finishes_setup_and_closes_the_open_window()
    {
        await ConnectAsync();
        var admin = Client();
        Assert.False((await Get<SettingsStore>().GetAsync()).SetupComplete);

        var landed = await SignInAsync(admin, _admin, "/setup?step=finish");

        Assert.Equal("/setup?step=finish", landed);
        Assert.True((await Get<SettingsStore>().GetAsync()).SetupComplete);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/settings")).StatusCode);

        // Someone else on the server itself no longer gets in without signing in.
        var other = Client();
        var response = await other.GetAsync("/settings");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(_cloud.LoginUrl, response.Headers.Location!.ToString());

        // And the screen now answers other computers, for people who sign in.
        _remoteAddress = IPAddress.Parse("203.0.113.9");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/jobs")).StatusCode);
        await using var db = await Get<IDbContextFactory<ScanRelayDbContext>>().CreateDbContextAsync();
        Assert.Contains(await db.AuditLog.ToListAsync(), a => a.Action == "Setup finished" && a.ActorName == "Alex Admin");
    }

    [Theory]
    [InlineData("scan.example.org", null)]
    [InlineData("localhost", "203.0.113.9")]
    public async Task Through_a_reverse_proxy_sign_in_returns_to_the_proxys_address(string host, string? forwardedFor)
    {
        await ConnectAsync();
        await SignInAsync(Client(), _admin);
        await Get<SettingsStore>().UpdateAsync(s => s with { ProxyAddress = "https://scan.example.org" }, Actor.Setup);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri($"https://{host}") });
        if (forwardedFor is not null) client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);

        var challenge = await client.GetAsync("/signin");

        var query = System.Web.HttpUtility.ParseQueryString(challenge.Headers.Location!.Query);
        Assert.Equal("https://scan.example.org/signin-oidc", query["redirect_uri"]);
    }

    [Fact]
    public async Task Without_a_proxy_address_sign_in_returns_to_the_address_that_was_used()
    {
        await ConnectAsync();
        await SignInAsync(Client(), _admin);

        var challenge = await Client().GetAsync("/signin");

        Assert.Equal("https://localhost/signin-oidc", System.Web.HttpUtility.ParseQueryString(challenge.Headers.Location!.Query)["redirect_uri"]);
    }

    [Fact]
    public async Task Signing_in_through_the_proxy_works_from_start_to_finish()
    {
        await ConnectAsync();
        await SignInAsync(Client(), _admin);
        await Get<SettingsStore>().UpdateAsync(s => s with { ProxyAddress = "https://scan.example.org" }, Actor.Setup);
        _remoteAddress = IPAddress.Parse("10.0.0.5");
        var viaProxy = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://scan.example.org") });
        viaProxy.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");

        var landed = await SignInAsync(viaProxy, _admin, "/jobs");

        Assert.Equal("/jobs", landed);
        Assert.Equal(HttpStatusCode.OK, (await viaProxy.GetAsync("/jobs")).StatusCode);
    }

    [Fact]
    public async Task Removing_the_admin_group_puts_the_screen_back_into_setup_on_the_server_only()
    {
        await ConnectAsync();
        var admin = Client();
        await SignInAsync(admin, _admin);
        Assert.Equal(HttpStatusCode.Redirect, (await Client().GetAsync("/settings")).StatusCode);

        // What the Settings page does when the admin group is removed.
        await Get<SettingsStore>().UpdateAsync(s => s with { AdminGroupId = null, AdminGroupName = null, SetupComplete = false }, Actor.Setup);

        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync("/setup")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Client().GetAsync("/signin")).StatusCode);
        Assert.Equal("/setup", (await Client().GetAsync("/signin")).Headers.Location!.ToString());
        _remoteAddress = IPAddress.Parse("203.0.113.9");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/settings")).StatusCode);

        // Choosing a group again on the server and signing in closes the window again.
        _remoteAddress = IPAddress.Loopback;
        var admins = _cloud.State.Groups.Values.Single(g => g.DisplayName == "Admins");
        await Get<SettingsStore>().UpdateAsync(s => s with { AdminGroupId = admins.Id }, Actor.Setup);
        await SignInAsync(Client(), _admin);
        Assert.True((await Get<SettingsStore>().GetAsync()).SetupComplete);
        Assert.Equal(HttpStatusCode.Redirect, (await Client().GetAsync("/settings")).StatusCode);
    }

    [Fact]
    public async Task A_helpdesk_sign_in_does_not_finish_setup()
    {
        await ConnectAsync();

        await SignInAsync(Client(), _helper);

        Assert.False((await Get<SettingsStore>().GetAsync()).SetupComplete);
    }

    [Fact]
    public async Task Someone_outside_the_groups_is_turned_away()
    {
        await ConnectAsync();
        var client = Client();

        var landed = await SignInAsync(client, _stranger);

        Assert.Equal("/denied?reason=not-allowed", landed);
        Assert.Contains("not in the group", await (await client.GetAsync(landed)).Content.ReadAsStringAsync());
        Assert.False((await Get<SettingsStore>().GetAsync()).SetupComplete);
    }

    [Fact]
    public async Task Helpdesk_can_view_jobs_and_runs_but_not_admin_pages_or_scan_files()
    {
        await ConnectAsync();
        await SignInAsync(Client(), _admin);
        var helper = Client();
        await SignInAsync(helper, _helper);
        var jobId = await AddJobAsync();

        Assert.Equal(HttpStatusCode.OK, (await helper.GetAsync("/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await helper.GetAsync("/runs")).StatusCode);
        foreach (var path in new[] { "/settings", "/printers", "/people", "/modules", "/audit", "/setup", $"/files/job/{jobId}", "/files/settings-export", "/files/printer/1/import", "/files/graph-certificate" })
        {
            var response = await helper.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"{path} answered {response.StatusCode}");
            Assert.StartsWith("/denied", response.Headers.Location!.OriginalString.Replace("https://localhost", ""));
        }
    }

    [Fact]
    public async Task An_admin_can_open_a_scan_file_and_that_is_audited()
    {
        await ConnectAsync();
        var admin = Client();
        await SignInAsync(admin, _admin);
        var jobId = await AddJobAsync();

        var response = await admin.GetAsync($"/files/job/{jobId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("%PDF-test", await response.Content.ReadAsStringAsync());
        await using var db = await Get<IDbContextFactory<ScanRelayDbContext>>().CreateDbContextAsync();
        var entry = Assert.Single(await db.AuditLog.Where(a => a.Action == "Scan file opened").ToListAsync());
        Assert.Equal("Alex Admin", entry.ActorName);
        Assert.Equal(jobId.ToString(), entry.Target);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/files/job/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task The_settings_export_downloads_without_secrets()
    {
        await ConnectAsync();
        var admin = Client();
        await SignInAsync(admin, _admin);
        await Get<SettingsStore>().SetSecretAsync("printer:1", "hunter2", Actor.Setup);

        var json = await admin.GetStringAsync("/files/settings-export");

        Assert.Contains("\"SyncIntervalMinutes\": \"60\"", json);
        Assert.DoesNotContain("hunter2", json);
    }

    [Theory]
    [InlineData("/jobs", true)]
    [InlineData("/setup?step=finish", true)]
    [InlineData("//evil.example", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("https://evil.example", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void After_sign_in_people_are_only_sent_to_pages_on_this_site(string? url, bool allowed)
    {
        Assert.Equal(allowed, ServiceHost.IsLocalPath(url));
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        await ConnectAsync();
        var admin = Client();
        await SignInAsync(admin, _admin);

        var signedOut = await admin.GetAsync("/signout");

        Assert.Equal("/denied?reason=signed-out", signedOut.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await admin.GetAsync("/settings")).StatusCode);
    }

    [Fact]
    public async Task Someone_removed_from_the_admin_group_cannot_sign_in_again()
    {
        await ConnectAsync();
        await SignInAsync(Client(), _admin);
        _cloud.State.Groups.Values.Single(g => g.DisplayName == "Admins").Members.Clear();

        Assert.Equal("/denied?reason=not-allowed", await SignInAsync(Client(), _admin));
    }

    private async Task<Guid> AddJobAsync()
    {
        var job = new Job { Id = Guid.NewGuid(), FolderName = "x", OriginalFileName = "scan.pdf", Status = JobStatus.Failed, DetectedUtc = DateTime.UtcNow, FinishedUtc = DateTime.UtcNow };
        await using var db = await Get<IDbContextFactory<ScanRelayDbContext>>().CreateDbContextAsync();
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        await File.WriteAllTextAsync(Get<Core.Jobs.JobPipeline>().StorePath(job), "%PDF-test");
        return job.Id;
    }

    /// <summary>The in-memory test server has no network address, so the test says where each request comes from.</summary>
    private sealed class RemoteAddressFilter(Func<IPAddress> address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                context.Connection.RemoteIpAddress = address();
                return following();
            });
            next(app);
        };
    }
}
