using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace ScanRelay.KyoceraProbe;

/// <summary>
/// Looks at a Kyocera printer's address book service and writes down what it finds, so
/// the Kyocera module can be built against the real thing. It only reads. It never changes
/// the printer. Everything personal is taken out of the report before it is saved.
/// </summary>
public sealed partial class Probe(HttpClient http, TextWriter report)
{
    public const string Namespace = "http://www.kyoceramita.com/ws/km-wsdl/setting/address_book";
    public const string Path = "/ws/km-wsdl/setting/address_book";

    /// <summary>Entries whose name starts with this are test entries, and are kept readable in the report.</summary>
    public const string TestPrefix = "ScanRelay";

    public async Task RunAsync(string address, string? netViewerFolder, bool brief = false, CancellationToken ct = default)
    {
        report.WriteLine("Kyocera address book probe");
        report.WriteLine($"Run at {DateTime.UtcNow:u}. Read-only: nothing on the printer was changed.");
        report.WriteLine();

        var open = new List<(int Port, string Scheme)>();
        foreach (var (port, scheme) in new[] { (9091, "https"), (9090, "http") })
        {
            var isOpen = await PortOpenAsync(address, port, ct);
            report.WriteLine($"Port {port} ({scheme}): {(isOpen ? "open" : "closed or filtered")}");
            if (isOpen) open.Add((port, scheme));
        }

        if (open.Count == 0)
        {
            report.WriteLine();
            report.WriteLine("Neither port answers. The printer's network service (the one KYOCERA Net Viewer uses) is off or blocked.");
        }

        foreach (var (port, scheme) in open)
        {
            var url = $"{scheme}://{address}:{port}{Path}";
            report.WriteLine();
            report.WriteLine($"=== {url} ===");
            foreach (var wsdl in new[] { url + "?wsdl", $"{scheme}://{address}:{port}/ws/km-wsdl/setting/address_book.wsdl" })
            {
                await TryAsync($"GET {wsdl[(wsdl.IndexOf(':', 6) + 1)..]}", async () =>
                {
                    using var response = await http.GetAsync(wsdl, ct);
                    var text = await response.Content.ReadAsStringAsync(ct);
                    report.WriteLine($"  {(int)response.StatusCode}, {text.Length} characters");
                    if (response.IsSuccessStatusCode && text.Contains("definitions", StringComparison.OrdinalIgnoreCase))
                    {
                        report.WriteLine("  This looks like the service description. Operation names in it:");
                        foreach (var name in OperationName().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order())
                        {
                            report.WriteLine("    " + name);
                        }
                    }
                });
            }

            if (!brief)
            {
                foreach (var soap in new[] { "1.2", "1.1" })
                {
                    await TryAsync($"Read the address book, SOAP {soap}, with no sign-in", () => ReadAsync(url, soap, ct));
                }
            }

            // More questions that only read: what the service supports, how many entries, which model and firmware, and whether sign-in is on.
            var root = $"{scheme}://{address}:{port}/ws/km-wsdl";
            foreach (var (service, operation, inner) in new[]
            {
                ("setting/address_book", "get_service_information", ""),
                ("setting/address_book", "get_personal_address_count", ""),
                ("setting/address_book", "get_one_touch_key_count", ""),
                ("information/device_information", "get_device_identification_information", ""),
                ("security/authentication_authorization", "get_authentication_status", ""),
                ("security/authentication_authorization", "get_service_information", ""),
            })
            {
                await TryAsync($"{service}: {operation}", async () =>
                {
                    var answer = await PostAsync($"{root}/{service}", "1.2", operation, inner, ct, "http://www.kyoceramita.com/ws/km-wsdl/" + service);
                    report.WriteLine($"  HTTP {answer.Status}");
                    report.WriteLine(Indent(BodyOnly(Redact(answer.Body))));
                });
            }
        }

        if (netViewerFolder is not null)
        {
            report.WriteLine();
            report.WriteLine($"=== Names inside KYOCERA Net Viewer ({netViewerFolder}) ===");
            ScanFolder(netViewerFolder);
            report.WriteLine();
            report.WriteLine("=== The shape of the address book messages, read from Net Viewer's files ===");
            ContractReader.Write(netViewerFolder, report);
        }
    }

