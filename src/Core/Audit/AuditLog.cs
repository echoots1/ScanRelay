using Microsoft.EntityFrameworkCore;
using ScanRelay.Core.Data;

namespace ScanRelay.Core.Audit;

/// <summary>Who is making a change. Setup and the service itself have no user id.</summary>
public sealed record Actor(Guid? Id, string Name)
{
    public static readonly Actor Setup = new(null, "Setup");
    public static readonly Actor Service = new(null, "Service");
}

public sealed class AuditLog(IDbContextFactory<ScanRelayDbContext> factory, TimeProvider time)
{
    public async Task WriteAsync(Actor actor, string action, string? target = null, string? detail = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AuditLog.Add(new AuditEntry
        {
            AtUtc = time.GetUtcNow().UtcDateTime,
            ActorId = actor.Id,
            ActorName = actor.Name,
            Action = action,
            Target = target,
            Detail = detail,
        });
        await db.SaveChangesAsync(ct);
    }
}
