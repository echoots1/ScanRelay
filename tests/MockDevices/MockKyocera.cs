using System.Collections.Concurrent;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ScanRelay.MockDevices;

public sealed record KyoceraContact(int Id, string Name, string Email = "", string SmbServer = "", string SmbPath = "", int SmbPort = 445, string SmbLogin = "", string SmbPassword = "", string Fax = "");

/// <summary>
/// A fake Kyocera address book service. Its answers copy the structure a real
/// ECOSYS M8124cidn returned. How it treats writes (numbers out of range, a number
/// already in use, a full book) is an assumption until a real printer has been written to.
/// </summary>
public sealed class MockKyocera : IAsyncDisposable
{
    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Wsa = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace Book = "http://www.kyoceramita.com/ws/km-wsdl/setting/address_book";
    private static readonly XNamespace Info = "http://www.kyoceramita.com/ws/km-wsdl/information/device_information";
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<int, (int PageSize, int Position)> _enumerations = new();
    private int _nextEnumeration = 20;

    private MockKyocera(WebApplication app) => _app = app;

    public ConcurrentDictionary<int, KyoceraContact> Contacts { get; } = new();
    public string Model { get; set; } = "ECOSYS M8124cidn";
    public string Firmware { get; set; } = "2P4_S000.002.204";
    public int HighestNumber { get; set; } = 200;
    public int Capacity { get; set; } = 200;

    /// <summary>The next N requests are answered "busy", as a printer waking from sleep does.</summary>
    public int BusyAnswers;

    /// <summary>Answer ACCESS_ERROR to everything, as a printer with sign-in switched on would.</summary>
    public bool RequiresSignIn { get; set; }

    /// <summary>Leave passwords out of what is read back, as patched firmware may.</summary>
    public bool HidePasswords { get; set; }

    public int Writes;
    public List<string> Operations { get; } = [];
    public int Port { get; private set; }
    public string Address => "127.0.0.1";

    public static async Task<MockKyocera> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var mock = new MockKyocera(app);

        // What an office printer already holds: scan-to-email contacts and an old folder route.
        mock.Contacts[1] = new KyoceraContact(1, "Front Desk", Email: "frontdesk@example.com");
        mock.Contacts[2] = new KyoceraContact(2, "Accounts", Email: "accounts@example.com", Fax: "5550100");
        mock.Contacts[3] = new KyoceraContact(3, "Old Route", SmbServer: "oldserver", SmbPath: "scans\\someone", SmbLogin: "olduser", SmbPassword: "old-password");

        app.MapPost("/ws/km-wsdl/{**service}", async (HttpContext context, string service) =>
        {
            var request = await XDocument.LoadAsync(context.Request.Body, LoadOptions.None, context.RequestAborted);
            var action = request.Root!.Element(Soap + "Header")?.Element(Wsa + "Action")?.Value ?? "";
            var operation = action[(action.LastIndexOf('/') + 1)..];
            var body = request.Root.Element(Soap + "Body")!.Elements().First();
            lock (mock.Operations) mock.Operations.Add(operation);
            var answer = mock.Handle(service, operation, body);
            return Results.Text(
                new XElement(Soap + "Envelope", new XAttribute(XNamespace.Xmlns + "SOAP-ENV", Soap), new XAttribute(XNamespace.Xmlns + "kmaddrbook", Book),
                    new XAttribute(XNamespace.Xmlns + "kmdevinfo", Info), new XElement(Soap + "Body", answer)).ToString(),
                "application/soap+xml");
        });

