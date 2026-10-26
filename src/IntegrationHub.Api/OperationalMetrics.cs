using IntegrationHub.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IntegrationHub.Api;

public static class OperationalMetrics
{
    public static void MapOperationalMetrics(this WebApplication app)
    {
        app.MapGet("/api/v1/metrics", async (HubDbContext db, CancellationToken ct) =>
        {
            var runs = await db.SyncRuns.AsNoTracking().GroupBy(x => x.Status)
                .Select(group => new { Status = group.Key, Count = group.Count() })
                .ToDictionaryAsync(x => x.Status, x => x.Count, ct);
            var retryEvents = await db.SyncRunEvents.AsNoTracking().CountAsync(x =>
                x.EventType == "source_retry_scheduled" || x.EventType == "record_retry_scheduled" ||
                x.EventType == "retry_requested", ct);
            var uncertainAttempts = await db.SyncAttempts.AsNoTracking()
                .CountAsync(x => x.Outcome == "uncertain", ct);
            return Results.Ok(new
            {
                observedAtUtc = DateTimeOffset.UtcNow,
                runs,
                retryEvents,
                uncertainAttempts
            });
        });
    }
}
