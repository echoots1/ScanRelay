using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScanRelay.Core.Install;
using ScanRelay.Core.Settings;

namespace ScanRelay.Core.Cloud;

public sealed class CloudException(string message, int? status = null, string? code = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? Status { get; } = status;
    public string? Code { get; } = code;

    /// <summary>True when trying again later might work.</summary>
    public bool IsTransient => Status is null or 408 or 429 or >= 500;
}

public static class Jwt
{
    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }

    /// <summary>A signed statement that proves the service holds the certificate's key.</summary>
    public static string ClientAssertion(X509Certificate2 certificate, string clientId, string audience, DateTimeOffset now)
    {
        var header = new JsonObject
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            ["x5t"] = Base64Url(certificate.GetCertHash()),
        };
        var payload = new JsonObject
        {
            ["aud"] = audience,
            ["iss"] = clientId,
            ["sub"] = clientId,
            ["jti"] = Guid.NewGuid().ToString(),
            ["nbf"] = now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(9).ToUnixTimeSeconds(),
        };
        var unsigned = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        using var key = certificate.GetRSAPrivateKey()
            ?? throw new CloudException("The Graph certificate has no usable private key.");
        var signature = key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return unsigned + "." + Base64Url(signature);
    }

    /// <summary>Reads the claims of a token without checking it. Only used on tokens we were just issued.</summary>
    public static JsonObject ReadPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) throw new CloudException("The token is not in the expected form.");
        return JsonNode.Parse(FromBase64Url(parts[1]))!.AsObject();
    }
}

/// <summary>Gets app-only tokens for Microsoft Graph by signing in with the certificate.</summary>
public sealed class AppTokenProvider(
    HttpClient http,
    InstallConfig install,
    SettingsStore settings,
    GraphCertificate certificate,
    TimeProvider time)
{
    public const string ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;
    private string? _tokenFor;

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        if (string.IsNullOrEmpty(s.TenantId) || string.IsNullOrEmpty(s.ClientId))
        {
            throw new CloudException("The Entra connection is not set up yet.");
        }

        var key = s.TenantId + "/" + s.ClientId;
        await _lock.WaitAsync(ct);
        try
        {
            if (_token is not null && _tokenFor == key && time.GetUtcNow() < _expires) return _token;
            var (token, expiresIn) = await RequestAsync(s.TenantId, s.ClientId, ct);
            _token = token;
            _tokenFor = key;
            _expires = time.GetUtcNow().AddSeconds(Math.Max(60, expiresIn - 300));
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forgets the cached token, so the next call sees newly approved permissions.</summary>
    public void Reset() => _token = null;

    /// <summary>The application permissions the current token carries.</summary>
    public async Task<IReadOnlyList<string>> GetGrantedRolesAsync(CancellationToken ct = default)
    {
        var payload = Jwt.ReadPayload(await GetTokenAsync(ct));
        return payload["roles"] is JsonArray roles ? [.. roles.Select(r => r!.GetValue<string>())] : [];
    }

    public string TokenEndpoint(string tenantId) => $"{install.Cloud.Login.TrimEnd('/')}/{tenantId}/oauth2/v2.0/token";

    public string NewAssertion(string tenantId, string clientId) =>
        Jwt.ClientAssertion(certificate.Get(), clientId, TokenEndpoint(tenantId), time.GetUtcNow());

    private async Task<(string Token, int ExpiresIn)> RequestAsync(string tenantId, string clientId, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["scope"] = install.Cloud.GraphScope,
            ["client_assertion_type"] = ClientAssertionType,
            ["client_assertion"] = NewAssertion(tenantId, clientId),
        });
        var json = await TokenHttp.PostAsync(http, TokenEndpoint(tenantId), form, ct);
        return (json["access_token"]!.GetValue<string>(), json["expires_in"]?.GetValue<int>() ?? 3600);
    }
}

internal static class TokenHttp
{
    /// <summary>Posts to a token endpoint. Returns the body on success; throws with Entra's own words otherwise.</summary>
    public static async Task<JsonObject> PostAsync(HttpClient http, string url, HttpContent form, CancellationToken ct, bool throwOnError = true)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(url, form, ct);
        }
        catch (HttpRequestException e)
        {
            throw new CloudException("Could not reach the Microsoft sign-in service: " + e.Message, inner: e);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonObject? json = null;
            try
            {
                json = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
            }

            if (response.IsSuccessStatusCode && json is not null) return json;
            if (!throwOnError && json?["error"] is not null) return json;
            var code = json?["error"]?.GetValue<string>();
            var description = json?["error_description"]?.GetValue<string>()?.Split('\n')[0].Trim();
            throw new CloudException(
                description ?? $"The Microsoft sign-in service answered {(int)response.StatusCode}.",
                (int)response.StatusCode,
                code);
        }
    }
}

/// <summary>Signs a person in with a link and a code. Used once, by setup, to create the app registration.</summary>
public sealed class DeviceCodeFlow(HttpClient http, InstallConfig install)
{
    /// <summary>Microsoft Graph Command Line Tools, a Microsoft client that exists in every tenant.</summary>
    public const string SetupClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    public const string SetupScope = "https://graph.microsoft.com/Application.ReadWrite.All offline_access openid profile";

    public sealed record Started(string UserCode, string DeviceCode, string VerificationUri, int IntervalSeconds, DateTimeOffset Expires);

    public sealed record PollResult(string? AccessToken, bool Pending, string? Error);

    public async Task<Started> StartAsync(string? tenant, TimeProvider time, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = SetupClientId,
            ["scope"] = SetupScope,
        });
        var json = await TokenHttp.PostAsync(http, $"{Base(tenant)}/oauth2/v2.0/devicecode", form, ct);
        return new Started(
            json["user_code"]!.GetValue<string>(),
            json["device_code"]!.GetValue<string>(),
            json["verification_uri"]!.GetValue<string>(),
            json["interval"]?.GetValue<int>() ?? 5,
            time.GetUtcNow().AddSeconds(json["expires_in"]?.GetValue<int>() ?? 900));
    }

    public async Task<PollResult> PollAsync(string? tenant, string deviceCode, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = SetupClientId,
            ["device_code"] = deviceCode,
        });
        var json = await TokenHttp.PostAsync(http, $"{Base(tenant)}/oauth2/v2.0/token", form, ct, throwOnError: false);
        if (json["access_token"] is { } token) return new PollResult(token.GetValue<string>(), false, null);
        var error = json["error"]?.GetValue<string>();
        return error is "authorization_pending" or "slow_down"
            ? new PollResult(null, true, null)
            : new PollResult(null, false, json["error_description"]?.GetValue<string>()?.Split('\n')[0].Trim() ?? error);
    }

    private string Base(string? tenant) =>
        $"{install.Cloud.Login.TrimEnd('/')}/{(string.IsNullOrWhiteSpace(tenant) ? "organizations" : tenant)}";
}
