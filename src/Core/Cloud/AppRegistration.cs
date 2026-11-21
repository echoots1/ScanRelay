using System.Text.Json.Nodes;
using ScanRelay.Core.Install;

namespace ScanRelay.Core.Cloud;

/// <summary>One permission the app registration asks for.</summary>
public sealed record RequiredPermission(string Name, Guid Id, bool IsApplication, string UsedFor);

/// <summary>
/// Creates or repairs the Entra app registration with the signed-in admin's own access.
/// Nothing from that sign-in is kept once this finishes.
/// </summary>
public sealed class AppRegistration(HttpClient http, InstallConfig install, GraphCertificate certificate, TimeProvider time)
{
    public const string DisplayName = "ScanRelay";
    public const string MicrosoftGraphAppId = "00000003-0000-0000-c000-000000000000";

    /// <summary>The permissions in the spec. The ids are Microsoft Graph's own, the same in every tenant.</summary>
    public static readonly IReadOnlyList<RequiredPermission> Permissions =
    [
        new("GroupMember.Read.All", Guid.Parse("98830695-27a2-44f7-8c18-0c3ebc9698f6"), true, "Reading who is in the sync groups and the admin group"),
        new("User.Read.All", Guid.Parse("df021288-bdef-4463-88db-98f22de89214"), true, "Finding people by name and locating their OneDrive"),
        new("Files.ReadWrite.All", Guid.Parse("75359482-378d-4052-8f01-80520e7db3cd"), true, "Uploading scans to each person's OneDrive"),
        new("Mail.Send", Guid.Parse("b633e1c5-b582-4048-a93e-9f11b44c7e96"), true, "Sending alert emails to the admin group"),
        new("User.Read", Guid.Parse("e1fe6dd8-ba31-4d61-89e7-88639da4683d"), false, "Signing IT in to the admin screen"),
    ];

    public static IEnumerable<string> ApplicationPermissionNames => Permissions.Where(p => p.IsApplication).Select(p => p.Name);

    public sealed record Result(string TenantId, string ClientId, bool Created);

    /// <summary>The address Entra sends people back to after signing in to the admin screen.</summary>
    public static string RedirectUri(string adminUrl) => adminUrl.TrimEnd('/') + "/signin-oidc";

    /// <summary>Finds the registration by name and repairs it, or creates it.</summary>
    public async Task<Result> CreateOrRepairAsync(string delegatedToken, CancellationToken ct = default)
    {
        var graph = new GraphHttp(http, install.Cloud.Graph, _ => Task.FromResult(delegatedToken), time);
        var tenantId = Jwt.ReadPayload(delegatedToken)["tid"]?.GetValue<string>()
            ?? throw new CloudException("The sign-in did not say which tenant it was for.");

        var cert = certificate.Get();
        // Both the server's name and localhost, so signing in works from the server itself too.
        string[] ours = [RedirectUri(install.Url), RedirectUri(install.LocalUrl())];
        var found = await graph.GetAsync($"applications?$filter=displayName eq '{DisplayName}'&$select=id,appId,web,keyCredentials", ct);
        var existing = (found["value"] as JsonArray)?.FirstOrDefault()?.AsObject();

        var resourceAccess = new JsonArray([.. Permissions.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id.ToString(),
            ["type"] = p.IsApplication ? "Role" : "Scope",
        })]);
        var body = new JsonObject
        {
            ["displayName"] = DisplayName,
            ["signInAudience"] = "AzureADMyOrg",
            ["requiredResourceAccess"] = new JsonArray(new JsonObject
            {
                ["resourceAppId"] = MicrosoftGraphAppId,
                ["resourceAccess"] = resourceAccess,
            }),
            ["keyCredentials"] = new JsonArray(new JsonObject
            {
                ["type"] = "AsymmetricX509Cert",
                ["usage"] = "Verify",
                ["displayName"] = "ScanRelay service",
                ["key"] = certificate.PublicKeyBase64(),
                ["startDateTime"] = cert.NotBefore.ToUniversalTime().ToString("o"),
                ["endDateTime"] = cert.NotAfter.ToUniversalTime().ToString("o"),
            }),
        };

        string appId;
        bool created;
        if (existing is null)
        {
            body["web"] = new JsonObject { ["redirectUris"] = new JsonArray([.. ours.Distinct(StringComparer.OrdinalIgnoreCase).Select(r => (JsonNode)r)]) };
            var app = await graph.PostAsync("applications", body, ct);
            appId = app["appId"]!.GetValue<string>();
            created = true;
        }
        else
        {
            // Keep any redirect addresses already there, so a second server or a renamed one keeps working.
            var redirects = (existing["web"]?["redirectUris"] as JsonArray ?? [])
                .Select(r => r!.GetValue<string>())
                .Concat(ours)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            body["web"] = new JsonObject { ["redirectUris"] = new JsonArray([.. redirects.Select(r => (JsonNode)r)]) };
            await graph.PatchAsync($"applications/{existing["id"]!.GetValue<string>()}", body, ct);
            appId = existing["appId"]!.GetValue<string>();
            created = false;
        }

        var principals = await graph.GetAsync($"servicePrincipals?$filter=appId eq '{appId}'&$select=id", ct);
        if ((principals["value"] as JsonArray)?.Count is null or 0)
        {
            await graph.PostAsync("servicePrincipals", new JsonObject { ["appId"] = appId }, ct);
        }

        return new Result(tenantId, appId, created);
    }

    /// <summary>The link someone with the right role opens to approve the permissions.</summary>
    public string AdminConsentUrl(string tenantId, string clientId) =>
        $"{install.Cloud.Login.TrimEnd('/')}/{tenantId}/adminconsent?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(RedirectUri(install.Url))}";
}
