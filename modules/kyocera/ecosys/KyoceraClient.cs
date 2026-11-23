using System.Text;
using System.Xml.Linq;

namespace ScanRelay.Modules.KyoceraEcosys;

/// <summary>One contact in the printer's address book, with only the parts this module reads or writes.</summary>
public sealed record Contact(int Id, string Name, string SmbServer, string SmbPath, int SmbPort, string SmbLoginName, string SmbPassword, bool HasOtherDestinations);

public sealed class KyoceraException(string message, string? result = null) : Exception(message)
{
    public string? Result { get; } = result;
}

/// <summary>
/// Talks to the address book part of the printer's network service. The messages follow what
/// a real ECOSYS M8124cidn returned and the shapes KYOCERA Net Viewer uses. Kyocera does not document them.
/// </summary>
public sealed class KyoceraClient(HttpClient http, string baseUrl)
{
    private const string Root = "http://www.kyoceramita.com/ws/km-wsdl/";
    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Wsa = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace Book = Root + "setting/address_book";
    private static readonly XNamespace Info = Root + "information/device_information";
    private static readonly TimeSpan[] BusyWaits = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    /// <summary>Set by tests to skip real waiting.</summary>
    public Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = Task.Delay;

    /// <summary>The last request and answer, for the hand test tool to show.</summary>
    public Action<string, string>? Trace { get; init; }

    public async Task<(string Model, string? Firmware)> IdentifyAsync(CancellationToken ct)
    {
        var body = await CallAsync(Info, "information/device_information", "get_device_identification_information", [], ct);
        var model = body.Descendants(Info + "product_name").Elements(Info + "name").FirstOrDefault()?.Value.Trim() ?? "";
        var main = body.Descendants(Info + "version_information")
            .FirstOrDefault(v => v.Element(Info + "type")?.Value.Trim() == "MAIN")?.Element(Info + "version")?.Value.Trim();
        return (model, main);
    }

    /// <summary>Reads every contact. The printer hands them over a page at a time.</summary>
    public async Task<List<Contact>> ReadAllAsync(CancellationToken ct)
    {
        var created = await CallAsync(Book, "setting/address_book", "create_personal_address_enumeration", [new XElement(Book + "number", 50)], ct);
        var enumeration = created.Descendants(Book + "enumeration").FirstOrDefault()?.Value.Trim()
            ?? throw new KyoceraException("The printer did not start listing its address book.");
        var contacts = new Dictionary<int, Contact>();
        try
        {
            for (var page = 0; page < 200; page++)
            {
                var body = await CallAsync(Book, "setting/address_book", "get_personal_address_list", [new XElement(Book + "enumeration", enumeration)], ct,
                    alsoOk: ["ALL_GET_COMPLETE"]);
                var before = contacts.Count;
                foreach (var contact in body.Descendants(Book + "personal_address").Select(Parse).Where(c => c is not null)) contacts[contact!.Id] = contact;
                if (Result(body) == "ALL_GET_COMPLETE" || contacts.Count == before) break;
            }
        }
        finally
        {
            try
            {
                await CallAsync(Book, "setting/address_book", "destroy_personal_address_enumeration", [new XElement(Book + "enumeration", enumeration)], ct);
            }
            catch (Exception e) when (e is KyoceraException or HttpRequestException)
            {
                // The list was already read. A list the printer keeps open times out on its own.
            }
        }

        return [.. contacts.Values.OrderBy(c => c.Id)];
    }

    public Task AddAsync(Contact contact, CancellationToken ct) =>
        CallAsync(Book, "setting/address_book", "add_personal_address", [ToXml(contact)], ct);

    public Task UpdateAsync(Contact contact, CancellationToken ct) =>
        CallAsync(Book, "setting/address_book", "update_personal_address", [ToXml(contact)], ct);

    public Task DeleteAsync(int id, CancellationToken ct) =>
        CallAsync(Book, "setting/address_book", "delete_personal_address", [new XElement(Book + "personal_address", id)], ct);

    private static Contact? Parse(XElement entry)
    {
        static string Text(XElement? parent, string name) => parent?.Element(Book + name)?.Value.Trim() ?? "";
        var name = entry.Element(Book + "name_information");
        if (!int.TryParse(Text(name, "id"), out var id)) return null;
        var smb = entry.Element(Book + "smb_information");
        var other = Text(entry.Element(Book + "email_information"), "address").Length > 0
            || Text(entry.Element(Book + "ftp_information"), "server_name").Length > 0
            || Text(entry.Element(Book + "ifax_information"), "address").Length > 0
            || Text(entry.Element(Book + "fax_information"), "fax_number").Length > 0;
        return new Contact(id, Text(name, "name"), Text(smb, "server_name"), Text(smb, "file_path"),
            int.TryParse(Text(smb, "port_number"), out var port) ? port : 445, Text(smb, "login_name"), Text(smb, "login_password"), other);
    }

