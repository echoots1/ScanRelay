using ScanRelay.Core.Cloud;
using ScanRelay.Core.Settings;

namespace ScanRelay.Core.Tests;

public class CloudTests
{
    [Fact]
    public async Task Setup_creates_a_single_tenant_registration_with_the_certificate_and_permissions()
    {
        await using var env = await TestEnvironment.StartAsync();

        var app = await env.ConnectAsync(consent: false);

        Assert.Equal("ScanRelay", app.DisplayName);
        Assert.Equal(["https://scanserver.example:8443/signin-oidc", "https://localhost:8443/signin-oidc"], app.RedirectUris);
        Assert.Single(app.Certificates);
        Assert.Equal(4, app.RequestedRoleIds.Count);
        Assert.True(app.HasServicePrincipal);
        var settings = await env.Settings.GetAsync();
        Assert.Equal(env.State.TenantId, settings.TenantId);
        Assert.Equal(app.AppId, settings.ClientId);
    }

    [Fact]
    public async Task Running_setup_again_repairs_the_same_registration()
    {
        await using var env = await TestEnvironment.StartAsync();
        var first = await env.ConnectAsync();
        first.RedirectUris = ["https://old-server.example/signin-oidc"];
        first.Certificates.Clear();

        var second = await env.ConnectAsync();

        Assert.Same(first, second);
        Assert.Single(env.State.Applications);
        Assert.Equal(["https://old-server.example/signin-oidc", "https://scanserver.example:8443/signin-oidc", "https://localhost:8443/signin-oidc"], second.RedirectUris);
        Assert.Single(second.Certificates);
    }

    [Fact]
    public async Task A_proxy_address_is_added_to_the_registration_when_setup_runs_again()
    {
        await using var env = await TestEnvironment.StartAsync();
        var app = await env.ConnectAsync();
        await env.Settings.UpdateAsync(s => s with { ProxyAddress = "https://scan.example.org/" }, Core.Audit.Actor.Setup);

        await env.ConnectAsync();

        Assert.Equal(
            ["https://scanserver.example:8443/signin-oidc", "https://localhost:8443/signin-oidc", "https://scan.example.org/signin-oidc"],
            app.RedirectUris);
    }

    [Theory]
    [InlineData("https://scan.example.org", "https://scan.example.org")]
    [InlineData(" https://Scan.Example.org:8443/ ", "https://scan.example.org:8443")]
    [InlineData("http://scan.example.org", null)]
    [InlineData("https://scan.example.org/admin", null)]
    [InlineData("scan.example.org", null)]
    [InlineData("", null)]
    public void A_proxy_address_must_be_a_plain_https_address(string value, string? expected)
    {
        var settings = new AppSettings { ProxyAddress = value };

        Assert.Equal(expected, settings.ProxyUrl());
        Assert.Equal(expected is null && value.Length > 0, settings.Validate().Count > 0);
    }

    [Fact]
    public async Task The_service_signs_in_with_its_certificate_and_sees_what_was_approved()
    {
        await using var env = await TestEnvironment.StartAsync();
        var app = await env.ConnectAsync(consent: false);

        Assert.Empty(await env.Graph.GetGrantedPermissionsAsync());

        app.ConsentGranted = true;
        env.Get<AppTokenProvider>().Reset();
        Assert.Equal(
            AppRegistration.ApplicationPermissionNames.Order(),
            (await env.Graph.GetGrantedPermissionsAsync()).Order());
        Assert.Equal("Example Organisation", await env.Graph.GetOrganizationNameAsync());
    }

    [Fact]
    public async Task A_certificate_entra_does_not_know_is_refused()
    {
        await using var env = await TestEnvironment.StartAsync();
        var app = await env.ConnectAsync();
        app.Certificates.Clear();
        env.Get<AppTokenProvider>().Reset();

        var error = await Assert.ThrowsAsync<CloudException>(() => env.Graph.GetOrganizationNameAsync());

        Assert.Contains("invalid signature", error.Message);
        Assert.Equal("invalid_client", error.Code);
    }