    private async Task ReadAsync(string url, string soap, CancellationToken ct)
    {
        var create = await PostAsync(url, soap, "create_personal_address_enumeration", "<k:number>25</k:number>", ct);
        report.WriteLine($"  create_personal_address_enumeration: HTTP {create.Status}");
        report.WriteLine(Indent(Redact(create.Body)));
        var enumeration = Tag(create.Body, "enumeration");
        if (create.Status != 200 || enumeration is null)
        {
            report.WriteLine("  No enumeration came back, so the list was not asked for.");
            return;
        }

        report.WriteLine("  The printer answered a read with no sign-in. On firmware without the fix for CVE-2022-1026 anyone on the network can do this,");
        report.WriteLine("  and the answer includes the passwords stored in the address book.");

        // The printer prepares the list in the background and says when it is ready.
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 3 : 2), ct);
            var list = await PostAsync(url, soap, "get_personal_address_list", $"<k:enumeration>{enumeration}</k:enumeration>", ct);
            var result = Tag(list.Body, "result");
            report.WriteLine($"  get_personal_address_list, try {attempt}: HTTP {list.Status}, result {result ?? "(none)"}");
            if (result is "PREPARING_NOW" or "DEEP_SLEEP_NOW_ERROR") continue;
            report.WriteLine(Indent(Redact(list.Body)));
            return;
        }
    }

    private async Task<(int Status, string Body)> PostAsync(string url, string soap, string operation, string inner, CancellationToken ct, string? serviceNamespace = null)
    {
        var ns = serviceNamespace ?? Namespace;
        var action = $"{ns}/{operation}";
        var (envelope, contentType, header) = soap == "1.2"
            ? ("http://www.w3.org/2003/05/soap-envelope", "application/soap+xml",
                $"<wsa:Action s:mustUnderstand=\"true\">{action}</wsa:Action>")
            : ("http://schemas.xmlsoap.org/soap/envelope/", "text/xml",
                $"<wsa:MessageID>urn:uuid:{Guid.NewGuid()}</wsa:MessageID><wsa:To>{url}</wsa:To><wsa:Action>{action}</wsa:Action>");
        var body = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"{envelope}\" xmlns:wsa=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" xmlns:k=\"{ns}\">"
            + $"<s:Header>{header}</s:Header><s:Body><k:{operation}Request>{inner}</k:{operation}Request></s:Body></s:Envelope>";
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) { CharSet = "utf-8" };
        using var response = await http.PostAsync(url, content, ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Takes names, addresses, paths, sign-in names and passwords out of a response. The structure
    /// and field names stay. An entry named "ScanRelay…" is a test entry and keeps everything but its passwords.
    /// </summary>
    public static string Redact(string xml)
    {
        var entries = Entry().Replace(xml, match =>
        {
            var isTest = Regex.IsMatch(match.Value, $@"<(?:\w+:)?name>\s*{TestPrefix}", RegexOptions.IgnoreCase);
            return Field().Replace(match.Value, field =>
            {
                var (name, value) = (field.Groups["name"].Value, field.Groups["value"].Value);
                var secret = name.Contains("password", StringComparison.OrdinalIgnoreCase);
                var personal = secret || PersonalFields.Contains(name);
                if (value.Length == 0 || !personal || (isTest && !secret)) return field.Value;
                return field.Value.Replace($">{value}<", $">(removed, {value.Length} characters)<");
            });
        });

        // Anything that looks like a password outside an entry goes too.
        return Password().Replace(entries, m => m.Groups["value"].Value.StartsWith("(removed", StringComparison.Ordinal)
            ? m.Value
            : m.Value.Replace($">{m.Groups["value"].Value}<", $">(removed, {m.Groups["value"].Value.Length} characters)<"));
    }

    private static readonly HashSet<string> PersonalFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "furigana", "address", "server_name", "file_path", "login_name", "fax_number", "sub_address", "host_name", "path",
    };

    private void ScanFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            report.WriteLine("  That folder does not exist.");
            return;
        }

        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => System.IO.Path.GetExtension(f).ToLowerInvariant() is ".dll" or ".exe" or ".wsdl" or ".xml" or ".xsd" or ".config"))
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                foreach (var text in new[] { Encoding.Latin1.GetString(bytes), Encoding.Unicode.GetString(bytes) })
                {
                    foreach (Match m in Interesting().Matches(text)) found.Add(m.Value);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        report.WriteLine(found.Count == 0 ? "  Nothing that looks like an address book operation was found." : "  " + string.Join(Environment.NewLine + "  ", found));
    }

    private async Task TryAsync(string what, Func<Task> action)
    {
        report.WriteLine();
        report.WriteLine(what);
        try
        {
            await action();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            report.WriteLine($"  Failed: {e.GetType().Name}: {e.Message} {e.InnerException?.Message}");
        }
    }

    private static async Task<bool> PortOpenAsync(string address, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await client.ConnectAsync(address, port, timeout.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static string? Tag(string xml, string name) =>
        Regex.Match(xml, $@"<(?:\w+:)?{name}>\s*([^<]*?)\s*</(?:\w+:)?{name}>") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Leaves out the long list of namespaces every answer starts with.</summary>
    private static string BodyOnly(string xml) =>
        Regex.Match(xml, @"<(?:[\w-]+:)?Body[^>]*>(.*)</(?:[\w-]+:)?Body>", RegexOptions.Singleline) is { Success: true } m ? m.Groups[1].Value.Trim() : xml;

    private static string Indent(string text) => "    " + (text.Length > 60000 ? text[..60000] + "… (cut)" : text).Replace("><", ">\n    <");

    [GeneratedRegex(@"<(?:\w+:)?personal_address>.*?</(?:\w+:)?personal_address>", RegexOptions.Singleline)]
    private static partial Regex Entry();

    [GeneratedRegex(@"<(?:\w+:)?(?<name>\w+)>(?<value>[^<]*)</(?:\w+:)?\k<name>>")]
    private static partial Regex Field();

    [GeneratedRegex(@"<(?:\w+:)?\w*password\w*>(?<value>[^<]+)</", RegexOptions.IgnoreCase)]
    private static partial Regex Password();

    [GeneratedRegex(@"operation\s+name=""(\w+)""")]
    private static partial Regex OperationName();

    [GeneratedRegex(@"(?:/ws/km-wsdl/[\w/]+|\b\w*(?:personal_address|address_book|one_touch|group_address)\w*\b)")]
    private static partial Regex Interesting();
}
