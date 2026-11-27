using ScanRelay.Modules.BrotherAds;

namespace ScanRelay.BrotherProbe;

/// <summary>
/// Reads a Brother ADS scanner's scan profiles the way the module will, and writes down what the
/// scanner answered, so the module can be built on what a real scanner does. It changes nothing.
/// </summary>
public static class BrotherProbeProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            Console.WriteLine("""
                Reads a Brother ADS scanner's scan profiles and saves a report. Changes nothing on the scanner.

                  brother-probe <scanner address>

                Options:  --plain          use http instead of https (the scanner only takes a sign-in over https)
                          --out <folder>   where the report goes (default: this folder)

                The Web Based Management password is asked for and not shown.
                """);
            return args.Length == 0 ? 0 : 1;
        }

        string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var folder = Option("--out") ?? ".";
        Directory.CreateDirectory(folder);
        var reportPath = Path.Combine(folder, "brother-probe-report.txt");
        var password = Environment.GetEnvironmentVariable("BROTHER_PASSWORD") ?? ReadHidden("Web Based Management password: ");

        await using var report = new StreamWriter(reportPath);
        void Say(string line)
        {
            report.WriteLine(line);
            Console.WriteLine(line);
        }

        using var ui = new BrotherWebUi(args[0], secure: !args.Contains("--plain")) { Trace = report.WriteLine };
        var ok = false;
        try
        {
            Say($"Brother probe, {DateTime.UtcNow:u}.");
            Say("Model: " + (await ui.ReadModelAsync(default) ?? "not found"));
            await ui.SignInAsync(password, default);
            Say("Signed in.");
            try
            {
                var slots = await ui.ReadSlotsAsync(default);
                var used = slots.Where(s => s.Name.Length > 0).ToList();
                Say($"Profiles: {slots.Count} slots, {used.Count} in use. By kind: {string.Join(", ", slots.GroupBy(s => s.TypeName).Select(g => $"{g.Count()} {g.Key}"))}.");
                foreach (var slot in used) Say($"  Profile {slot.Index + 1}: {slot.TypeName}, a name of {slot.Name.Length} characters");

                // One profile that is filled in and one that is empty, of the Network kind, show every value the scanner keeps.
                foreach (var slot in new[] { used.FirstOrDefault(s => s.IsNetwork), slots.FirstOrDefault(s => s.IsNetwork && s.Name.Length == 0) }.Where(s => s is not null).Distinct())
                {
                    Say($"Reading profile {slot!.Index + 1} ({(slot.Name.Length > 0 ? "in use" : "empty")}).");
                    var profile = await ui.ReadNetworkProfileAsync(slot.Index, default);
                    Say($"  {profile.Elements().Count()} values. Folder: {profile.Element("FolderPath")?.Value}  as {profile.Element("FolderUser")?.Value}  (password {(profile.Element("FolderPass")?.Value.Length > 0 ? "set" : "not set")})");
                }

                if (!slots.Any(s => s.IsNetwork)) Say("No slot is of the Network kind. Set one profile to Network on the scanner's \"Scan to FTP/SFTP/Network/SharePoint\" page, then run this again.");

                // The page where a slot's kind is chosen. Its script is the scanner's own code and holds no settings.
                foreach (var path in new[] { "/EWS/common/js/netscan.js", "/EWS/scan/netscan.html" })
                {
                    var (status, body) = await ui.GetPageAsync(path, default);
                    report.WriteLine($"----- {path} ({status}, {body.Length} characters) -----");
                    if (status == 200) report.WriteLine(body);
                    report.WriteLine("----- end -----");
                }

                ok = true;
            }
            finally
            {
                await ui.SignOutAsync(default);
            }
        }
        catch (Exception e) when (e is BrotherException or HttpRequestException or TaskCanceledException)
        {
            Say("STOPPED: " + e.Message);
        }

        Say(ok ? "RESULT: worked." : "RESULT: did not work. The report shows how far it got.");
        Console.WriteLine($"Saved as {Path.GetFullPath(reportPath)}. Passwords, PINs and profile names are left out; it does show folder paths and the account name. Read it over before you share it.");
        return ok ? 0 : 1;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0) text.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }

        Console.WriteLine();
        return text.ToString();
    }
}
