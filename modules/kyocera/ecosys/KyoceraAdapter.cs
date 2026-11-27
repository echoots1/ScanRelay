using System.Text;
using System.Text.RegularExpressions;
using ScanRelay.ModuleSdk;

namespace ScanRelay.Modules.KyoceraEcosys;

/// <summary>
/// Keeps a Kyocera ECOSYS address book in step with the list of people.
///
/// What this module owns on the printer: contacts whose folder path ends in a person's id
/// and that carry no fax, FTP or internet fax destination. Nothing else is ever changed or
/// removed, whatever number it has. New contacts are given numbers from the reserved range only.
/// </summary>
public sealed partial class KyoceraAdapter(ModuleContext context) : IDeviceAdapter
{
    /// <summary>Kyocera contact names are limited. Longer names are cut.</summary>
    public const int MaxNameLength = 32;

    public string Id => "kyocera-ecosys";

    /// <summary>Lets tests replace the connection to the printer.</summary>
    public Func<Device, KyoceraClient>? ClientFactory { get; init; }

    public async Task<ProbeResult> ProbeAsync(Device device, CancellationToken cancellationToken = default)
    {
        try
        {
            var (model, firmware) = await Client(device).IdentifyAsync(cancellationToken);
            return new ProbeResult(true, model, firmware, null);
        }
        catch (Exception e) when (IsExpected(e))
        {
            return new ProbeResult(false, null, null, Explain(e));
        }
    }

    public async Task<IReadOnlyList<Entry>?> ReadManagedAsync(Device device, CancellationToken cancellationToken = default)
    {
        var all = await Client(device).ReadAllAsync(cancellationToken);
        return [.. all.Select(c => (Contact: c, Key: KeyOf(c))).Where(x => x.Key is not null)
            .Select(x => new Entry(x.Key!, x.Contact.Name, new ScanDestination(ScanProtocol.Smb, x.Contact.SmbServer, x.Contact.SmbPath, x.Contact.SmbLoginName, ""), x.Contact.Email.Length > 0 ? x.Contact.Email : null))];
    }

    public async Task<ApplyResult> ApplyAsync(Device device, IReadOnlyList<Entry> wanted, CancellationToken cancellationToken = default)
    {
        int added = 0, updated = 0, removed = 0;
        try
        {
            var (first, last) = Range(device);
            var client = Client(device);
            var all = await client.ReadAllAsync(cancellationToken);
            var ours = all.Select(c => (Contact: c, Key: KeyOf(c))).Where(x => x.Key is not null).ToList();
            var wantedByKey = wanted.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

            // Remove our contacts that are no longer wanted, and extra copies of one person.
            var kept = new Dictionary<string, Contact>(StringComparer.OrdinalIgnoreCase);
            foreach (var (contact, key) in ours)
            {
                if (wantedByKey.ContainsKey(key!) && kept.TryAdd(key!, contact)) continue;
                await client.DeleteAsync(contact.Id, cancellationToken);
                removed++;
            }

            var taken = all.Select(c => c.Id).ToHashSet();
            var free = new Queue<int>(Enumerable.Range(first, last - first + 1).Where(n => !taken.Contains(n) || ours.Any(o => o.Contact.Id == n && !kept.ContainsValue(o.Contact))));
            foreach (var entry in wanted)
            {
                var target = ToContact(entry, 0);
                if (kept.TryGetValue(entry.Key, out var existing))
                {
                    // A printer that does not give passwords back cannot be compared on them, so the password is left out of the comparison then.
                    var same = existing.Name == target.Name && existing.SmbServer.Equals(target.SmbServer, StringComparison.OrdinalIgnoreCase)
                        && SamePath(existing.SmbPath, target.SmbPath) && existing.SmbPort == target.SmbPort && existing.SmbLoginName == target.SmbLoginName
                        && (existing.SmbPassword.Length == 0 || existing.SmbPassword == target.SmbPassword)
                        && existing.Email.Equals(target.Email, StringComparison.OrdinalIgnoreCase);
                    if (same) continue;
                    await client.UpdateAsync(target with { Id = existing.Id }, cancellationToken, clearEmail: target.Email.Length == 0 && existing.Email.Length > 0);
                    updated++;
                }
                else
                {
                    if (free.Count == 0)
                    {
                        return new ApplyResult(false, added, updated, removed,
                            $"There are no free contact numbers left between {first} and {last} on the printer. Widen the range in this printer's settings, or give it a smaller group.");
                    }

                    await client.AddAsync(target with { Id = free.Dequeue() }, cancellationToken);
                    added++;
                }
            }

            context.Log($"Applied {wanted.Count} contacts to {device.Address}: {added} added, {updated} updated, {removed} removed. {all.Count - ours.Count} other contacts left alone.");
            return new ApplyResult(true, added, updated, removed, null);
        }
        catch (Exception e) when (IsExpected(e))
        {
            var progress = added + updated + removed > 0 ? $" Before that, {added} were added, {updated} updated and {removed} removed; the next push finishes the job." : "";
            return new ApplyResult(false, added, updated, removed, Explain(e) + progress);
        }
    }

