using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ScanRelay.MockDevices;

public sealed record MockEntry(int Slot, string Name, string? Key, string Protocol, string Host, string Path, string User, string? Password, string? Email = null);

/// <summary>
/// A fake printer with an address book, so modules can be tested without hardware.
/// It starts with entries the service does not own, such as fax numbers, to prove a
/// module leaves them alone.
/// </summary>
public sealed class MockPrinter : IAsyncDisposable
{
    private readonly WebApplication _app;

    private MockPrinter(WebApplication app) => _app = app;

    public ConcurrentDictionary<int, MockEntry> AddressBook { get; } = new();
    public string AdminUser { get; set; } = "admin";
    public string AdminPassword { get; set; } = "mock-admin-password";
    public string Model { get; set; } = "MockPrinter 1000";
    public string Firmware { get; set; } = "1.0";

    /// <summary>Answer every request with an error, as a printer in trouble would.</summary>
    public bool Broken { get; set; }

    /// <summary>How many changes the printer has accepted.</summary>
    public int Writes;

    public int Port { get; private set; }
    public string Address => "127.0.0.1";

    public static async Task<MockPrinter> StartAsync(int port = 0)
    {
        var app = MockHost.Create(port);
        var printer = new MockPrinter(app);
        printer.AddressBook[1] = new MockEntry(1, "Fax: Head office", null, "fax", "", "", "", null);
        printer.AddressBook[2] = new MockEntry(2, "Fax: Supplier", null, "fax", "", "", "", null);
        printer.AddressBook[10] = new MockEntry(10, "Shared folder (set up by hand)", null, "smb", "fileserver", "public", "someone", null);

        app.Use(async (context, next) =>
        {
            if (printer.Broken)
            {
                context.Response.StatusCode = 500;
                return;
            }

            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{printer.AdminUser}:{printer.AdminPassword}"));
            if (context.Request.Headers.Authorization != expected)
            {
                context.Response.StatusCode = 401;
                return;
            }

            await next();
        });
        app.MapGet("/api/info", () => new { model = printer.Model, firmware = printer.Firmware });
        app.MapGet("/api/addressbook", () => printer.AddressBook.Values.OrderBy(e => e.Slot).Select(e => e with { Password = null }));
        app.MapPut("/api/addressbook/{slot:int}", (int slot, MockEntry entry) =>
        {
            printer.AddressBook[slot] = entry with { Slot = slot };
            Interlocked.Increment(ref printer.Writes);
            return Results.NoContent();
        });
        app.MapDelete("/api/addressbook/{slot:int}", (int slot) =>
        {
            Interlocked.Increment(ref printer.Writes);
            return printer.AddressBook.TryRemove(slot, out _) ? Results.NoContent() : Results.NotFound();
        });

        printer.Port = await MockHost.StartAsync(app);
        return printer;
    }

    /// <summary>The entries in the range the sample module reserves.</summary>
    public IReadOnlyList<MockEntry> Managed => [.. AddressBook.Values.Where(e => e.Slot is >= 900 and <= 999).OrderBy(e => e.Slot)];

    public IReadOnlyList<MockEntry> Unmanaged => [.. AddressBook.Values.Where(e => e.Slot is < 900 or > 999).OrderBy(e => e.Slot)];

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
