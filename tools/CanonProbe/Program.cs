using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ScanRelay.Modules.CanonImageClass;

namespace ScanRelay.CanonProbe;

/// <summary>
/// Tries the Canon module's steps against a real printer, one deliberate step at a time,
/// and writes down what the printer answered so the module can be corrected where it guessed.
/// </summary>
public static partial class CanonProbeProgram
{
    private const string Report = "canon-probe-report.txt";

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}[\\/]?$")]
    private static partial Regex OursPattern();

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            Console.WriteLine("""
                Tries the Canon module's steps against an imageCLASS printer and saves a report.

                  canon-probe <printer address> --id <System Manager ID>
                      Signs in and exports the address book. Changes nothing on the printer.

                  canon-probe <printer address> --id <ID> --add "<name>" --folder "<share\folder>"
                      Adds one shared folder entry, with the password copied from the template entry,
                      by loading the address book back. Every other entry is sent back as it was.

                  canon-probe <printer address> --id <ID> --remove "<name>"
                      Removes that one entry again.

                Options:  --template "<name>"   the entry whose password is copied (default: ScanRelay)
                          --book-pin <PIN>      the address book PIN, if the printer has one
                          --plain               use http instead of https

                The System Manager PIN is asked for and not shown. Export the address book by hand first as a backup.
                """);
            return args.Length == 0 ? 0 : 1;
        }

        string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var id = Option("--id");
        if (id is null)
        {
            Console.Error.WriteLine("Give the System Manager ID with --id.");
            return 1;
        }

        var pin = Environment.GetEnvironmentVariable("CANON_PIN") ?? ReadHidden("System Manager PIN: ");
        var template = Option("--template") ?? "ScanRelay";
        var (add, folder, remove) = (Option("--add"), Option("--folder"), Option("--remove"));
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

        using var ui = new CanonRemoteUi(args[0], secure: !args.Contains("--plain")) { Trace = report.WriteLine };
        var ok = false;
        try
        {
            Say($"Canon probe, {DateTime.UtcNow:u}.");
            Say("Model, from the sign-in page: " + (await ui.ReadModelAsync(default) ?? "not found"));
            await ui.SignInAsync(id, pin, default);
            Say("Signed in as System Manager.");
            try
            {
                var filePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
                var exported = DcmFile.Read(await ui.ExportAddressBookAsync(filePassword, Option("--book-pin"), default));
                var book = exported.AddressBook();
                Describe(book, template, Say);

                if (add is not null || remove is not null)
                {
                    var before = Others(book, add ?? remove!);
                    if (add is not null)
                    {
                        var source = book.Entries.FirstOrDefault(e => e.IsFolder && e.Name.Equals(template, StringComparison.OrdinalIgnoreCase));
                        if (source is null)
                        {
                            Say($"There is no shared folder entry called \"{template}\" to copy the password from. Make one by hand first. Nothing was changed.");
                            return 1;
                        }

                        book.Entries.RemoveAll(e => e.Name.Equals(Cut(add), StringComparison.OrdinalIgnoreCase));
                        book.Entries.Add(CanonEntry.Folder(0, Cut(add), source.Host, folder!, source.UserName, source.EncryptedPassword));
                        Say($"Adding \"{Cut(add)}\" -> \\\\{source.Host}\\{folder}, user {source.UserName}, password copied from \"{template}\".");
                    }
                    else
                    {
                        var removed = book.Entries.RemoveAll(e => e.Name.Equals(Cut(remove!), StringComparison.OrdinalIgnoreCase));
                        Say($"Removing \"{Cut(remove!)}\": {removed} entry found.");
                        if (removed == 0) return 1;
                    }

                    book.Renumber();
                    exported.SetAddressBook(book);
                    await ui.ImportAddressBookAsync(exported.Write(), filePassword, Option("--book-pin"), default);
                    Say("The printer says the load finished. Reading the address book back to check.");

                    var after = DcmFile.Read(await ui.ExportAddressBookAsync(filePassword, Option("--book-pin"), default)).AddressBook();
                    Describe(after, template, Say);
                    var present = after.Entries.Any(e => e.Name.Equals(Cut(add ?? remove!), StringComparison.OrdinalIgnoreCase));
                    var untouched = before.SequenceEqual(Others(after, add ?? remove!));
                    Say($"The entry is {(present ? "there" : "not there")}. Every other entry is exactly as it was: {(untouched ? "yes" : "NO")}.");
                    ok = untouched && present == (add is not null);
                    if (add is not null && ok)
                    {
                        var made = after.Entries.First(e => e.Name.Equals(Cut(add), StringComparison.OrdinalIgnoreCase));
                        var source = after.Entries.First(e => e.IsFolder && e.Name.Equals(template, StringComparison.OrdinalIgnoreCase));
                        Say($"Its stored password is {(made.EncryptedPassword == source.EncryptedPassword ? "the same as" : "DIFFERENT from")} the template's.");
                        Say("Now scan a page to it at the panel. If the file arrives, the copied password works.");
                    }
                }
                else
                {
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

    private static string Cut(string name) => name.Length > CanonBook.MaxNameLength ? name[..CanonBook.MaxNameLength] : name;

    /// <summary>Everything about every entry except the one being worked on and its number, to prove nothing else moved.</summary>
    private static List<string> Others(CanonBook book, string name) =>
        [.. book.Entries.Where(e => !e.Name.Equals(Cut(name), StringComparison.OrdinalIgnoreCase))
            .Select(e => string.Join("|", e.Attributes.Where(a => a.Name != "indexId").Select(a => a.Name + "=" + string.Join(",", a.Values))))];

    private static void Describe(CanonBook book, string template, Action<string> say)
    {
        var folders = book.Entries.Where(e => e.IsFolder).ToList();
        var ours = folders.Where(e => OursPattern().IsMatch(e.Path)).ToList();
        say($"Address book: {book.Entries.Count} entries, {folders.Count} of them shared folders, {ours.Count} of those in ScanRelay's form.");
        say($"Template entry \"{template}\": {(folders.Any(e => e.Name.Equals(template, StringComparison.OrdinalIgnoreCase)) ? "found" : "not found")}.");
        foreach (var e in folders.Where(e => e.Name.StartsWith("ScanRelay", StringComparison.OrdinalIgnoreCase)))
        {
            say($"  {e.Get("indexId"),3}  {e.Name}  ->  \\\\{e.Host}\\{e.Path}  as {e.UserName}  (password {(e.EncryptedPassword.Length > 0 ? "set" : "not set")})");
        }
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
