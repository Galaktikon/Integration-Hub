using IntegrationHub.Domain;
using IntegrationHub.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IntegrationHub.Api;

public sealed class WorkerHost(IServiceScopeFactory scopes, ILogger<WorkerHost> logger) : BackgroundService
{
    private readonly string owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Sync worker started as {Owner}", owner);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
                var now = DateTimeOffset.UtcNow;
                var candidate = await db.SyncRuns.AsNoTracking()
                    .Where(x => x.Status == RunStatuses.Queued || x.Status == RunStatuses.Running && x.LeaseExpiresAtUtc < now)
                    .OrderBy(x => x.CreatedAtUtc).Select(x => new { x.Id }).FirstOrDefaultAsync(stoppingToken);
                if (candidate is not null)
                {
                    var claimed = await db.SyncRuns.Where(x => x.Id == candidate.Id &&
                            (x.Status == RunStatuses.Queued || x.Status == RunStatuses.Running && x.LeaseExpiresAtUtc < now))
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.Status, RunStatuses.Running)
                            .SetProperty(x => x.LeaseOwner, owner)
                            .SetProperty(x => x.LeaseGeneration, x => x.LeaseGeneration + 1)
                            .SetProperty(x => x.LeaseExpiresAtUtc, now.AddMinutes(2))
                            .SetProperty(x => x.StartedAtUtc, x => x.StartedAtUtc ?? now), stoppingToken);
                    if (claimed == 1)
                    {
                        var run = await db.SyncRuns.SingleAsync(x => x.Id == candidate.Id, stoppingToken);
                        logger.LogInformation("Claimed run {RunId} generation {Generation}", run.Id, run.LeaseGeneration);
                        await scope.ServiceProvider.GetRequiredService<SyncProcessor>()
                            .ProcessAsync(run.Id, owner, run.LeaseGeneration, stoppingToken);
                        continue;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sync worker iteration failed; durable work will be reconsidered after its lease expires.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        logger.LogInformation("Sync worker stopped");
    }
}
