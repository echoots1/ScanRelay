using ScanRelay.ProbeTools;

namespace ScanRelay.KyoceraProbe;

public static class ProbeProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            Console.WriteLine("""
                Looks at a Kyocera printer's address book service and saves a report. It only reads.

                  kyocera-probe <printer address> [--netviewer "<KYOCERA Net Viewer install folder>"] [--brief]

                --brief leaves the address book itself out of the report.

                  kyocera-probe <printer address> --write-test [--number 150] [--plain]

                The first write. It adds one contact called "ScanRelay write test" at that number, renames
                it, deletes it, and checks that every other contact is exactly as it was. Back up the
                address book first. --plain uses port 9090 without encryption.

                Before you run it, add one test entry to the printer's address book whose name starts
                with "ScanRelay" (a made-up folder and a throwaway password). The report keeps that entry
                readable and removes every other name, address, path, sign-in name and password.
                """);
            return args.Length == 0 ? 0 : 1;
        }

        var netViewer = ProbeConsole.Option(args, "--netviewer");

        // Printers use a self-signed certificate. This tool only ever talks to the one address it is given.
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        if (args.Contains("--write-test"))
        {
            var number = int.TryParse(ProbeConsole.Option(args, "--number"), out var chosen) ? chosen : 150;
            var plain = args.Contains("--plain");
            const string writeReport = "kyocera-write-test-report.txt";
            bool ok;
            await using (var report = new StreamWriter(writeReport))
            {
                ok = await WriteTest.RunAsync(http, $"{(plain ? "http" : "https")}://{args[0]}:{(plain ? 9090 : 9091)}", number, report);
            }

            Console.WriteLine();
            Console.WriteLine($"Saved as {Path.GetFullPath(writeReport)}.");
            return ok ? 0 : 1;
        }

        const string file = "kyocera-probe-report.txt";
        await using (var report = new StreamWriter(file))
        {
            await new Probe(http, report).RunAsync(args[0], netViewer, args.Contains("--brief"));
        }

        Console.WriteLine($"Saved as {Path.GetFullPath(file)}. Read it over before you share it.");
        return 0;
    }
}