    [Fact]
    public async Task Without_consent_graph_refuses_and_says_why()
    {
        await using var env = await TestEnvironment.StartAsync();
        await env.ConnectAsync(consent: false);

        var error = await Assert.ThrowsAsync<CloudException>(() => env.Graph.SearchGroupsAsync(""));

        Assert.Equal(403, error.Status);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task A_token_issued_just_after_consent_without_its_permissions_is_not_kept()
    {
        await using var env = await TestEnvironment.StartAsync();
        await env.ConnectAsync();
        env.State.AddGroup("IT Staff");

        // Entra hands out a token that does not have the permissions yet. One refusal must not stick.
        env.State.TokensWithoutPermissions = 1;
        Assert.Single(await env.Graph.SearchGroupsAsync("IT"));

        // Several in a row: the first search may fail, but it recovers on its own within seconds, not an hour.
        env.Get<AppTokenProvider>().Reset();
        env.State.TokensWithoutPermissions = 2;
        await Assert.ThrowsAsync<CloudException>(() => env.Graph.SearchGroupsAsync("IT"));
        env.Get<AppTokenProvider>().Reset();
        Assert.Single(await env.Graph.SearchGroupsAsync("IT"));
    }

    [Fact]
    public async Task Group_members_include_nested_groups_across_pages()
    {
        await using var env = await TestEnvironment.StartAsync();
        await env.ConnectAsync();
        var people = Enumerable.Range(1, 5).Select(i => env.State.AddUser($"Person {i}")).ToArray();
        var inner = env.State.AddGroup("Inner", people[3], people[4]);
        var outer = env.State.AddGroup("Outer", people[0], people[1], people[2], people[3]);
        outer.NestedGroups.Add(inner.Id);

        var members = await env.Graph.GetGroupMembersAsync(outer.Id);

        Assert.Equal(people.Select(p => p.Id).Order(), members.Select(m => m.Id).Order());
        Assert.True(await env.Graph.IsMemberAsync(people[4].Id, outer.Id));
        Assert.False(await env.Graph.IsMemberAsync(people[0].Id, inner.Id));
    }

    [Fact]
    public async Task People_and_groups_can_be_found_by_name()
    {
        await using var env = await TestEnvironment.StartAsync();
        await env.ConnectAsync();
        var user = env.State.AddUser("Dara O'Neil");
        env.State.AddUser("Someone Else");
        var group = env.State.AddGroup("Scanner Users");

        Assert.Equal(user.Id, Assert.Single(await env.Graph.SearchUsersAsync("Dara O'")).Id);
        Assert.Equal(user.Id, Assert.Single(await env.Graph.SearchUsersAsync("o'neil")).Id);
        Assert.Equal(group.Id, Assert.Single(await env.Graph.SearchGroupsAsync("scan")).Id);
        Assert.Equal("Scanner Users", (await env.Graph.GetGroupAsync(group.Id))!.DisplayName);
        Assert.Null(await env.Graph.GetGroupAsync(Guid.NewGuid()));
        Assert.Null(await env.Graph.GetUserAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Upload_goes_under_the_real_documents_folder_and_creates_scans()
    {
        await using var env = await TestEnvironment.StartAsync();
        await env.ConnectAsync();
        var user = env.State.AddUser("Sam Scanner");
        var file = Path.Combine(env.Data.Root, "a.pdf");
        await File.WriteAllTextAsync(file, "%PDF-one");
        var target = new OneDriveTarget(true, ["Scans"], "Scan_2026-11-20_143205", "pdf");

        var first = await env.Graph.UploadAsync(user.Id, target, file);
        var second = await env.Graph.UploadAsync(user.Id, target, file);

        // The mock's Documents folder has a translated name, as in a non-English tenant.
        var files = env.State.Files(user, "Dokumente/Scans");
        Assert.Equal(["Scan_2026-11-20_143205 (2).pdf", "Scan_2026-11-20_143205.pdf"], files.Select(f => f.Name));
        Assert.Equal("%PDF-one", System.Text.Encoding.UTF8.GetString(files[1].Content));
        Assert.Equal("Scan_2026-11-20_143205.pdf", first.Name);
        Assert.Equal("Scan_2026-11-20_143205 (2).pdf", second.Name);
        Assert.True(await env.Graph.ItemExistsAsync(first.DriveId, first.ItemId));
        Assert.False(await env.Graph.ItemExistsAsync(first.DriveId, "missing"));
    }

    [Fact]
    public async Task A_file_over_the_single_call_limit_is_uploaded_in_pieces()
    {
        await using var env = await TestEnvironment.StartAsync();
        await env.ConnectAsync();
        var user = env.State.AddUser("Sam Scanner");
        var file = Path.Combine(env.Data.Root, "big.pdf");
        var content = new byte[(320 * 1024 * 2) + 12345];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(file, content);

        // The same client, with the limits brought down so a small file takes the large-file path.
        var cloud = new GraphCloud(env.Get<HttpClient>(), env.Install, env.Get<AppTokenProvider>(), TimeProvider.System)
        {
            SingleCallLimit = 1000,
            ChunkSize = 320 * 1024,
        };
        var target = new OneDriveTarget(true, ["Scans"], "Big", "pdf");

        var first = await cloud.UploadAsync(user.Id, target, file);
        var second = await cloud.UploadAsync(user.Id, target, file);

        var files = env.State.Files(user, "Dokumente/Scans");
        Assert.Equal(["Big (2).pdf", "Big.pdf"], files.Select(f => f.Name));
        Assert.All(files, f => Assert.Equal(content, f.Content));
        Assert.Equal("Big.pdf", first.Name);
        Assert.Equal("Big (2).pdf", second.Name);
        Assert.Contains(env.State.GraphRequests, r => r.Contains("createUploadSession"));
    }

    [Fact]
    public async Task Upload_can_go_to_a_folder_outside_documents()
    {
        await using var env = await TestEnvironment.StartAsync();
        await env.ConnectAsync();
        var user = env.State.AddUser("Sam Scanner");
        var file = Path.Combine(env.Data.Root, "a.pdf");
        await File.WriteAllTextAsync(file, "x");
        Assert.True(OneDrivePath.TryParse("Incoming/Scans", out var path, out _));

        await env.Graph.UploadAsync(user.Id, new OneDriveTarget(path!.UnderDocuments, path.Segments, "Scan", "pdf"), file);

        Assert.Single(env.State.Files(user, "Incoming/Scans"));
    }

    [Fact]
    public async Task Someone_without_onedrive_is_reported_as_such()
    {
        await using var env = await TestEnvironment.StartAsync();
        await env.ConnectAsync();
        var with = env.State.AddUser("Has Drive");
        var without = env.State.AddUser("No Drive", hasDrive: false);

        Assert.True(await env.Graph.HasOneDriveAsync(with.Id));
        Assert.False(await env.Graph.HasOneDriveAsync(without.Id));
    }

    [Fact]
    public async Task Graph_slowing_us_down_is_waited_out()
    {
        await using var env = await TestEnvironment.StartAsync();
        await env.ConnectAsync();
        env.State.FailStatus = 429;
        env.State.FailNextGraphRequests = 2;

        Assert.Equal("Example Organisation", await env.Graph.GetOrganizationNameAsync());
    }

    [Fact]
    public async Task A_graph_outage_is_reported_as_worth_retrying()
    {
        await using var env = await TestEnvironment.StartAsync();
        await env.ConnectAsync();
        env.State.FailStatus = 500;
        env.State.FailNextGraphRequests = 1;

        var error = await Assert.ThrowsAsync<CloudException>(() => env.Graph.GetOrganizationNameAsync());

        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task Mail_is_sent_from_the_chosen_mailbox()
    {
        await using var env = await TestEnvironment.StartAsync();
        await env.ConnectAsync();
        var sender = env.State.AddUser("Alerts", "alerts@example.com");

        await env.Graph.SendMailAsync("alerts@example.com", ["a@example.com", "b@example.com"], "Subject", "Body");

        var mail = Assert.Single(env.State.Outbox);
        Assert.Equal(sender.Upn, mail.Sender);
        Assert.Equal(["a@example.com", "b@example.com"], mail.To);
        Assert.Equal("Subject", mail.Subject);
    }
}
