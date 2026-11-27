using ScanRelay.ProbeTools;
using ScanRelay.Modules.BrotherAds;

namespace ScanRelay.BrotherProbe;

/// <summary>
/// Tries the Brother module's steps against a real scanner and writes down what it answered.
/// By itself it only reads. With --add or --remove it saves or clears one profile.
/// </summary>
public static class BrotherProbeProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            Console.WriteLine("""
                Tries the Brother module's steps against an ADS scanner and saves a report.

                  brother-probe <scanner address>
                      Signs in and reads the profiles. Changes nothing on the scanner.

                  brother-probe <scanner address> --add "<name>" --folder "\\server\share\folder" --user "<account>"
                      Puts one Network profile into the first slot that has no name, and reads it back.
                      The share password is asked for and not shown. No other profile is touched.

                  brother-probe <scanner address> --remove "<name>"
                      Clears the Network profile with that name again.

                Options:  --plain          use http instead of https (the scanner only takes a sign-in over https)
                          --out <folder>   where the report goes (default: this folder)
                          --scripts        also record the scanner's own page scripts in the report

                The Web Based Management password is asked for and not shown.
                """);
            return args.Length == 0 ? 0 : 1;
        }

        string? Option(string name) => ProbeConsole.Option(args, name);
        var folder = Option("--out") ?? ".";
        Directory.CreateDirectory(folder);
        var reportPath = Path.Combine(folder, "brother-probe-report.txt");
        var password = Environment.GetEnvironmentVariable("BROTHER_PASSWORD") ?? ProbeConsole.ReadHidden("Web Based Management password: ");
        var (add, folderPath, user, remove) = (Option("--add"), Option("--folder"), Option("--user"), Option("--remove"));
        if (add is not null && (folderPath is null || user is null))
        {
            Console.Error.WriteLine("--add needs --folder, the full path the profile points at, and --user, the account it signs in to the share with.");
            return 1;
        }

        var sharePassword = add is null ? "" : Environment.GetEnvironmentVariable("BROTHER_SHARE_PASSWORD") ?? ProbeConsole.ReadHidden("Password of the share account: ");

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

            // The scanner's own page scripts, fetched before signing in so they are in the report even when sign-in fails.
            // They are the scanner's code and hold no settings. The first is the one that handles the Login button.
            foreach (var path in args.Contains("--scripts") ? new[] { "/EWS/common/js/mobilemenucontrl.js", "/EWS/common/js/netscan.js", "/EWS/common/js/submit_result_controller.js" } : [])
            {
                var (status, body) = await ui.GetPageAsync(path, default);
                report.WriteLine($"----- {path} ({status}, {body.Length} characters) -----");
                if (status == 200) report.WriteLine(body);
                report.WriteLine("----- end -----");
            }

            // The Login button's own handler is in a script not seen yet. Record the part of it that signs in.
            foreach (var path in new[] { "/EWS/common/js/status.js", "/EWS/common/js/status_language.js", "/EWS/common/js/menu_controller.js" })
            {
                var (status, body) = await ui.GetPageAsync(path, default);
                var at = status == 200 ? body.IndexOf("function login_pass", StringComparison.Ordinal) : -1;
                if (at < 0) continue;
                report.WriteLine($"----- login_pass, from {path} -----");
                report.WriteLine(body[at..Math.Min(body.Length, at + 2500)]);
                report.WriteLine("----- end -----");
            }

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

                if (add is not null)
                {
                    var name = BrotherAdapter.Cut(add);
                    var slot = slots.Where(s => s.Name.Length == 0).OrderBy(s => s.IsNetwork ? 0 : 1).ThenBy(s => s.Index).FirstOrDefault();
                    if (slot is null)
                    {
                        Say("Every one of the scanner's profile slots has a name, so there is nowhere to put a new one. Nothing was changed.");
                        return 1;
                    }

                    var others = slots.Where(s => s.Index != slot.Index).ToList();
                    if (!slot.IsNetwork)
                    {
                        Say($"Making profile {slot.Index + 1} a Network profile (it is {slot.TypeName} and has no name).");
                        await ui.MakeNetworkAsync([slot.Index], default);
                    }

                    Say($"Saving \"{name}\" into profile {slot.Index + 1}: {folderPath} as {user}.");
                    await ui.SaveNetworkProfileAsync(slot.Index, await ui.ReadNetworkProfileAsync(slot.Index, default), name, folderPath!, user!, sharePassword, default);

                    var kept = await ui.ReadNetworkProfileAsync(slot.Index, default);
                    var back = kept.Element("FolderPass")?.Value ?? "";
                    Say($"Read back: name {(kept.Element("ProfileName")?.Value == name ? "kept" : "NOT kept")}, folder {(kept.Element("FolderPath")?.Value == folderPath ? "kept" : "NOT kept")}, account {(kept.Element("FolderUser")?.Value == user ? "kept" : "NOT kept")}.");
                    Say("The scanner hands the password back " + (back == sharePassword ? "as it was typed." : back.Length == 0 ? "as nothing." : back.All(c => c == '*') ? "as a row of stars." : $"in another form, {back.Length} characters long."));
                    var after = await ui.ReadSlotsAsync(default);
                    var untouched = others.SequenceEqual(after.Where(s => s.Index != slot.Index));
                    Say($"Every other profile is as it was: {(untouched ? "yes" : "NO")}.");
                    Say($"Now choose \"{name}\" at the scanner and scan a page. If the file arrives in the folder, the profile works.");
                    ok = untouched && kept.Element("ProfileName")?.Value == name;
                }
                else if (remove is not null)
                {
                    var name = BrotherAdapter.Cut(remove);
                    var slot = slots.FirstOrDefault(s => s.IsNetwork && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (slot is null)
                    {
                        Say("No Network profile has that name. Nothing was changed.");
                        return 1;
                    }

                    Say($"Clearing profile {slot.Index + 1}.");
                    await ui.SaveNetworkProfileAsync(slot.Index, await ui.ReadNetworkProfileAsync(slot.Index, default), "", "", "", "", default);
                    var after = await ui.ReadSlotsAsync(default);
                    var cleared = after[slot.Index].Name.Length == 0;
                    var untouched = slots.Where(s => s.Index != slot.Index).SequenceEqual(after.Where(s => s.Index != slot.Index));
                    Say($"The profile is {(cleared ? "empty" : "STILL THERE")}. Every other profile is as it was: {(untouched ? "yes" : "NO")}.");
                    ok = cleared && untouched;
                }
                else
                {
                    ok = true;
                }

                if (!slots.Any(s => s.IsNetwork)) Say("No slot is of the Network kind. Set one profile to Network on the scanner's \"Scan to FTP/SFTP/Network/SharePoint\" page, then run this again.");

                // The page where a slot's kind is chosen. Its script is the scanner's own code and holds no settings.
                foreach (var path in args.Contains("--scripts") ? new[] { "/EWS/scan/netscan.html" } : [])
                {
                    var (status, body) = await ui.GetPageAsync(path, default);
                    report.WriteLine($"----- {path} ({status}, {body.Length} characters) -----");
                    if (status == 200) report.WriteLine(body);
                    report.WriteLine("----- end -----");
                }
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
}
