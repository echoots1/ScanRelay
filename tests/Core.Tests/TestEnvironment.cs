using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ScanRelay.Core.Audit;
using ScanRelay.Core.Cloud;
using ScanRelay.Core.Install;
using ScanRelay.Core.Settings;
using ScanRelay.MockCloud;

namespace ScanRelay.Core.Tests;

/// <summary>
/// The real Core services, with a temporary data folder, pointed at a mock Microsoft 365.
/// </summary>
public sealed class TestEnvironment : IAsyncDisposable
{
    private TestEnvironment(MockCloudServer cloud, ServiceProvider services, InstallConfig install, DataFolder data)
    {
        Cloud = cloud;
        Services = services;
        Install = install;
        Data = data;
    }

    public MockCloudServer Cloud { get; }
    public MockCloudState State => Cloud.State;
    public ServiceProvider Services { get; }
    public InstallConfig Install { get; }
    public DataFolder Data { get; }
    public SettingsStore Settings => Services.GetRequiredService<SettingsStore>();
    public ICloud Graph => Services.GetRequiredService<ICloud>();
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public static async Task<TestEnvironment> StartAsync(Action<IServiceCollection>? configure = null, TimeProvider? time = null, Action<InstallConfig>? prepare = null)
    {
        var cloud = await MockCloudServer.StartAsync();
        var data = new DataFolder(Path.Combine(Path.GetTempPath(), "scanrelay-test-" + Guid.NewGuid().ToString("N")));
        data.EnsureCreated();
        var install = NewInstallConfig(cloud, data);
        prepare?.Invoke(install);
        var services = new ServiceCollection();
        services.AddLogging();
        if (time is not null) services.AddSingleton(time);
        services.AddScanRelayCore(install, data);
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider();
        await provider.InitializeScanRelayAsync();
        return new TestEnvironment(cloud, provider, install, data);
    }

    public static InstallConfig NewInstallConfig(MockCloudServer cloud, DataFolder data) => new()
    {
        DataFolder = data.Root,
        Url = "https://localhost:8443",
        PrinterAccount = "scanrelay-printer",
        ShareName = "Scans$",
        ServerName = "scanserver",
        Cloud = new CloudEndpoints
        {
            Login = cloud.LoginUrl,
            Graph = cloud.GraphUrl,
            GitHubApi = cloud.BaseUrl + "/github-api",
            GitHubDownload = cloud.BaseUrl + "/github",
        },
    };

    /// <summary>Runs the whole of setup: registration, consent, and saving the connection.</summary>
    public async Task<MockApplication> ConnectAsync(bool consent = true)
    {
        var flow = Get<DeviceCodeFlow>();
        var started = await flow.StartAsync(null, TimeProvider.System);
        var token = (await flow.PollAsync(null, started.DeviceCode)).AccessToken!;
        var result = await Get<AppRegistration>().CreateOrRepairAsync(token);
        await Settings.UpdateAsync(s => s with { TenantId = result.TenantId, ClientId = result.ClientId }, Actor.Setup);
        var app = State.Applications.Values.Single(a => a.AppId == result.ClientId);
        app.ConsentGranted = consent;
        Get<AppTokenProvider>().Reset();
        return app;
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        await Cloud.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Data.Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
