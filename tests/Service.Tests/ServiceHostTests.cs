using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace ScanRelay.Service.Tests;

public sealed class ServiceHostTests : IDisposable
{
    private readonly string _dataFolder = Path.Combine(Path.GetTempPath(), "scanrelay-test-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public ServiceHostTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("DataFolder", _dataFolder));
    }

    [Fact]
    public async Task Health_reports_ok_once_the_database_is_ready()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.True(body.RootElement.GetProperty("database").GetBoolean());
    }

    [Fact]
    public async Task Starting_creates_the_data_folder_and_database()
    {
        await _factory.CreateClient().GetAsync("/health");

        Assert.True(Directory.Exists(Path.Combine(_dataFolder, "Inbox")));
        Assert.True(Directory.Exists(Path.Combine(_dataFolder, "Store")));
        Assert.True(File.Exists(Path.Combine(_dataFolder, "scanrelay.db")));
    }

    [Fact]
    public async Task The_admin_screen_is_served_by_the_service()
    {
        var response = await _factory.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<h1>ScanRelay</h1>", html);
        Assert.Contains("_framework/blazor.web.js", html);
    }

    [Fact]
    public async Task The_script_the_admin_screen_needs_is_served()
    {
        var response = await _factory.CreateClient().GetAsync("/_framework/blazor.web.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public void Dispose()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataFolder))
        {
            Directory.Delete(_dataFolder, recursive: true);
        }
    }
}
