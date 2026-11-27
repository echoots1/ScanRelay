using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ScanRelay.MockDevices;

/// <summary>How every mock device listens: on this machine only, quietly, on a port of its own.</summary>
internal static class MockHost
{
    /// <summary>A web application not yet started. Port 0 lets the system choose a free one.</summary>
    public static WebApplication Create(int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        return builder.Build();
    }

    /// <summary>Starts it and says which port it got.</summary>
    public static async Task<int> StartAsync(WebApplication app)
    {
        await app.StartAsync();
        return new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
    }
}
