using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScanRelay.Core.Cloud;

/// <summary>Sends requests to Microsoft Graph with a token, waits when Graph says to slow down, and turns errors into <see cref="CloudException"/>.</summary>
public sealed class GraphHttp(HttpClient http, string baseUrl, Func<CancellationToken, Task<string>> token, TimeProvider time, Action? forgetToken = null)
{
    private const int MaxThrottleRetries = 2;
    private static readonly TimeSpan MaxThrottleWait = TimeSpan.FromSeconds(30);

    public string BaseUrl { get; } = baseUrl.TrimEnd('/');

    public Task<JsonObject> GetAsync(string path, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Get, path, null, ct);

    public async Task<JsonObject?> GetOrNullAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await GetAsync(path, ct);
        }
        catch (CloudException e) when (e.Status == 404)
        {
            return null;
        }
    }

    public Task<JsonObject> PostAsync(string path, JsonNode body, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Post, path, body, ct);

    public Task<JsonObject> PatchAsync(string path, JsonNode body, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Patch, path, body, ct);

    /// <summary>Reads every page of a list.</summary>
    public async Task<List<JsonObject>> GetAllAsync(string path, CancellationToken ct = default)
    {
        var items = new List<JsonObject>();
        string? next = path;
        while (next is not null)
        {
            var page = await GetAsync(next, ct);
            if (page["value"] is JsonArray values)
            {
                items.AddRange(values.Select(v => v!.AsObject()));
            }

            next = page["@odata.nextLink"]?.GetValue<string>();
        }

        return items;
    }

    public async Task<JsonObject> SendJsonAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var response = await SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(method, Url(path));
                if (body is not null)
                {
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                }

                return request;
            },
            authenticated: true,
            ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>Sends a request built fresh for each attempt. Throws <see cref="CloudException"/> unless Graph answers with success.</summary>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, bool authenticated, CancellationToken ct)
    {
        var retriedWithFreshToken = false;
        for (var attempt = 0; ; attempt++)
        {
            using var request = build();
            if (authenticated)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(ct));
            }

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException e)
            {
                throw new CloudException("Could not reach Microsoft Graph: " + e.Message, inner: e);
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                throw new CloudException("Microsoft Graph did not answer in time.", inner: e);
            }

            if (response.IsSuccessStatusCode) return response;

            var status = (int)response.StatusCode;
            if (status is 429 or 503 && attempt < MaxThrottleRetries)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * (attempt + 1));
                response.Dispose();
                await Task.Delay(wait < MaxThrottleWait ? wait : MaxThrottleWait, time, ct);
                continue;
            }

            // Refused, or the token was not accepted: the token may be stale, for example one issued
            // moments after consent, before Entra had caught up. Ask for a fresh one, once.
            if (status is 401 or 403 && authenticated && forgetToken is not null && !retriedWithFreshToken)
            {
                retriedWithFreshToken = true;
                response.Dispose();
                forgetToken();
                await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
                continue;
            }

            var text = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw ToException(status, text);
        }
    }

    public string Url(string path) =>
        path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : BaseUrl + "/" + path.TrimStart('/');

    private static CloudException ToException(int status, string text)
    {
        string? code = null;
        string? message = null;
        try
        {
            var error = JsonNode.Parse(text)?["error"];
            code = error?["code"]?.GetValue<string>();
            message = error?["message"]?.GetValue<string>();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
        }

        return new CloudException(
            string.IsNullOrWhiteSpace(message) ? $"Microsoft Graph answered {status}." : $"{message} ({status}{(code is null ? "" : " " + code)})",
            status,
            code);
    }
}
