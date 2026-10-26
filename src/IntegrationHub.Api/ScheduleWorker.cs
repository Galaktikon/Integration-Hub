using System.Data;
using System.Text.Json;
using IntegrationHub.Domain;
using IntegrationHub.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace IntegrationHub.Api;

public sealed class ScheduleWorker(IServiceScopeFactory scopes, ILogger<ScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Schedule polling failed; due schedules remain persisted.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    public async Task ProcessDueAsync(CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using var command = new NpgsqlCommand("""
                SELECT "Id" FROM hub.integration_schedule
                WHERE "Enabled" = true AND "NextRunAtUtc" <= @now
                ORDER BY "NextRunAtUtc" LIMIT 1 FOR UPDATE SKIP LOCKED
                """, connection, (NpgsqlTransaction)transaction.GetDbTransaction());
            command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            var selected = await command.ExecuteScalarAsync(ct);
            if (selected is not Guid id) return;
            var schedule = await db.IntegrationSchedules.SingleAsync(x => x.Id == id, ct);
            var now = DateTimeOffset.UtcNow;
            var due = schedule.NextRunAtUtc;
            var steps = Math.Floor((now - due).TotalSeconds / schedule.IntervalSeconds) + 1;
            schedule.NextRunAtUtc = due.AddSeconds(steps * schedule.IntervalSeconds);
            schedule.LastHandledAtUtc = now;
            schedule.RowVersion = Guid.NewGuid();
            var integration = await db.Integrations.SingleAsync(x => x.Id == schedule.IntegrationId, ct);
            var outcome = "skipped_disabled";
            SyncRun? run = null;
            if (integration.Enabled)
            {
                var active = await db.SyncRuns.AnyAsync(x => x.IntegrationId == integration.Id &&
                    (x.Status == RunStatuses.Queued || x.Status == RunStatuses.Running), ct);
                outcome = active ? "skipped_overlap" : "created";
                if (!active)
                {
                    var source = await db.Connectors.AsNoTracking().SingleAsync(x => x.Id == integration.SourceConnectorId, ct);
                    var destination = await db.Connectors.AsNoTracking().SingleAsync(x => x.Id == integration.DestinationConnectorId, ct);
                    if (!source.Enabled || !destination.Enabled || source.EncryptedSecret is null || destination.EncryptedSecret is null)
                        outcome = "skipped_configuration";
                    else
                    {
                        run = new SyncRun { IntegrationId = integration.Id, TriggerType = "scheduled",
                            ConfigSnapshotJson = JsonSerializer.Serialize(RunEndpoints.Snapshot(integration, source, destination), RunEndpoints.Json),
                            DeadlineAtUtc = now.AddHours(1) };
                        db.SyncRuns.Add(run);
                        db.SyncRunEvents.Add(new SyncRunEvent { RunId = run.Id, ActorType = "scheduler", EventType = "queued" });
                    }
                }
            }
            db.ScheduleEvents.Add(new ScheduleEvent { ScheduleId = id, DueAtUtc = due,
                Outcome = outcome, RunId = run?.Id });
            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                logger.LogInformation("Schedule {ScheduleId} occurrence {DueAtUtc}: {Outcome}", id, due, outcome);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                // A concurrent manual request won the active-run constraint. The occurrence remains due
                // and will be recorded as skipped_overlap on the next poll.
                await transaction.RollbackAsync(ct);
            }
        }
    }
}
