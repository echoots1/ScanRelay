using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScanRelay.Core.Alerts;
using ScanRelay.Core.Audit;
using ScanRelay.Core.Cloud;
using ScanRelay.Core.Data;
using ScanRelay.Core.Health;
using ScanRelay.Core.Install;
using ScanRelay.Core.Jobs;
using ScanRelay.Core.Modules;
using ScanRelay.Core.Printers;
using ScanRelay.Core.Security;
using ScanRelay.Core.Settings;
using ScanRelay.Core.Sync;
using ScanRelay.Core.Updates;

namespace ScanRelay.Core;

public static class ServiceRegistration
{
    /// <summary>Registers everything in Core. The service and the tests both start here.</summary>
    public static IServiceCollection AddScanRelayCore(this IServiceCollection services, InstallConfig install, DataFolder dataFolder)
    {
        services.AddSingleton(install);
        services.AddSingleton(dataFolder);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) })
        {
            Timeout = TimeSpan.FromMinutes(30),
        });

        services.AddDbContextFactory<ScanRelayDbContext>(options =>
            options.UseSqlite($"Data Source={dataFolder.Database}"));

        var protection = services.AddDataProtection()
            .SetApplicationName("ScanRelay")
            .PersistKeysToFileSystem(new DirectoryInfo(dataFolder.Keys));
        if (OperatingSystem.IsWindows())
        {
            protection.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }

        services.AddSingleton<SecretProtector>();
        services.AddSingleton<AuditLog>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<GraphCertificate>();
        services.AddSingleton<AppTokenProvider>();
        services.TryAddSingleton<ICloud, GraphCloud>();
        services.AddSingleton<DeviceCodeFlow>();
        services.AddSingleton<AppRegistration>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<JobPipeline>();
        services.AddSingleton<Retention>();
        services.TryAddSingleton(_ => TrustedKeys.BuiltIn());
        services.AddSingleton<ModuleHost>();
        services.AddSingleton<PrinterService>();
        services.AddSingleton<DirectorySync>();
        services.AddSingleton<ModuleCatalog>();
        services.AddSingleton<UpdateChecker>();
        services.AddSingleton<HealthCheck>();
        services.TryAddSingleton<IInstalledPrinters, InstalledPrinters>();
        return services;
    }

    /// <summary>Creates the data folders and brings the database up to date.</summary>
    public static async Task InitializeScanRelayAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        services.GetRequiredService<DataFolder>().EnsureCreated();
        await using var db = await services.GetRequiredService<IDbContextFactory<ScanRelayDbContext>>().CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}
