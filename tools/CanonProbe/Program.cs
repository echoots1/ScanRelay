using ScanRelay.ProbeTools;
using System.Security.Cryptography;
using ScanRelay.Modules.CanonImageClass;

namespace ScanRelay.CanonProbe;

/// <summary>
/// Tries the Canon module's steps against a real printer, one deliberate step at a time,
/// and writes down what the printer answered so the module can be corrected where it guessed.
/// </summary>
public static class CanonProbeProgram
{
    // Set in Main: the files go in the current folder unless --out names another.
    private static string Report = "canon-probe-report.txt";
    private static string SavedBook = "canon-probe-saved.dcm";
    private static string SavedKey = "canon-probe-saved.key";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            Console.WriteLine("""
                Tries the Canon module's steps against an imageCLASS printer and saves a report.

                  canon-probe <printer address> --id <System Manager ID>
                      Signs in and exports the address book. Changes nothing on the printer.

                  canon-probe <printer address> --id <ID> --save
                      The same, and keeps the export in this folder, as the app does when you press
                      "Read the address book from the printer". The printer will not take a load for
                      a few minutes after any export.

                  canon-probe <printer address> --id <ID> --add "<name>" --folder "<share\folder>"
                      Loads the saved copy plus one shared folder entry, with the password copied from
                      the template entry in the saved copy. Nothing is exported first.

                  canon-probe <printer address> --id <ID> --restore
                      Loads the saved copy as it is, which takes the added entry off again.

                Options:  --template "<name>"   the entry whose password is copied (default: ScanRelay)
                          --book-pin <PIN>      the address book PIN, if the printer has one
                          --plain               use http instead of https
                          --out <folder>        where the report and the saved copy go (default: this folder)

                The System Manager PIN is asked for and not shown. Export the address book by hand first as a backup.
                """);
            return args.Length == 0 ? 0 : 1;
        }

        string? Option(string name) => ProbeConsole.Option(args, name);
        var outFolder = Option("--out") ?? ".";
        Directory.CreateDirectory(outFolder);
        (Report, SavedBook, SavedKey) = (Path.Combine(outFolder, "canon-probe-report.txt"), Path.Combine(outFolder, "canon-probe-saved.dcm"), Path.Combine(outFolder, "canon-probe-saved.key"));
        var id = Option("--id");
        if (id is null)
        {
            Console.Error.WriteLine("Give the System Manager ID with --id.");
            return 1;
        }

        var pin = Environment.GetEnvironmentVariable("CANON_PIN") ?? ProbeConsole.ReadHidden("System Manager PIN: ");
        var template = Option("--template") ?? "ScanRelay";
        var (add, folder) = (Option("--add"), Option("--folder"));
        var (save, restore) = (args.Contains("--save"), args.Contains("--restore"));
        if (add is not null && folder is null)
        {
            Console.Error.WriteLine("--add needs --folder, the share and folder the entry points at.");
            return 1;
        }

        await using var report = new StreamWriter(Report);
        void Say(string line)
        {
            report.WriteLine(line);
            Console.WriteLine(line);
        }

        var told = false;
        using var ui = new CanonRemoteUi(args[0], secure: !args.Contains("--plain"))
        {
            Trace = report.WriteLine,
            WaitWhileBusy = TimeSpan.FromMinutes(15),
            Waiting = () =>
            {
                if (!told) Console.WriteLine("The printer will not take a load yet, as happens for a few minutes after an export. Waiting and asking again every 15 seconds, for up to 15 minutes.");
                told = true;
            },
        };
        var ok = false;
        try
        {
            Say($"Canon probe, {DateTime.UtcNow:u}.");
            Say("Model, from the sign-in page: " + (await ui.ReadModelAsync(default) ?? "not found"));
            await ui.SignInAsync(id, pin, default);
            Say("Signed in as System Manager.");
            try
            {
                if (add is null && !restore)
                {
                    var filePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
                    var bytes = await ui.ExportAddressBookAsync(filePassword, Option("--book-pin"), default);
                    Describe(DcmFile.Read(bytes).AddressBook(), template, Say);
                    if (save)
                    {
                        File.WriteAllBytes(SavedBook, bytes);
                        File.WriteAllText(SavedKey, filePassword);
                        Say($"Kept the export as {SavedBook}. Loads are built from it. Wait a few minutes before the first one. Delete {SavedBook} and {SavedKey} when you are done.");
                    }

                    ok = true;
                }
                else
                {
                    if (!File.Exists(SavedBook) || !File.Exists(SavedKey))
                    {
                        Say("There is no saved copy of the address book in this folder. Run with --save first, wait a few minutes, then run this again. Nothing was changed.");
                        return 1;
                    }

                    // As the app does it: no export first, everything from the copy read earlier.
                    var filePassword = File.ReadAllText(SavedKey).Trim();
                    var saved = DcmFile.Read(File.ReadAllBytes(SavedBook));
                    var book = saved.AddressBook();
                    Say($"Using the copy saved at {File.GetLastWriteTimeUtc(SavedBook):u}. Nothing is exported first.");
                    Describe(book, template, Say);
                    if (add is not null)
                    {
                        var source = book.Entries.FirstOrDefault(e => e.IsFolder && e.Name.Equals(template, StringComparison.OrdinalIgnoreCase));
                        if (source is null)
                        {
                            Say($"The saved copy has no shared folder entry called \"{template}\" to copy the password from. Make one on the printer by hand, then run with --save again. Nothing was changed.");
                            return 1;
                        }

                        // As the app does it: the template was only there to be read, and does not go back onto the printer.
                        book.Entries.Remove(source);
                        book.Entries.RemoveAll(e => e.Name.Equals(Cut(add), StringComparison.OrdinalIgnoreCase));
                        book.Entries.Add(CanonEntry.Folder(0, Cut(add), source.Host, folder!, source.UserName, source.EncryptedPassword));
                        Say($"Loading the saved copy without \"{template}\", plus \"{Cut(add)}\" -> \\\\{source.Host}\\{folder}, user {source.UserName}, password copied from \"{template}\".");
                    }
                    else
                    {
                        Say("Loading the saved copy as it is.");
                    }

                    book.Renumber();
                    saved.SetAddressBook(book);
                    var bytes = saved.Write();
                    Say($"The file is {bytes.Length} bytes and holds {book.Entries.Count} entries.");
                    var said = await ui.ImportAddressBookAsync(bytes, filePassword, Option("--book-pin"), default);
                    Say($"The printer says the load finished: \"{said}\".");
                    Say("The address book is not read back here, because an export would make the printer refuse loads again for a few minutes.");
                    Say("Look at the address book in the Remote UI or at the panel." + (add is not null ? $" If \"{Cut(add)}\" is there, scan a page to it: if the file arrives, the copied password works." : ""));
                    ok = true;
                }
            }
            finally
            {
                await ui.SignOutAsync(default);
            }
        }
        catch (Exception e) when (e is CanonException or FormatException or HttpRequestException or TaskCanceledException)
        {
            Say("STOPPED: " + e.Message);
        }

        Say(ok ? "RESULT: worked." : "RESULT: did not work. The report shows how far it got.");
        Console.WriteLine($"Saved as {Path.GetFullPath(Report)}. It has no names of people and no passwords; read it over before you share it.");
        return ok ? 0 : 1;
    }

    private static string Cut(string name) => CanonAdapter.Cut(name);

    private static void Describe(CanonBook book, string template, Action<string> say)
    {
        var folders = book.Entries.Where(e => e.IsFolder).ToList();
        var ours = folders.Where(e => CanonAdapter.KeyOf(e) is not null).ToList();
        say($"Address book: {book.Entries.Count} entries, {folders.Count} of them shared folders, {ours.Count} of those in ScanRelay's form.");
        say($"Template entry \"{template}\": {(folders.Any(e => e.Name.Equals(template, StringComparison.OrdinalIgnoreCase)) ? "found" : "not found")}.");
        foreach (var e in folders.Where(e => e.Name.StartsWith("ScanRelay", StringComparison.OrdinalIgnoreCase)))
        {
            say($"  {e.Get("indexId"),3}  {e.Name}  ->  \\\\{e.Host}\\{e.Path}  as {e.UserName}  (password {(e.EncryptedPassword.Length > 0 ? "set" : "not set")})");
        }
    }
}
