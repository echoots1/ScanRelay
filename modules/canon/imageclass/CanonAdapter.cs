using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ScanRelay.ModuleSdk;

namespace ScanRelay.Modules.CanonImageClass;

/// <summary>
/// Keeps a Canon imageCLASS address book in step with the list of people.
///
/// The printer only takes its address book whole, so each change is: export it, change
/// ScanRelay's own entries, load everything back. An entry is ours when it is a shared
/// folder whose path ends in a person's id. Every other entry goes back exactly as it came.
///
/// The printer stores folder passwords in a form only it can read. The module never sees
/// the share password in a usable form here; it copies the stored password of one entry
/// that IT made by hand, the template, onto the entries it creates.
/// </summary>
public sealed partial class CanonAdapter(ModuleContext context) : IDeviceAdapter
{
    public string Id => "canon-imageclass";

    /// <summary>Lets tests replace the connection to the printer.</summary>
    public Func<Device, CanonRemoteUi>? UiFactory { get; init; }

    [GeneratedRegex(@"(?:^|[\\/])(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})[\\/]?$")]
    private static partial Regex OurPath();

    public async Task<ProbeResult> ProbeAsync(Device device, CancellationToken cancellationToken = default)
    {
        try
        {
            using var ui = Ui(device);
            var model = await ui.ReadModelAsync(cancellationToken);
            if (model is null) return new ProbeResult(false, null, null, "Something answered at that address, but it is not an imageCLASS Remote UI.");

            // Also prove the sign-in, since every push needs it.
            if (!string.IsNullOrEmpty(device.AdminUser))
            {
                await ui.SignInAsync(device.AdminUser, device.AdminPassword ?? "", cancellationToken);
                await ui.SignOutAsync(cancellationToken);
            }

            return new ProbeResult(true, model, null, null);
        }
        catch (Exception e) when (IsExpected(e))
        {
            return new ProbeResult(false, null, null, Explain(e));
        }
    }

    public async Task<IReadOnlyList<Entry>?> ReadManagedAsync(Device device, CancellationToken cancellationToken = default) =>
        await WithSessionAsync(device, async ui =>
        {
            var book = DcmFile.Read(await ui.ExportAddressBookAsync(NewFilePassword(), Pin(device), cancellationToken)).AddressBook();
            return (IReadOnlyList<Entry>)[.. book.Entries.Select(e => (Entry: e, Key: KeyOf(e))).Where(x => x.Key is not null)
                .Select(x => new Entry(x.Key!, x.Entry.Name, new ScanDestination(ScanProtocol.Smb, x.Entry.Host, x.Entry.Path, x.Entry.UserName, "")))];
        }, cancellationToken);

