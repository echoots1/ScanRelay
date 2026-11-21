using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScanRelay.ModuleSdk;

namespace ScanRelay.Modules.Sample;

/// <summary>
/// Drives the mock printer. It shows the three rules every adapter follows:
/// only touch the reserved scope, make Apply safe to run twice, and report failures
/// as a result with a reason a person can act on.
/// </summary>
public sealed class SampleAdapter(ModuleContext context) : IDeviceAdapter
{
    private const int RangeLow = 900;
    private const int RangeHigh = 999;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Id => "scanrelay-sample";

    public async Task<ProbeResult> ProbeAsync(Device device, CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await SendAsync<Info>(device, HttpMethod.Get, "api/info", null, cancellationToken);
            return new ProbeResult(true, info?.Model, info?.Firmware, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new ProbeResult(false, null, null, Explain(e));
        }
    }

    public async Task<IReadOnlyList<Entry>?> ReadManagedAsync(Device device, CancellationToken cancellationToken = default)
    {
        var slots = await ReadSlotsAsync(device, cancellationToken);
        return [.. slots.Select(s => new Entry(s.Key ?? "", s.Name, new ScanDestination(ParseProtocol(s.Protocol), s.Host, s.Path, s.User, "")))];
    }

    public async Task<ApplyResult> ApplyAsync(Device device, IReadOnlyList<Entry> wanted, CancellationToken cancellationToken = default)
    {
        try
        {
            var held = await ReadSlotsAsync(device, cancellationToken);
            var wantedByKey = wanted.ToDictionary(e => e.Key);
            int added = 0, updated = 0, removed = 0;

            // Remove what is no longer wanted, including anything in our range we did not put there.
            foreach (var slot in held.Where(s => s.Key is null || !wantedByKey.ContainsKey(s.Key)))
            {
                await SendAsync<object>(device, HttpMethod.Delete, $"api/addressbook/{slot.Slot}", null, cancellationToken);
                removed++;
            }

            var kept = held.Where(s => s.Key is not null && wantedByKey.ContainsKey(s.Key)).ToDictionary(s => s.Key!);
            var free = new Queue<int>(Enumerable.Range(RangeLow, RangeHigh - RangeLow + 1).Except(kept.Values.Select(s => s.Slot)));
            foreach (var entry in wanted)
            {
                var target = ToSlot(entry);
                if (kept.TryGetValue(entry.Key, out var existing))
                {
                    // The printer never gives the password back, so it is always rewritten when anything else changed.
                    if (existing with { Slot = 0 } == target with { Slot = 0, Password = null }) continue;
                    await SendAsync<object>(device, HttpMethod.Put, $"api/addressbook/{existing.Slot}", target with { Slot = existing.Slot }, cancellationToken);
                    updated++;
                }
                else
                {
                    if (free.Count == 0) return new ApplyResult(false, added, updated, removed, "The reserved number range on the printer is full.");
                    var number = free.Dequeue();
                    await SendAsync<object>(device, HttpMethod.Put, $"api/addressbook/{number}", target with { Slot = number }, cancellationToken);
                    added++;
                }
            }

            context.Log($"Applied {wanted.Count} entries to {device.Address}: {added} added, {updated} updated, {removed} removed.");
            return new ApplyResult(true, added, updated, removed, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new ApplyResult(false, 0, 0, 0, Explain(e));
        }
    }

    public Task<ImportFile> ExportAsync(Device device, IReadOnlyList<Entry> wanted, CancellationToken cancellationToken = default)
    {
        var csv = new StringBuilder("slot,name,protocol,host,path,user\r\n");
        var number = RangeLow;
        foreach (var e in wanted)
        {
            csv.Append(number++).Append(',').Append(Quote(e.DisplayName)).Append(',').Append(e.Destination.Protocol.ToString().ToLowerInvariant()).Append(',')
                .Append(Quote(e.Destination.Host)).Append(',').Append(Quote(e.Destination.Path)).Append(',').Append(Quote(e.Destination.UserName)).Append("\r\n");
        }

        // The password is left out of a file that will sit in someone's downloads.
        return Task.FromResult(new ImportFile("mock-printer-address-book.csv", "text/csv", Encoding.UTF8.GetBytes(csv.ToString())));
    }

    private async Task<List<SlotRecord>> ReadSlotsAsync(Device device, CancellationToken ct) =>
        [.. (await SendAsync<List<SlotRecord>>(device, HttpMethod.Get, "api/addressbook", null, ct) ?? []).Where(s => s.Slot is >= RangeLow and <= RangeHigh)];

    private async Task<T?> SendAsync<T>(Device device, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var port = device.Options.GetValueOrDefault("port", "80");
        using var request = new HttpRequestMessage(method, $"http://{device.Address}:{port}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{device.AdminUser}:{device.AdminPassword}")));
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await context.Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "The printer refused the admin sign-in. Check the user name and password."
                    : $"The printer answered {(int)response.StatusCode}.");
        }

        return method == HttpMethod.Get ? await response.Content.ReadFromJsonAsync<T>(Json, ct) : default;
    }

    private static SlotRecord ToSlot(Entry e) => new(
        0, e.DisplayName, e.Key, e.Destination.Protocol.ToString().ToLowerInvariant(), e.Destination.Host, e.Destination.Path, e.Destination.UserName, e.Destination.Password);

    private static ScanProtocol ParseProtocol(string value) => Enum.TryParse<ScanProtocol>(value, true, out var p) ? p : ScanProtocol.Smb;

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string Explain(Exception e) => e is TaskCanceledException ? "The printer did not answer in time." : e.Message;

    private sealed record Info(string Model, string Firmware);

    private sealed record SlotRecord(
        int Slot, string Name, string? Key, string Protocol, string Host, string Path, string User,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Password);
}