        await app.StartAsync();
        mock.Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        return mock;
    }

    private XElement Handle(string service, string operation, XElement body)
    {
        var ns = service.EndsWith("device_information", StringComparison.Ordinal) ? Info : Book;
        XElement Answer(string result, params object[] more) => new(ns + (operation + "Response"), new XElement(ns + "result", result), more);

        if (Interlocked.Decrement(ref BusyAnswers) >= 0) return Answer("DEEP_SLEEP_NOW_ERROR");
        Interlocked.Exchange(ref BusyAnswers, Math.Max(0, BusyAnswers));
        if (RequiresSignIn) return Answer("ACCESS_ERROR");

        if (ns == Info && operation == "get_device_identification_information")
        {
            return Answer("SUCCESS", new XElement(Info + "information",
                new XElement(Info + "product_name", new XElement(Info + "name", Model), new XElement(Info + "absolute_name", Model)),
                new XElement(Info + "version_information", new XElement(Info + "type", "MAIN"), new XElement(Info + "version", Firmware)),
                new XElement(Info + "version_information", new XElement(Info + "type", "ENGINE"), new XElement(Info + "version", "2P3_1000.003.001"))));
        }

        switch (operation)
        {
            case "create_personal_address_enumeration":
            {
                var id = Interlocked.Increment(ref _nextEnumeration);
                _enumerations[id] = (int.Parse(body.Element(Book + "number")!.Value), 0);
                return Answer("SUCCESS", new XElement(Book + "enumeration", id));
            }

            case "get_personal_address_list":
            {
                var id = int.Parse(body.Element(Book + "enumeration")!.Value);
                if (!_enumerations.TryGetValue(id, out var state)) return Answer("INFORMATION_NOT_FOUND_ERROR");
                var all = Contacts.Values.OrderBy(c => c.Id).ToList();
                var page = all.Skip(state.Position).Take(state.PageSize).ToList();
                _enumerations[id] = (state.PageSize, state.Position + page.Count);
                var done = state.Position + page.Count >= all.Count;
                return Answer(done ? "ALL_GET_COMPLETE" : "SUCCESS", [.. page.Select(ToXml)]);
            }

            case "destroy_personal_address_enumeration":
                return Answer(_enumerations.TryRemove(int.Parse(body.Element(Book + "enumeration")!.Value), out _) ? "SUCCESS" : "INFORMATION_NOT_FOUND_ERROR");

            case "get_personal_address_count":
                return Answer("SUCCESS", new XElement(Book + "count", Contacts.Count));

            case "add_personal_address":
            case "update_personal_address":
            {
                foreach (var entry in body.Elements(Book + "personal_address"))
                {
                    var contact = FromXml(entry);
                    if (contact.Id < 1 || contact.Id > HighestNumber || contact.Name.Length is 0 or > 32) return Answer("INVALID_VALUE_ERROR");
                    if (operation == "add_personal_address")
                    {
                        if (Contacts.ContainsKey(contact.Id)) return Answer("OVERLAPED_ID_ERROR");
                        if (Contacts.Count >= Capacity) return Answer("MAXIMUM_INFORMATION_ERROR");
                    }
                    else if (!Contacts.ContainsKey(contact.Id))
                    {
                        return Answer("INFORMATION_NOT_FOUND_ERROR");
                    }

                    // A part left out of an update keeps what the printer had. That is an assumption; the write test checks the real thing.
                    if (operation == "update_personal_address" && entry.Element(Book + "email_information") is null)
                    {
                        contact = contact with { Email = Contacts[contact.Id].Email };
                    }

                    Contacts[contact.Id] = contact;
                    Interlocked.Increment(ref Writes);
                }

                return Answer("SUCCESS");
            }

            case "delete_personal_address":
            {
                foreach (var id in body.Elements(Book + "personal_address").Select(e => int.Parse(e.Value)))
                {
                    if (!Contacts.TryRemove(id, out _)) return Answer("INFORMATION_NOT_FOUND_ERROR");
                    Interlocked.Increment(ref Writes);
                }

                return Answer("SUCCESS");
            }

            default:
                return new XElement(Soap + "Fault", new XElement(Soap + "Reason", new XElement(Soap + "Text", "The [action] cannot be processed at the receiver")));
        }
    }

    private XElement ToXml(KyoceraContact c) => new(Book + "personal_address",
        new XElement(Book + "name_information", new XElement(Book + "name", c.Name), new XElement(Book + "furigana", c.Name), new XElement(Book + "id", c.Id)),
        new XElement(Book + "email_information", new XElement(Book + "address", c.Email)),
        new XElement(Book + "ftp_information", new XElement(Book + "server_name", ""), new XElement(Book + "port_number", 21)),
        new XElement(Book + "smb_information",
            new XElement(Book + "server_name", c.SmbServer),
            c.SmbPath.Length > 0 ? new XElement(Book + "file_path", c.SmbPath) : null,
            new XElement(Book + "port_number", c.SmbPort),
            c.SmbLogin.Length > 0 ? new XElement(Book + "login_name", c.SmbLogin) : null,
            c.SmbPassword.Length > 0 && !HidePasswords ? new XElement(Book + "login_password", c.SmbPassword) : null),
        new XElement(Book + "ifax_information", new XElement(Book + "address", "")),
        new XElement(Book + "fax_information", new XElement(Book + "fax_number", c.Fax), new XElement(Book + "ecm", "OFF")));

    private static KyoceraContact FromXml(XElement entry)
    {
        static string Text(XElement? parent, string name) => parent?.Element(Book + name)?.Value.Trim() ?? "";
        var name = entry.Element(Book + "name_information");
        var smb = entry.Element(Book + "smb_information");
        return new KyoceraContact(
            int.TryParse(Text(name, "id"), out var id) ? id : 0, Text(name, "name"), Text(entry.Element(Book + "email_information"), "address"),
            Text(smb, "server_name"), Text(smb, "file_path"), int.TryParse(Text(smb, "port_number"), out var port) ? port : 445,
            Text(smb, "login_name"), Text(smb, "login_password"), Text(entry.Element(Book + "fax_information"), "fax_number"));
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