    public async Task<ApplyResult> ApplyAsync(Device device, IReadOnlyList<Entry> wanted, CancellationToken cancellationToken = default)
    {
        try
        {
            return await WithSessionAsync(device, async ui =>
            {
                var filePassword = NewFilePassword();
                var file = DcmFile.Read(await ui.ExportAddressBookAsync(filePassword, Pin(device), cancellationToken));
                var book = file.AddressBook();
                var templateName = device.Options.GetValueOrDefault("template_name") is { Length: > 0 } n ? n : "ScanRelay";
                var template = book.Entries.FirstOrDefault(e => e.IsFolder && e.Name.Equals(Cut(templateName), StringComparison.OrdinalIgnoreCase));
                if (template is null || template.EncryptedPassword.Length == 0)
                {
                    return new ApplyResult(false, 0, 0, 0,
                        $"The printer has no shared folder entry called \"{templateName}\" with a password. Make one on the printer by hand, pointing at the ScanRelay share, "
                        + "with the printer account and its password from Settings, Printer account. The module copies that entry's stored password onto the entries it creates.");
                }

                var ours = book.Entries.Where(e => KeyOf(e) is not null).ToList();
                var wantedByKey = new Dictionary<string, CanonEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var w in wanted)
                {
                    wantedByKey[w.Key] = CanonEntry.Folder(0, Cut(w.DisplayName.Trim()), w.Destination.Host, w.Destination.Path, template.UserName, template.EncryptedPassword);
                }

                int added = 0, updated = 0, removed = 0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var existing in ours)
                {
                    var key = KeyOf(existing)!;
                    if (!wantedByKey.TryGetValue(key, out var target) || !seen.Add(key)) removed++;
                    else if (!Same(existing, target)) updated++;
                }

                added = wantedByKey.Keys.Count(k => !seen.Contains(k));
                if (added + updated + removed == 0) return new ApplyResult(true, 0, 0, 0, null);

                // Everything that is not ours first, in the order it was. Then ours, by name.
                book.Entries.RemoveAll(e => KeyOf(e) is not null);
                book.Entries.AddRange(wantedByKey.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase));
                book.Renumber();
                file.SetAddressBook(book);
                await ui.ImportAddressBookAsync(file.Write(), filePassword, Pin(device), cancellationToken);

                // The printer's own "finished" says little. Read the book back and see for ourselves.
                var after = DcmFile.Read(await ui.ExportAddressBookAsync(filePassword, Pin(device), cancellationToken)).AddressBook();
                var kept = after.Entries.Select(KeyOf).Where(k => k is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!kept.SetEquals(wantedByKey.Keys))
                {
                    var missing = wantedByKey.Keys.Count(k => !kept.Contains(k));
                    return new ApplyResult(false, 0, 0, 0,
                        $"The printer accepted the address book but did not keep it as sent: {missing} of {wantedByKey.Count} entries are missing afterwards. Its address book may be full.");
                }

                var others = after.Entries.Count(e => KeyOf(e) is null);
                context.Log($"Loaded the address book onto {device.Address}: {added} added, {updated} updated, {removed} removed. {others} other entries sent back as they were.");
                return new ApplyResult(true, added, updated, removed, null);
            }, cancellationToken);
        }
        catch (Exception e) when (IsExpected(e))
        {
            return new ApplyResult(false, 0, 0, 0, Explain(e));
        }
    }

    public Task<ImportFile> ExportAsync(Device device, IReadOnlyList<Entry> wanted, CancellationToken cancellationToken = default)
    {
        // A plain list for typing in at the Remote UI. The password is left out; it is shown under Settings, Printer account.
        var csv = new StringBuilder("Name,Host name,Folder path,User name\r\n");
        foreach (var e in wanted)
        {
            csv.Append(Quote(Cut(e.DisplayName))).Append(',').Append(Quote(e.Destination.Host)).Append(',')
                .Append(Quote(e.Destination.Path)).Append(',').Append(Quote(e.Destination.UserName)).Append("\r\n");
        }

        return Task.FromResult(new ImportFile("canon-address-book.csv", "text/csv", Encoding.UTF8.GetBytes(csv.ToString())));
    }

    /// <summary>The person an entry belongs to, or null when the entry is not one of ours.</summary>
    public static string? KeyOf(CanonEntry entry) =>
        entry.IsFolder && OurPath().Match(entry.Path) is { Success: true } m ? m.Groups["id"].Value.ToLowerInvariant() : null;

    public static string Cut(string name) => name.Length > CanonBook.MaxNameLength ? name[..CanonBook.MaxNameLength].TrimEnd() : name;

    private static bool Same(CanonEntry a, CanonEntry b) =>
        a.Name == b.Name && a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) && a.Path.Equals(b.Path, StringComparison.OrdinalIgnoreCase)
        && a.UserName == b.UserName && a.EncryptedPassword == b.EncryptedPassword;

    private async Task<T> WithSessionAsync<T>(Device device, Func<CanonRemoteUi, Task<T>> work, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(device.AdminUser) || string.IsNullOrEmpty(device.AdminPassword))
        {
            throw new CanonException("Enter the printer's System Manager ID and PIN in this printer's settings. The module signs in with them.");
        }

        using var ui = Ui(device);
        await ui.SignInAsync(device.AdminUser, device.AdminPassword, ct);
        try
        {
            return await work(ui);
        }
        finally
        {
            await ui.SignOutAsync(ct);
        }
    }

    private CanonRemoteUi Ui(Device device) => UiFactory?.Invoke(device)
        ?? new CanonRemoteUi(device.Address, !string.Equals(device.Options.GetValueOrDefault("secure", "true"), "false", StringComparison.OrdinalIgnoreCase));

    private static string? Pin(Device device) => device.Options.GetValueOrDefault("address_book_pin") is { Length: > 0 } pin ? pin : null;

    /// <summary>A fresh password for each export. The file never leaves this process, and is useless once the load is done.</summary>
    private static string NewFilePassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    private static bool IsExpected(Exception e) => e is CanonException or FormatException or HttpRequestException or TaskCanceledException or IOException;

    private static string Explain(Exception e) => e switch
    {
        TaskCanceledException => "The printer did not answer in time.",
        FormatException => "The printer's export was not in the form this module expects. " + e.Message,
        _ => e.Message,
    };

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