    public Task<ImportFile> ExportAsync(Device device, IReadOnlyList<Entry> wanted, CancellationToken cancellationToken = default)
    {
        // A plain list for typing in at the panel or in Command Center RX. The password is left out
        // of a file that will sit in someone's downloads; it is shown under Settings, Printer account.
        var (first, _) = Range(device);
        var csv = new StringBuilder("Number,Name,Host name,Path,Port,Login user name,E-mail address\r\n");
        var number = first;
        foreach (var e in wanted)
        {
            var c = ToContact(e, number++);
            csv.Append(c.Id).Append(',').Append(Quote(c.Name)).Append(',').Append(Quote(c.SmbServer)).Append(',')
                .Append(Quote(c.SmbPath)).Append(',').Append(c.SmbPort).Append(',').Append(Quote(c.SmbLoginName)).Append(',').Append(Quote(c.Email)).Append("\r\n");
        }

        return Task.FromResult(new ImportFile("kyocera-address-book.csv", "text/csv", Encoding.UTF8.GetBytes(csv.ToString())));
    }

    /// <summary>The person a contact belongs to, or null when the contact is not one of ours.</summary>
    public static string? KeyOf(Contact contact)
    {
        // Ours point at a folder named with a person's id, and carry no fax or FTP destination.
        // An email address is part of ours: it is the person's own, so scan to email works from the same contact.
        if (contact.HasOtherDestinations) return null;
        var match = OurPath().Match(contact.SmbPath);
        return match.Success ? match.Groups["id"].Value.ToLowerInvariant() : null;
    }

    public static Contact ToContact(Entry entry, int id)
    {
        var name = entry.DisplayName.Trim();
        return new Contact(
            id,
            name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name,
            entry.Destination.Host,
            entry.Destination.Path,
            445,
            entry.Destination.UserName,
            entry.Destination.Password,
            HasOtherDestinations: false,
            Email: entry.Email?.Trim() ?? "");
    }

    private static bool SamePath(string a, string b) =>
        a.Replace('/', '\\').Trim('\\').Equals(b.Replace('/', '\\').Trim('\\'), StringComparison.OrdinalIgnoreCase);

    private static (int First, int Last) Range(Device device)
    {
        var first = int.TryParse(device.Options.GetValueOrDefault("first_number"), out var f) ? f : 100;
        var last = int.TryParse(device.Options.GetValueOrDefault("last_number"), out var l) ? l : 199;
        if (first < 1 || last < first) throw new KyoceraException("The contact number range in this printer's settings is not valid. The first number must be 1 or more and not above the last.");
        return (first, last);
    }

    private KyoceraClient Client(Device device)
    {
        if (ClientFactory is not null) return ClientFactory(device);
        var secure = !string.Equals(device.Options.GetValueOrDefault("secure", "true"), "false", StringComparison.OrdinalIgnoreCase);
        var port = device.Options.GetValueOrDefault("port", secure ? "9091" : "9090");
        return new KyoceraClient(secure ? PrinterHttp.Value : context.Http, $"{(secure ? "https" : "http")}://{device.Address}:{port}");
    }

    /// <summary>
    /// Printers present a certificate they made themselves, so it cannot be checked against anything.
    /// The connection is still encrypted. What travels over it is the printer account's password,
    /// which the design already assumes can leak.
    /// </summary>
    private static readonly Lazy<HttpClient> PrinterHttp = new(() => new HttpClient(
        new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator })
    {
        Timeout = TimeSpan.FromSeconds(60),
    });

    private static bool IsExpected(Exception e) => e is KyoceraException or HttpRequestException or TaskCanceledException or IOException;

    private static string Explain(Exception e) => e switch
    {
        KyoceraException k => k.Message,
        TaskCanceledException => "The printer did not answer in time.",
        HttpRequestException h => "Could not reach the printer's network service: " + (h.InnerException?.Message ?? h.Message)
            + " Check the address and port, that the service is on, and that this server is allowed through to ports 9090 and 9091.",
        _ => e.Message,
    };

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    [GeneratedRegex(@"(?:^|[\\/])(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})[\\/]?$")]
    private static partial Regex OurPath();
}
