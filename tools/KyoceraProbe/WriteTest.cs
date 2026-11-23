using ScanRelay.Modules.KyoceraEcosys;

namespace ScanRelay.KyoceraProbe;

/// <summary>
/// The first write to a real printer, kept as small as it can be: add one test contact,
/// change it, delete it, and prove every other contact is exactly as it was.
/// </summary>
public static class WriteTest
{
    public const string TestName = "ScanRelay write test";

    public static async Task<bool> RunAsync(HttpClient http, string baseUrl, int number, TextWriter report, CancellationToken ct = default)
    {
        void Say(string line)
        {
            report.WriteLine(line);
            Console.WriteLine(line);
        }

        var client = new KyoceraClient(http, baseUrl)
        {
            Trace = (request, answer) =>
            {
                // Only the writes are shown in full. Reads would repeat the whole address book.
                if (request.Contains("personal_address_list") || request.Contains("_enumeration")) return;
                report.WriteLine("  sent:     " + Probe.Redact(request));
                report.WriteLine("  answered: " + Probe.Redact(answer));
            },
        };

        Say($"Kyocera write test against {baseUrl}, using contact number {number}.");
        try
        {
            var before = await client.ReadAllAsync(ct);
            Say($"The printer has {before.Count} contacts. Numbers in use: {string.Join(", ", before.Select(c => c.Id))}.");
            if (before.Any(c => c.Id == number))
            {
                Say($"Number {number} is already in use, so nothing was done. Choose a free number with --number.");
                return false;
            }

            var test = new Contact(number, TestName, "scanrelay-test", "Scans$\\00000000-0000-4000-8000-00005ca17e57", 445, "scanrelay-test\\tester", "Throwaway1", false);

            Say("1. Add the test contact.");
            await client.AddAsync(test, ct);
            var afterAdd = await client.ReadAllAsync(ct);
            var added = afterAdd.FirstOrDefault(c => c.Id == number);
            Say(added is null ? "   NOT FOUND after adding." : $"   Stored as: name \"{added.Name}\", server \"{added.SmbServer}\", path \"{added.SmbPath}\", port {added.SmbPort}, login \"{added.SmbLoginName}\", password given back: {(added.SmbPassword.Length > 0 ? "yes" : "no")}.");
            Say($"   The module recognises it as its own: {(added is not null && KyoceraAdapter.KeyOf(added) is not null ? "yes" : "NO")}.");

            Say("2. Change its name.");
            await client.UpdateAsync(test with { Name = TestName + " 2" }, ct);
            var changed = (await client.ReadAllAsync(ct)).FirstOrDefault(c => c.Id == number);
            Say($"   Name is now \"{changed?.Name}\". Folder details kept: {(changed is not null && changed.SmbPath == added?.SmbPath && changed.SmbServer == added?.SmbServer ? "yes" : "NO")}.");

            Say("3. Delete it.");
            await client.DeleteAsync(number, ct);
            var after = await client.ReadAllAsync(ct);
            var gone = after.All(c => c.Id != number);
            Say($"   Gone: {(gone ? "yes" : "NO")}.");

            var untouched = before.SequenceEqual(after);
            Say($"4. Every other contact is exactly as it was: {(untouched ? "yes" : "NO")}.");
            if (!untouched)
            {
                foreach (var id in before.Select(c => c.Id).Union(after.Select(c => c.Id)).Order())
                {
                    var (was, now) = (before.FirstOrDefault(c => c.Id == id), after.FirstOrDefault(c => c.Id == id));
                    if (was != now) Say($"   Contact {id} differs: {(was is null ? "it is new" : now is null ? "it is missing" : "its details changed")}.");
                }
            }

            var ok = added is not null && changed?.Name == TestName + " 2" && gone && untouched;
            Say(ok ? "RESULT: the add, change and delete all worked, and nothing else was touched." : "RESULT: something did not go as expected. See above.");
            return ok;
        }
        catch (Exception e) when (e is KyoceraException or HttpRequestException or TaskCanceledException)
        {
            Say("STOPPED: " + e.Message);
            Say($"If the test contact was left behind, delete contact number {number} (\"{TestName}\") at the printer or in Net Viewer.");
            return false;
        }
    }
}
