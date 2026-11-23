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

                Before you run it, add one test entry to the printer's address book whose name starts
                with "ScanRelay" (a made-up folder and a throwaway password). The report keeps that entry
                readable and removes every other name, address, path, sign-in name and password.
                """);
            return args.Length == 0 ? 0 : 1;
        }

        var netViewer = Array.IndexOf(args, "--netviewer") is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

        // Printers use a self-signed certificate. This tool only ever talks to the one address it is given.
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        const string file = "kyocera-probe-report.txt";
        await using (var report = new StreamWriter(file))
        {
            await new Probe(http, report).RunAsync(args[0], netViewer, args.Contains("--brief"));
        }

        Console.WriteLine($"Saved as {Path.GetFullPath(file)}. Read it over before you share it.");
        return 0;
    }
}
