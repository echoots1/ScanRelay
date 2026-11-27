using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ScanRelay.ModuleSdk;

namespace ScanRelay.Modules.BrotherAds;

/// <summary>
/// Keeps a Brother ADS scanner's "Scan to Network" profiles in step with the list of people: one
/// profile per person, pointing at their folder on the share.
///
/// The scanner has 25 profile slots in all. A profile is ours when it is a Network profile whose
/// folder path ends in a person's id. Every other profile, of any kind, is left exactly as it is.
/// New people go into slots with no name; such a slot of another kind is made a Network one first.
/// </summary>
public sealed partial class BrotherAdapter(ModuleContext context) : IDeviceAdapter
{
    /// <summary>The scanner keeps this many characters of a profile's name.</summary>
    public const int MaxNameLength = 14;

    public string Id => "brother-ads";

    /// <summary>Lets tests replace the connection to the scanner.</summary>
    public Func<Device, BrotherWebUi>? UiFactory { get; init; }

    [GeneratedRegex(@"[\\/](?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})[\\/]?$")]
    private static partial Regex OurPath();

    private sealed record Ours(int Index, string Key, string Name, string FolderPath, string User, string Password, XElement Profile);

    public async Task<ProbeResult> ProbeAsync(Device device, CancellationToken cancellationToken = default)
    {
        try
        {
            using var ui = Ui(device);
            var model = await ui.ReadModelAsync(cancellationToken);
            if (model is null) return new ProbeResult(false, null, null, "Something answered at that address, but it is not a Brother scanner's Web Based Management.");

            // Also prove the sign-in, since every push needs it.
            if (!string.IsNullOrEmpty(device.AdminPassword))
            {
                await ui.SignInAsync(device.AdminPassword, cancellationToken);
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
            var (_, ours) = await ReadAsync(ui, cancellationToken);
            return (IReadOnlyList<Entry>)[.. ours.Select(o =>
            {
                var (host, path) = SplitPath(o.FolderPath);
                return new Entry(o.Key, o.Name, new ScanDestination(ScanProtocol.Smb, host, path, o.User, ""));
            })];
        }, cancellationToken);

    public async Task<ApplyResult> ApplyAsync(Device device, IReadOnlyList<Entry> wanted, CancellationToken cancellationToken = default)
    {
        try
        {
            return await WithSessionAsync(device, async ui =>
            {
                var (slots, ours) = await ReadAsync(ui, cancellationToken);
                var wantedByKey = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                foreach (var w in wanted) wantedByKey[w.Key] = w;

                // A person held twice keeps the first profile; the second is cleared like a leaver's.
                var held = new Dictionary<string, Ours>(StringComparer.OrdinalIgnoreCase);
                var toClear = new List<Ours>();
                foreach (var o in ours)
                {
                    if (wantedByKey.ContainsKey(o.Key) && held.TryAdd(o.Key, o)) continue;
                    toClear.Add(o);
                }

                var toUpdate = held.Values.Where(o => !Same(o, wantedByKey[o.Key])).ToList();
                var toAdd = wantedByKey.Values.Where(w => !held.ContainsKey(w.Key)).OrderBy(w => w.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
                if (toClear.Count + toUpdate.Count + toAdd.Count == 0) return new ApplyResult(true, 0, 0, 0, null);

                // Room for the new people: slots being cleared, then slots with no name, a Network one before any other kind.
                var free = toClear.Select(o => o.Index)
                    .Concat(slots.Where(s => s.Name.Length == 0 && ours.All(o => o.Index != s.Index)).OrderBy(s => s.IsNetwork ? 0 : 1).ThenBy(s => s.Index).Select(s => s.Index))
                    .ToList();
                if (toAdd.Count > free.Count)
                {
                    var others = slots.Count(s => s.Name.Length > 0 && ours.All(o => o.Index != s.Index));
                    return new ApplyResult(false, 0, 0, 0,
                        $"This scanner holds {slots.Count} scan profiles. {others} are used for other things, which leaves room for {slots.Count - others} people, and {wantedByKey.Count} are on its list. "
                        + "Give it its own smaller group, or assign people to it one by one. Nothing was changed.");
                }

                foreach (var o in toClear) await ui.SaveNetworkProfileAsync(o.Index, o.Profile, "", "", "", "", cancellationToken);

                var places = toAdd.Select((w, i) => (Entry: w, Index: free[i])).ToList();
                var notNetwork = places.Select(p => p.Index).Where(i => !slots[i].IsNetwork).ToList();
                if (notNetwork.Count > 0) await ui.MakeNetworkAsync(notNetwork, cancellationToken);

                foreach (var o in toUpdate) await SaveAsync(ui, o.Index, o.Profile, wantedByKey[o.Key], cancellationToken);
                foreach (var (entry, index) in places) await SaveAsync(ui, index, await ui.ReadNetworkProfileAsync(index, cancellationToken), entry, cancellationToken);

                // The scanner's "200" says little. Read the profiles back and see for ourselves.
                var (_, after) = await ReadAsync(ui, cancellationToken);
                var kept = after.Select(o => o.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!kept.SetEquals(wantedByKey.Keys))
                {
                    var missing = wantedByKey.Keys.Count(k => !kept.Contains(k));
                    var extra = kept.Count(k => !wantedByKey.ContainsKey(k));
                    return new ApplyResult(false, 0, 0, 0,
                        $"The scanner accepted the profiles but does not hold them as sent: {missing} of {wantedByKey.Count} are missing afterwards{(extra > 0 ? $", and {extra} that should have been cleared are still there" : "")}.");
                }

                context.Log($"Set the scan profiles on {device.Address}: {toAdd.Count} added, {toUpdate.Count} updated, {toClear.Count} removed. Profiles that are not ScanRelay's were not touched.");
                return new ApplyResult(true, toAdd.Count, toUpdate.Count, toClear.Count, null);
            }, cancellationToken);
        }
        catch (Exception e) when (IsExpected(e))
        {
            return new ApplyResult(false, 0, 0, 0, Explain(e));
        }
    }

    public Task<ImportFile> ExportAsync(Device device, IReadOnlyList<Entry> wanted, CancellationToken cancellationToken = default)
    {
        // A plain list for typing in at the scanner's Web Based Management. The password is left out; it is shown under Settings, Printer account.
        var csv = new StringBuilder("Profile name,Network folder path,User name\r\n");
        foreach (var e in wanted)
        {
            csv.Append(Quote(Cut(e.DisplayName))).Append(',').Append(Quote(FullPath(e.Destination))).Append(',').Append(Quote(e.Destination.UserName)).Append("\r\n");
        }

        return Task.FromResult(new ImportFile("brother-scan-profiles.csv", "text/csv", Encoding.UTF8.GetBytes(csv.ToString())));
    }

    /// <summary>The scanner's Network profiles that are ours, with everything it holds for each.</summary>
    private static async Task<(IReadOnlyList<BrotherSlot> Slots, List<Ours> Ours)> ReadAsync(BrotherWebUi ui, CancellationToken ct)
    {
        var slots = await ui.ReadSlotsAsync(ct);
        var ours = new List<Ours>();
        foreach (var slot in slots.Where(s => s.IsNetwork && s.Name.Length > 0))
        {
            var profile = await ui.ReadNetworkProfileAsync(slot.Index, ct);
            var path = profile.Element("FolderPath")?.Value ?? "";
            if (OurPath().Match(path) is not { Success: true } m) continue;
            ours.Add(new Ours(slot.Index, m.Groups["id"].Value.ToLowerInvariant(), profile.Element("ProfileName")?.Value ?? slot.Name, path,
                profile.Element("FolderUser")?.Value ?? "", profile.Element("FolderPass")?.Value ?? "", profile));
        }

        return (slots, ours);
    }

    private static Task SaveAsync(BrotherWebUi ui, int index, XElement current, Entry entry, CancellationToken ct) =>
        ui.SaveNetworkProfileAsync(index, current, Cut(entry.DisplayName), FullPath(entry.Destination), entry.Destination.UserName, entry.Destination.Password, ct);

    /// <summary>
    /// Whether a profile already is what is wanted. The password counts only when the scanner hands back
    /// something that could be it: some firmware returns nothing, or a row of stars, in its place.
    /// </summary>
    private static bool Same(Ours held, Entry wanted) =>
        held.Name == Cut(wanted.DisplayName)
        && held.FolderPath.Equals(FullPath(wanted.Destination), StringComparison.OrdinalIgnoreCase)
        && held.User.Equals(wanted.Destination.UserName, StringComparison.OrdinalIgnoreCase)
        && (held.Password.Length == 0 || held.Password.All(c => c == '*') || held.Password == wanted.Destination.Password);

    public static string Cut(string name)
    {
        name = name.Trim();
        return name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name;
    }

    public static string FullPath(ScanDestination d) => $@"\\{d.Host}\{d.Path.TrimStart('\\')}";

    private static (string Host, string Path) SplitPath(string full)
    {
        var parts = full.TrimStart('\\').Split('\\', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : "");
    }

    private async Task<T> WithSessionAsync<T>(Device device, Func<BrotherWebUi, Task<T>> work, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(device.AdminPassword))
        {
            throw new BrotherException("Enter the scanner's Web Based Management password in this printer's settings, in the password box of the admin sign-in. The user name is not used.");
        }

        var said = new List<string>();
        using var ui = Ui(device, said.Add);
        try
        {
            await ui.SignInAsync(device.AdminPassword, ct);
            try
            {
                return await work(ui);
            }
            finally
            {
                await ui.SignOutAsync(ct);
            }
        }
        catch (Exception e) when (IsExpected(e))
        {
            // What was asked and answered, for working out what went wrong. Passwords, PINs and profile names are left out of it.
            context.Log($"The exchange with {device.Address} that ended in \"{Explain(e)}\":{Environment.NewLine}{string.Join(Environment.NewLine, said)}");
            throw;
        }
    }

    private BrotherWebUi Ui(Device device, Action<string>? trace = null) => UiFactory?.Invoke(device)
        ?? new BrotherWebUi(device.Address, !string.Equals(device.Options.GetValueOrDefault("secure", "true"), "false", StringComparison.OrdinalIgnoreCase)) { Trace = trace };

    private static bool IsExpected(Exception e) => e is BrotherException or HttpRequestException or TaskCanceledException or IOException;

    private static string Explain(Exception e) => e is TaskCanceledException ? "The scanner did not answer in time." : e.Message;

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
