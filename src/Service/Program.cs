using System.Reflection;
using Microsoft.EntityFrameworkCore;
using ScanRelay.Admin.Components;
using ScanRelay.Core;
using ScanRelay.Core.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options => options.ServiceName = "ScanRelay");

// The installer passes the data folder to the service. Without it, fall back to a default.
var dataFolder = new DataFolder(builder.Configuration["DataFolder"] ?? DefaultDataFolder(builder.Environment));
builder.Services.AddSingleton(dataFolder);
builder.Services.AddDbContextFactory<ScanRelayDbContext>(options =>
    options.UseSqlite($"Data Source={dataFolder.Database}"));

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

dataFolder.EnsureCreated();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ScanRelayDbContext>>().CreateDbContextAsync())
{
    // The database upgrades itself on start.
    await db.Database.MigrateAsync();
}

app.UseAntiforgery();
app.MapStaticAssets();

app.MapGet("/health", async (IDbContextFactory<ScanRelayDbContext> factory) =>
{
    await using var db = await factory.CreateDbContextAsync();
    var databaseOk = await db.Database.CanConnectAsync();
    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
    return Results.Json(
        new { status = databaseOk ? "ok" : "degraded", version, database = databaseOk },
        statusCode: databaseOk ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();

static string DefaultDataFolder(IHostEnvironment environment) =>
    OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ScanRelay")
        : Path.Combine(environment.ContentRootPath, "data");

public partial class Program;
