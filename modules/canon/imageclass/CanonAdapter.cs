using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ScanRelay.ModuleSdk;

namespace ScanRelay.Modules.CanonImageClass;

/// <summary>
/// Keeps a Canon imageCLASS address book in step with the list of people.
///
/// The printer only takes its address book whole, and refuses a load for some minutes after
/// an export. So the book is read once, from the Printers page, and kept. Each push is that
/// copy with ScanRelay's own entries replaced, loaded back. An entry is ours when it is a
/// shared folder whose path ends in a person's id. Every other entry goes back as it was read.
///
/// The printer stores folder passwords in a form only it can read. The module never sees
/// the share password in a usable form here; it copies the stored password of one entry
/// that IT made by hand, the template, onto the entries it creates.
/// </summary>
public sealed partial class CanonAdapter(ModuleContext context) : IDeviceAdapter, IPreparesDevice
{
    /// <summary>The names the read address book and its file password are stored under with the printer.</summary>
    public const string CapturedBook = "captured_book";
    public const string CapturedPassword = "captured_file_password";

    /// <summary>What each printer was last loaded with, so a push can count its changes without reading the printer.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, CanonEntry>> LastLoaded = new();

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

    public string PrepareLabel => "Read the address book from the printer";

    public string PrepareHint =>
        "First make one shared folder entry on the printer by hand, named exactly \"ScanRelay\", pointing at the ScanRelay share with the printer account and its password "
        + "from Settings, Printer account. Then enter the System Manager ID and PIN below and press the button. ScanRelay keeps a copy of the address book, with the password "
        + "in the form only the printer can read, and builds every later push from that copy: the entries in it that are not ScanRelay's own go back as they were, and an entry "
        + "added at the printer afterwards is lost at the next push unless you read again. The printer will not take a load for a few minutes after a read.";

    /// <summary>
    /// Reads the whole address book once. The printer refuses a load for some minutes after an export,
    /// so a push cannot begin with one; it is built from this copy instead.
    /// </summary>
    public async Task<PrepareResult> PrepareAsync(Device device, CancellationToken cancellationToken = default)
    {
        var none = new Dictionary<string, string>();
        try
        {
            return await WithSessionAsync(device, async ui =>
            {
                var filePassword = NewFilePassword();
                var bytes = await ui.ExportAddressBookAsync(filePassword, Pin(device), cancellationToken);
                var book = DcmFile.Read(bytes).AddressBook();
                if (Template(book, device) is not { } template) return new PrepareResult(false, none, NoTemplate(device));
                var others = book.Entries.Count(e => KeyOf(e) is null);
                return new PrepareResult(true, new Dictionary<string, string>
                {
                    [CapturedBook] = Convert.ToBase64String(bytes),
                    [CapturedPassword] = filePassword,
                }, $"Read {book.Entries.Count} entries from the printer. Found \"{template.Name}\" with a stored password for {template.UserName}. "
                    + $"{others} entries that are not ScanRelay's will be sent back as they are with every push.");
            }, cancellationToken);
        }
        catch (Exception e) when (IsExpected(e))
        {
            return new PrepareResult(false, none, Explain(e));
        }
    }

    public async Task<ApplyResult> ApplyAsync(Device device, IReadOnlyList<Entry> wanted, CancellationToken cancellationToken = default)
    {
        try
        {
            if (device.Options.GetValueOrDefault(CapturedBook) is not { Length: > 0 } captured || device.Options.GetValueOrDefault(CapturedPassword) is not { Length: > 0 } filePassword)
            {
                return new ApplyResult(false, 0, 0, 0,
                    "The printer's address book has not been read yet. Open this printer under Printers, press \"Read the address book from the printer\" and save.");
            }

            var file = DcmFile.Read(Convert.FromBase64String(captured));
            var book = file.AddressBook();
            if (Template(book, device) is not { } template) return new ApplyResult(false, 0, 0, 0, NoTemplate(device));

            var wantedByKey = new Dictionary<string, CanonEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in wanted)
            {
                wantedByKey[w.Key] = CanonEntry.Folder(0, Cut(w.DisplayName.Trim()), w.Destination.Host, w.Destination.Path, template.UserName, template.EncryptedPassword);
            }

            // What the printer holds now: what this process last loaded, or else what was read from it.
            var state = device.Address + "|" + captured.GetHashCode();
            var held = LastLoaded.TryGetValue(state, out var last)
                ? last
                : book.Entries.Where(e => KeyOf(e) is not null).GroupBy(e => KeyOf(e)!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var added = wantedByKey.Keys.Count(k => !held.ContainsKey(k));
            var removed = held.Keys.Count(k => !wantedByKey.ContainsKey(k));
            var updated = wantedByKey.Count(w => held.TryGetValue(w.Key, out var now) && !Same(now, w.Value));
            if (added + updated + removed == 0) return new ApplyResult(true, 0, 0, 0, null);

            // Everything that is not ours first, in the order it was. Then ours, by name.
            book.Entries.RemoveAll(e => KeyOf(e) is not null);
            var others = book.Entries.Count;
            book.Entries.AddRange(wantedByKey.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase));
            book.Renumber();
            file.SetAddressBook(book);
            var bytes = file.Write();

            return await WithSessionAsync(device, async ui =>
            {
                await ui.ImportAddressBookAsync(bytes, filePassword, Pin(device), cancellationToken);
                LastLoaded[state] = wantedByKey;
                context.Log($"Loaded the address book onto {device.Address}: {added} added, {updated} updated, {removed} removed. {others} other entries sent back as they were read.");
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

    private static CanonEntry? Template(CanonBook book, Device device) =>
        book.Entries.FirstOrDefault(e => e.IsFolder && e.EncryptedPassword.Length > 0 && e.Name.Equals(Cut(TemplateName(device)), StringComparison.OrdinalIgnoreCase));

    private static string TemplateName(Device device) => device.Options.GetValueOrDefault("template_name") is { Length: > 0 } n ? n : "ScanRelay";

    private static string NoTemplate(Device device) =>
        $"The printer has no shared folder entry called \"{TemplateName(device)}\" with a password. Make one on the printer by hand, pointing at the ScanRelay share, "
        + "with the printer account and its password from Settings, Printer account. Then read the address book from the printer again.";

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

    private static bool IsExpected(Exception e) => e is CanonException or FormatException or InvalidDataException or HttpRequestException or TaskCanceledException or IOException;

    private static string Explain(Exception e) => e switch
    {
        TaskCanceledException => "The printer did not answer in time.",
        FormatException => "The printer's export was not in the form this module expects. " + e.Message,
        _ => e.Message,
    };

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