    private static XElement ToXml(Contact c) => new(Book + "personal_address",
        new XElement(Book + "name_information",
            new XElement(Book + "name", c.Name),
            new XElement(Book + "furigana", c.Name),
            new XElement(Book + "id", c.Id)),
        new XElement(Book + "smb_information",
            new XElement(Book + "server_name", c.SmbServer),
            new XElement(Book + "file_path", c.SmbPath),
            new XElement(Book + "port_number", c.SmbPort),
            new XElement(Book + "login_name", c.SmbLoginName),
            new XElement(Book + "login_password", c.SmbPassword)));

    private static string? Result(XElement body) =>
        body.Descendants().FirstOrDefault(e => e.Name.LocalName == "result")?.Value.Trim();

    private async Task<XElement> CallAsync(XNamespace ns, string service, string operation, object[] content, CancellationToken ct, string[]? alsoOk = null)
    {
        var action = ns.NamespaceName + "/" + operation;
        var envelope = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", Soap), new XAttribute(XNamespace.Xmlns + "wsa", Wsa), new XAttribute(XNamespace.Xmlns + "k", ns),
            new XElement(Soap + "Header", new XElement(Wsa + "Action", new XAttribute(Soap + "mustUnderstand", "true"), action)),
            new XElement(Soap + "Body", new XElement(ns + (operation + "Request"), content)));
        var request = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + envelope.ToString(SaveOptions.DisableFormatting);

        for (var attempt = 0; ; attempt++)
        {
            using var message = new StringContent(request, Encoding.UTF8);
            message.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            using var response = await http.PostAsync($"{baseUrl}/ws/km-wsdl/{service}", message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            Trace?.Invoke(request, text);

            XElement body;
            try
            {
                body = XDocument.Parse(text).Root!.Element(Soap + "Body") ?? throw new KyoceraException("The printer's answer had no body.");
            }
            catch (System.Xml.XmlException)
            {
                throw new KyoceraException($"The printer answered {(int)response.StatusCode} with something that is not the expected kind of message.");
            }

            if (body.Element(Soap + "Fault") is { } fault)
            {
                var reason = fault.Descendants(Soap + "Text").FirstOrDefault()?.Value.Trim() ?? "no reason given";
                throw new KyoceraException($"The printer does not accept \"{operation}\": {reason}.", "FAULT");
            }

            var result = Result(body);
            if (result == "SUCCESS" || (alsoOk?.Contains(result) ?? false)) return body;

            // The printer is waking up or busy. Give it a moment.
            if (result is "PREPARING_NOW" or "SYSTEM_BUSY_ERROR" or "DEEP_SLEEP_NOW_ERROR" && attempt < BusyWaits.Length)
            {
                await Wait(BusyWaits[attempt], ct);
                continue;
            }

            var detail = body.Descendants(Book + "error_information").Select(Result).FirstOrDefault(r => r is not null and not "SUCCESS");
            throw new KyoceraException(Explain(operation, detail ?? result), detail ?? result);
        }
    }

    private static string Explain(string operation, string? result) => result switch
    {
        "ACCESS_ERROR" => "The printer refused access. Its sign-in for the network service is on, and this version of the module cannot sign in.",
        "OVERLAPED_ID_ERROR" => "The printer already has a contact with that number.",
        "MAXIMUM_INFORMATION_ERROR" => "The printer's address book is full.",
        "INVALID_VALUE_ERROR" => "The printer rejected a value. The contact number may be outside what this model allows, or a name or path is too long.",
        "INFORMATION_NOT_FOUND_ERROR" => "The printer has no contact with that number.",
        "NOT_SUPPORTED_ERROR" => $"This printer does not support \"{operation}\".",
        "SYSTEM_BUSY_ERROR" or "PREPARING_NOW" => "The printer stayed busy. Try again in a minute.",
        "DEEP_SLEEP_NOW_ERROR" => "The printer is in deep sleep and did not wake. Wake it at the panel and try again.",
        null => $"The printer gave no result for \"{operation}\".",
        _ => $"The printer answered {result} to \"{operation}\".",
    };
}
