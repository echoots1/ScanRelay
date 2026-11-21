using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ScanRelay.Core.Data;

/// <summary>Used only by the migration tooling.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<ScanRelayDbContext>
{
    public ScanRelayDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ScanRelayDbContext>().UseSqlite("Data Source=design.db").Options);
}
