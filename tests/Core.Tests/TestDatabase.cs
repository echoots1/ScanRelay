using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ScanRelay.Core.Data;

namespace ScanRelay.Core.Tests;

/// <summary>A migrated in-memory database that lives as long as the test.</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public TestDatabase()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.Migrate();
    }

    public ScanRelayDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ScanRelayDbContext>().UseSqlite(_connection).Options);

    public void Dispose() => _connection.Dispose();
}
