using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ScanRelay.MockCloud;

/// <summary>
/// A stand-in for Entra sign-in, Microsoft Graph, OneDrive and Exchange mail.
/// It checks what the real services check: the certificate signature on the
/// service's sign-in, consent before app permissions work, and name clashes on upload.
/// </summary>
public sealed partial class MockCloudServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private MockCloudServer(WebApplication app, MockCloudState state)
    {
        _app = app;
        State = state;
    }

    public MockCloudState State { get; }
    public string BaseUrl { get; private set; } = "";
    public string LoginUrl => BaseUrl + "/login";
    public string GraphUrl => BaseUrl + "/graph/v1.0";

    public static async Task<MockCloudServer> StartAsync(int port = 0, MockCloudState? state = null)
    {
        state ??= new MockCloudState();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(state);
        var app = builder.Build();
        var server = new MockCloudServer(app, state);
        app.Map("/login/{**rest}", server.HandleLoginAsync);
        app.Map("/graph/v1.0/{**rest}", server.HandleGraphAsync);
        app.Map("/upload/{session}", server.HandleUploadSessionAsync);
        await app.StartAsync();
        server.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        return server;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    // ---------------------------------------------------------------- tokens

    private string Issuer => $"{LoginUrl}/{State.TenantId}/v2.0";

    private string IssueToken(JsonObject claims)
    {
        var now = DateTimeOffset.UtcNow;
        claims["iss"] = Issuer;
        claims["tid"] = State.TenantId;
        claims["iat"] = now.ToUnixTimeSeconds();
        claims["nbf"] = now.AddMinutes(-1).ToUnixTimeSeconds();
        claims["exp"] = now.AddHours(1).ToUnixTimeSeconds();
        var header = new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = State.KeyId };
        var unsigned = B64(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + B64(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var signature = State.SigningKey.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return unsigned + "." + B64(signature);
    }

    private JsonObject? ReadToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var parts = header[7..].Split('.');
        if (parts.Length != 3) return null;
        var ok = State.SigningKey.VerifyData(
            Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), UnB64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return ok ? JsonNode.Parse(UnB64(parts[1]))!.AsObject() : null;
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnB64(string t)
    {
        var s = t.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }

    private static IResult OAuthError(string error, string description, int status = 400) =>
        Results.Json(new { error, error_description = description }, statusCode: status);

    private static IResult GraphError(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: status);

    // ----------------------------------------------------------------- login

    private async Task<IResult> HandleLoginAsync(HttpContext context, string rest)
    {
        var request = context.Request;
        var parts = rest.Split('/', 2);
        var path = parts.Length > 1 ? parts[1] : "";

        if (path == "v2.0/.well-known/openid-configuration")
        {
            var b = $"{LoginUrl}/{State.TenantId}";
            return Results.Json(new Dictionary<string, object>
            {
                ["issuer"] = Issuer,
                ["authorization_endpoint"] = b + "/oauth2/v2.0/authorize",
                ["token_endpoint"] = b + "/oauth2/v2.0/token",
                ["jwks_uri"] = b + "/discovery/v2.0/keys",
                ["end_session_endpoint"] = b + "/oauth2/v2.0/logout",
                ["response_types_supported"] = new[] { "code" },
                ["subject_types_supported"] = new[] { "pairwise" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["response_modes_supported"] = new[] { "query", "form_post" },
            });
        }

        if (path == "discovery/v2.0/keys")
        {
            var p = State.SigningKey.ExportParameters(false);
            return Results.Json(new
            {
                keys = new[] { new { kty = "RSA", use = "sig", kid = State.KeyId, alg = "RS256", n = B64(p.Modulus!), e = B64(p.Exponent!) } },
            });
        }

        if (path == "oauth2/v2.0/devicecode")
        {
            var code = Guid.NewGuid().ToString("N");
            State.DeviceCodes[code] = State.AutoApproveDeviceCodes;
            return Results.Json(new
            {
                user_code = "MOCK-" + code[..4].ToUpperInvariant(),
                device_code = code,
                verification_uri = LoginUrl + "/device",
                expires_in = 900,
                interval = 1,
                message = "Open the link and enter the code.",
            });
        }

        if (path == "oauth2/v2.0/authorize")
        {
            var q = request.Query;
            if (State.SignInAs is not { } userId || !State.Users.ContainsKey(userId))
            {
                return Results.Content("<h1>Mock sign-in</h1><p>No test user is selected.</p>", "text/html", statusCode: 400);
            }

            var code = Guid.NewGuid().ToString("N");
            State.AuthCodes[code] = (userId, q["nonce"].ToString(), q["client_id"].ToString());
            var redirect = q["redirect_uri"].ToString();
            var state = q["state"].ToString();
            if (q["response_mode"] == "form_post")
            {
                var html = $"""
                    <html><body onload="document.forms[0].submit()">
                    <form method="post" action="{System.Net.WebUtility.HtmlEncode(redirect)}">
                    <input type="hidden" name="code" value="{code}"/>
                    <input type="hidden" name="state" value="{System.Net.WebUtility.HtmlEncode(state)}"/>
                    <noscript><button type="submit">Continue</button></noscript>
                    </form></body></html>
                    """;
                return Results.Content(html, "text/html");
            }

            return Results.Redirect($"{redirect}?code={code}&state={Uri.EscapeDataString(state)}");
        }

        if (path == "adminconsent")
        {
            var clientId = request.Query["client_id"].ToString();
            var app = State.Applications.Values.FirstOrDefault(a => a.AppId == clientId);
            if (app is null) return Results.Content("Unknown application.", statusCode: 400);
            app.ConsentGranted = true;
            return Results.Content("<h1>Mock consent</h1><p>Permissions approved. You can close this page.</p>", "text/html");
        }

        if (path == "oauth2/v2.0/logout")
        {
            var back = request.Query["post_logout_redirect_uri"].ToString();
            var state = request.Query["state"].ToString();
            return string.IsNullOrEmpty(back) ? Results.Content("Signed out.") : Results.Redirect(back + (string.IsNullOrEmpty(state) ? "" : "?state=" + Uri.EscapeDataString(state)));
        }

        if (path == "oauth2/v2.0/token" && request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            return Token(form);
        }

        return Results.NotFound();
    }

    private IResult Token(IFormCollection form)
    {
        var grant = form["grant_type"].ToString();
        if (grant == "urn:ietf:params:oauth:grant-type:device_code")
        {
            var code = form["device_code"].ToString();
            if (!State.DeviceCodes.TryGetValue(code, out var approved)) return OAuthError("expired_token", "The code has expired.");
            if (!approved) return OAuthError("authorization_pending", "Waiting for the person to sign in.");
            State.DeviceCodes.TryRemove(code, out _);
            var user = State.SignInAs is { } id && State.Users.TryGetValue(id, out var u) ? u : null;
            return Results.Json(new
            {
                token_type = "Bearer",
                expires_in = 3600,
                access_token = IssueToken(new JsonObject
                {
                    ["aud"] = "https://graph.microsoft.com",
                    ["scp"] = "Application.ReadWrite.All",
                    ["oid"] = (user?.Id ?? Guid.Empty).ToString(),
                    ["name"] = user?.DisplayName ?? "Setup Admin",
                }),
            });
        }

        var clientId = form["client_id"].ToString();
        var app = State.Applications.Values.FirstOrDefault(a => a.AppId == clientId);
        if (app is null) return OAuthError("unauthorized_client", $"AADSTS700016: Application with identifier '{clientId}' was not found in the directory.");
        if (!AssertionIsValid(app, form["client_assertion"].ToString()))
        {
            return OAuthError("invalid_client", "AADSTS700027: Client assertion contains an invalid signature.", 401);
        }

        if (grant == "client_credentials")
        {
            var roles = new JsonArray();
            if (app.ConsentGranted && app.HasServicePrincipal)
            {
                foreach (var name in RoleNames(app)) roles.Add(name);
            }

            return Results.Json(new
            {
                token_type = "Bearer",
                expires_in = 3600,
                access_token = IssueToken(new JsonObject { ["aud"] = "https://graph.microsoft.com", ["appid"] = app.AppId, ["roles"] = roles }),
            });
        }

        if (grant == "authorization_code")
        {
            if (!State.AuthCodes.TryRemove(form["code"].ToString(), out var code) || code.ClientId != clientId)
            {
                return OAuthError("invalid_grant", "AADSTS70008: The code has expired or was already used.");
            }

            var user = State.Users[code.User];
            var claims = new JsonObject
            {
                ["aud"] = clientId,
                ["sub"] = user.Id.ToString("N"),
                ["oid"] = user.Id.ToString(),
                ["name"] = user.DisplayName,
                ["preferred_username"] = user.Upn,
            };
            if (!string.IsNullOrEmpty(code.Nonce)) claims["nonce"] = code.Nonce;
            return Results.Json(new
            {
                token_type = "Bearer",
                expires_in = 3600,
                scope = "openid profile User.Read",
                access_token = IssueToken(new JsonObject { ["aud"] = "https://graph.microsoft.com", ["scp"] = "User.Read", ["oid"] = user.Id.ToString() }),
                id_token = IssueToken(claims),
            });
        }

        return OAuthError("unsupported_grant_type", "Not supported by the mock.");
    }

    private static readonly Dictionary<string, string> KnownRoles = new()
    {
        ["98830695-27a2-44f7-8c18-0c3ebc9698f6"] = "GroupMember.Read.All",
        ["df021288-bdef-4463-88db-98f22de89214"] = "User.Read.All",
        ["75359482-378d-4052-8f01-80520e7db3cd"] = "Files.ReadWrite.All",
        ["b633e1c5-b582-4048-a93e-9f11b44c7e96"] = "Mail.Send",
    };

    private static IEnumerable<string> RoleNames(MockApplication app) =>
        app.RequestedRoleIds.Where(KnownRoles.ContainsKey).Select(id => KnownRoles[id]);

    /// <summary>Checks the assertion was signed by a certificate uploaded to the registration, as Entra does.</summary>
    private bool AssertionIsValid(MockApplication app, string assertion)
    {
        var parts = assertion.Split('.');
        if (parts.Length != 3) return false;
        try
        {
            var header = JsonNode.Parse(UnB64(parts[0]))!;
            var payload = JsonNode.Parse(UnB64(parts[1]))!;
            if (payload["iss"]?.GetValue<string>() != app.AppId || payload["sub"]?.GetValue<string>() != app.AppId) return false;
            if (payload["aud"]?.GetValue<string>() != $"{LoginUrl}/{State.TenantId}/oauth2/v2.0/token") return false;
            if (payload["exp"]!.GetValue<long>() < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
            var thumbprint = UnB64(header["x5t"]!.GetValue<string>());
            foreach (var raw in app.Certificates)
            {
                using var cert = X509CertificateLoader.LoadCertificate(raw);
                if (!cert.GetCertHash().AsSpan().SequenceEqual(thumbprint)) continue;
                using var key = cert.GetRSAPublicKey()!;
                return key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), UnB64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException or NullReferenceException)
        {
        }

        return false;
    }

    // ----------------------------------------------------------------- graph

    [GeneratedRegex(@"^drives/([^/]+)/items/([^/:]+):/([^:]+?)(:/content|:/createUploadSession)?$")]
    private static partial Regex ItemByPath();

    [GeneratedRegex(@"startswith\((\w+),'((?:[^']|'')*)'\)")]
    private static partial Regex StartsWith();

    [GeneratedRegex(@"(\w+) eq '((?:[^']|'')*)'")]
    private static partial Regex Equal();

    private async Task<IResult> HandleGraphAsync(HttpContext context, string rest)
    {
        var request = context.Request;
        var method = request.Method;
        rest = Uri.UnescapeDataString(rest).TrimEnd('/');
        State.GraphRequests.Enqueue($"{method} {rest}");

        if (Interlocked.Decrement(ref State.FailNextGraphRequests) >= 0)
        {
            if (State.FailStatus == 429) context.Response.Headers.RetryAfter = "0";
            return GraphError(State.FailStatus, "serviceNotAvailable", "The mock was told to fail this request.");
        }

        Interlocked.Exchange(ref State.FailNextGraphRequests, Math.Max(0, State.FailNextGraphRequests));

        var token = ReadToken(request);
        if (token is null) return GraphError(401, "InvalidAuthenticationToken", "Access token is missing or invalid.");
        var roles = (token["roles"] as JsonArray ?? []).Select(r => r!.GetValue<string>()).ToHashSet();
        var delegated = token["scp"]?.GetValue<string>() ?? "";
        IResult? Need(string role) =>
            roles.Contains(role) ? null : GraphError(403, "Authorization_RequestDenied", "Insufficient privileges to complete the operation.");

        var filter = request.Query["$filter"].ToString();

        // ---- setup: app registration, with the signed-in admin's delegated access
        if (rest is "applications" or "servicePrincipals" || rest.StartsWith("applications/", StringComparison.Ordinal))
        {
            if (!delegated.Contains("Application.ReadWrite.All")) return GraphError(403, "Authorization_RequestDenied", "Insufficient privileges to complete the operation.");
            return await HandleApplicationsAsync(request, rest, filter);
        }

        if (rest == "organization") return Results.Json(new { value = new[] { new { id = State.TenantId, displayName = State.OrganizationName } } });

        // ---- groups
        if (rest == "groups")
        {
            if (Need("GroupMember.Read.All") is { } denied) return denied;
            var prefix = StartsWith().Match(filter) is { Success: true } m ? m.Groups[2].Value.Replace("''", "'") : "";
            return Results.Json(new
            {
                value = State.Groups.Values
                    .Where(g => g.DisplayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(g => new { id = g.Id, displayName = g.DisplayName }),
            });
        }

        var segments = rest.Split('/');
        if (segments[0] == "groups" && Guid.TryParse(segments.ElementAtOrDefault(1), out var groupId))
        {
            if (Need("GroupMember.Read.All") is { } denied) return denied;
            if (!State.Groups.TryGetValue(groupId, out var group)) return GraphError(404, "Request_ResourceNotFound", "Group not found.");
            if (segments.Length == 2) return Results.Json(new { id = group.Id, displayName = group.DisplayName });
            if (rest.EndsWith("/transitiveMembers/microsoft.graph.user", StringComparison.Ordinal))
            {
                // Two pages, so paging is always exercised.
                var all = State.TransitiveMembers(group).DistinctBy(u => u.Id).OrderBy(u => u.Id).ToList();
                var skip = int.TryParse(request.Query["$skiptoken"], out var s) ? s : 0;
                var pageSize = Math.Max(1, (all.Count + 1) / 2);
                var page = all.Skip(skip).Take(pageSize).Select(UserJson).ToList();
                var result = new JsonObject { ["value"] = new JsonArray([.. page]) };
                if (skip + pageSize < all.Count)
                {
                    result["@odata.nextLink"] = $"{GraphUrl}/groups/{groupId}/transitiveMembers/microsoft.graph.user?$skiptoken={skip + pageSize}";
                }

                return Results.Json(result);
            }
        }

        // ---- users
        if (rest == "users")
        {
            if (Need("User.Read.All") is { } denied) return denied;
            var prefix = StartsWith().Match(filter) is { Success: true } m ? m.Groups[2].Value.Replace("''", "'") : "";
            return Results.Json(new JsonObject
            {
                ["value"] = new JsonArray([.. State.Users.Values
                    .Where(u => u.DisplayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        || u.Upn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        || u.DisplayName.Split(' ').Last().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(UserJson)]),
            });
        }

        if (segments[0] == "users" && segments.Length >= 2)
        {
            var user = Guid.TryParse(segments[1], out var uid)
                ? State.Users.GetValueOrDefault(uid)
                : State.Users.Values.FirstOrDefault(u => u.Upn.Equals(segments[1], StringComparison.OrdinalIgnoreCase));
            var tail = string.Join('/', segments.Skip(2));

            if (tail == "sendMail" && method == "POST")
            {
                if (Need("Mail.Send") is { } denied) return denied;
                if (user is null) return GraphError(404, "ErrorInvalidUser", "The requested user is invalid.");
                var body = (await JsonNode.ParseAsync(request.Body))!["message"]!;
                State.Outbox.Enqueue(new SentMail(
                    user.Upn,
                    [.. body["toRecipients"]!.AsArray().Select(r => r!["emailAddress"]!["address"]!.GetValue<string>())],
                    body["subject"]!.GetValue<string>(),
                    body["body"]!["content"]!.GetValue<string>()));
                return Results.Accepted();
            }

            if (user is null) return GraphError(404, "Request_ResourceNotFound", "User not found.");
            if (tail == "")
            {
                if (Need("User.Read.All") is { } denied) return denied;
                return Results.Json(UserJson(user));
            }

            if (tail == "checkMemberGroups" && method == "POST")
            {
                if (Need("GroupMember.Read.All") is { } denied) return denied;
                var ids = (await JsonNode.ParseAsync(request.Body))!["groupIds"]!.AsArray().Select(i => Guid.Parse(i!.GetValue<string>()));
                var member = ids.Where(id => State.Groups.TryGetValue(id, out var g) && State.TransitiveMembers(g).Any(u => u.Id == user.Id));
                return Results.Json(new { value = member });
            }

            if (tail.StartsWith("drive", StringComparison.Ordinal))
            {
                if (Need("Files.ReadWrite.All") is { } denied) return denied;
                if (!user.HasDrive) return GraphError(404, "ResourceNotFound", "User's mysite not found.");
                if (tail == "drive") return Results.Json(new { id = user.DriveId });
                if (tail == "drive/root") return Results.Json(ItemJson(State.Root(user.DriveId)));
                if (tail == "drive/special/documents") return Results.Json(ItemJson(State.Documents(user.DriveId)));
            }
        }

        // ---- drive items
        if (segments[0] == "drives")
        {
            if (Need("Files.ReadWrite.All") is { } denied) return denied;
            if (ItemByPath().Match(rest) is { Success: true } byPath)
            {
                var (driveId, parentId, name, action) = (byPath.Groups[1].Value, byPath.Groups[2].Value, byPath.Groups[3].Value, byPath.Groups[4].Value);
                if (!State.Items.ContainsKey(parentId)) return GraphError(404, "itemNotFound", "The parent folder was not found.");
                var existing = State.Child(parentId, name);
                if (action == "" && method == "GET")
                {
                    return existing is null ? GraphError(404, "itemNotFound", "Item not found.") : Results.Json(ItemJson(existing));
                }

                if (existing is not null) return GraphError(409, "nameAlreadyExists", "An item with the same name already exists.");
                if (action == ":/content" && method == "PUT")
                {
                    using var buffer = new MemoryStream();
                    await request.Body.CopyToAsync(buffer);
                    return Results.Json(ItemJson(State.AddItem(driveId, parentId, name, folder: false, buffer.ToArray())), statusCode: 201);
                }

                if (action == ":/createUploadSession" && method == "POST")
                {
                    var session = Guid.NewGuid().ToString("N");
                    State.UploadSessions[session] = (driveId, parentId, name, new MemoryStream());
                    return Results.Json(new { uploadUrl = $"{BaseUrl}/upload/{session}", expirationDateTime = DateTimeOffset.UtcNow.AddHours(1) });
                }
            }

            if (segments.Length == 5 && segments[2] == "items" && segments[4] == "children" && method == "POST")
            {
                var body = (await JsonNode.ParseAsync(request.Body))!;
                var name = body["name"]!.GetValue<string>();
                if (!State.Items.ContainsKey(segments[3])) return GraphError(404, "itemNotFound", "The parent folder was not found.");
                if (State.Child(segments[3], name) is not null) return GraphError(409, "nameAlreadyExists", "An item with the same name already exists.");
                return Results.Json(ItemJson(State.AddItem(segments[1], segments[3], name, folder: true)), statusCode: 201);
            }

            if (segments.Length == 4 && segments[2] == "items" && method == "GET")
            {
                return State.Items.TryGetValue(segments[3], out var item) ? Results.Json(ItemJson(item)) : GraphError(404, "itemNotFound", "Item not found.");
            }
        }

        return GraphError(400, "BadRequest", $"The mock does not handle {method} {rest}.");
    }

    private async Task<IResult> HandleApplicationsAsync(HttpRequest request, string rest, string filter)
    {
        var value = Equal().Match(filter) is { Success: true } m ? m.Groups[2].Value : null;
        if (rest == "servicePrincipals")
        {
            if (request.Method == "GET")
            {
                var has = State.Applications.Values.Where(a => a.AppId == value && a.HasServicePrincipal);
                return Results.Json(new { value = has.Select(a => new { id = "sp-" + a.ObjectId }) });
            }

            var appId = (await JsonNode.ParseAsync(request.Body))!["appId"]!.GetValue<string>();
            var target = State.Applications.Values.FirstOrDefault(a => a.AppId == appId);
            if (target is null) return GraphError(400, "Request_BadRequest", "The appId does not reference a valid application object.");
            target.HasServicePrincipal = true;
            return Results.Json(new { id = "sp-" + target.ObjectId, appId }, statusCode: 201);
        }

        if (rest == "applications" && request.Method == "GET")
        {
            var found = State.Applications.Values.Where(a => value is null || a.DisplayName == value);
            return Results.Json(new JsonObject { ["value"] = new JsonArray([.. found.Select(AppJson)]) });
        }

        var body = (await JsonNode.ParseAsync(request.Body))!.AsObject();
        MockApplication app;
        if (rest == "applications" && request.Method == "POST")
        {
            app = new MockApplication();
            State.Applications[app.ObjectId] = app;
        }
        else if (request.Method == "PATCH" && State.Applications.TryGetValue(rest["applications/".Length..], out var existing))
        {
            app = existing;
        }
        else
        {
            return GraphError(404, "Request_ResourceNotFound", "Application not found.");
        }

        if (body["signInAudience"]?.GetValue<string>() is { } audience && audience != "AzureADMyOrg")
        {
            return GraphError(400, "Request_BadRequest", "The mock only accepts single-tenant registrations.");
        }

        app.DisplayName = body["displayName"]?.GetValue<string>() ?? app.DisplayName;
        if (body["web"]?["redirectUris"] is JsonArray uris)
        {
            app.RedirectUris = [.. uris.Select(u => u!.GetValue<string>())];
            if (app.RedirectUris.Any(u => !u.StartsWith("https://", StringComparison.Ordinal) && !u.StartsWith("http://localhost", StringComparison.Ordinal)
                && !u.StartsWith("http://127.0.0.1", StringComparison.Ordinal)))
            {
                return GraphError(400, "InvalidRedirectUri", "Redirect URIs must be HTTPS, except for localhost.");
            }
        }

        if (body["keyCredentials"] is JsonArray keys)
        {
            app.Certificates = [.. keys.Select(k => Convert.FromBase64String(k!["key"]!.GetValue<string>()))];
        }

        if (body["requiredResourceAccess"] is JsonArray access)
        {
            var before = app.RequestedRoleIds.ToHashSet();
            app.RequestedRoleIds = [.. access.SelectMany(r => r!["resourceAccess"]!.AsArray())
                .Where(a => a!["type"]!.GetValue<string>() == "Role")
                .Select(a => a!["id"]!.GetValue<string>())];

            // Asking for something new needs fresh consent, as in Entra.
            if (!app.RequestedRoleIds.ToHashSet().IsSubsetOf(before)) app.ConsentGranted = false;
        }

        return request.Method == "POST" ? Results.Json(AppJson(app), statusCode: 201) : Results.NoContent();
    }

    private async Task<IResult> HandleUploadSessionAsync(HttpContext context, string session)
    {
        if (!State.UploadSessions.TryGetValue(session, out var upload)) return GraphError(404, "itemNotFound", "Upload session not found.");
        var range = context.Request.Headers.ContentRange.ToString();
        var match = Regex.Match(range, @"bytes (\d+)-(\d+)/(\d+)");
        if (!match.Success) return GraphError(400, "invalidRequest", "Content-Range is required.");
        var (from, to, total) = (long.Parse(match.Groups[1].Value), long.Parse(match.Groups[2].Value), long.Parse(match.Groups[3].Value));
        if (from != upload.Data.Length) return GraphError(416, "invalidRange", "The piece does not follow the last one.");
        await context.Request.Body.CopyToAsync(upload.Data);
        if (upload.Data.Length != to + 1) return GraphError(400, "invalidRequest", "The piece is not the size its range says.");
        if (upload.Data.Length < total)
        {
            return Results.Json(new { nextExpectedRanges = new[] { $"{upload.Data.Length}-" } }, statusCode: 202);
        }

        State.UploadSessions.TryRemove(session, out _);
        if (State.Child(upload.ParentId, upload.Name) is not null) return GraphError(409, "nameAlreadyExists", "An item with the same name already exists.");
        return Results.Json(ItemJson(State.AddItem(upload.DriveId, upload.ParentId, upload.Name, folder: false, upload.Data.ToArray())), statusCode: 201);
    }

    private static JsonNode UserJson(MockUser u) => new JsonObject
    {
        ["id"] = u.Id.ToString(),
        ["displayName"] = u.DisplayName,
        ["userPrincipalName"] = u.Upn,
        ["accountEnabled"] = u.Enabled,
        ["userType"] = u.Guest ? "Guest" : "Member",
    };

    private static JsonObject ItemJson(MockItem i)
    {
        var json = new JsonObject
        {
            ["id"] = i.Id,
            ["name"] = i.Name,
            ["size"] = i.Content.Length,
            ["parentReference"] = new JsonObject { ["driveId"] = i.DriveId, ["id"] = i.ParentId },
        };
        json[i.IsFolder ? "folder" : "file"] = new JsonObject();
        return json;
    }

    private static JsonNode AppJson(MockApplication a) => new JsonObject
    {
        ["id"] = a.ObjectId,
        ["appId"] = a.AppId,
        ["displayName"] = a.DisplayName,
        ["web"] = new JsonObject { ["redirectUris"] = new JsonArray([.. a.RedirectUris.Select(u => (JsonNode)u)]) },
    };
}
