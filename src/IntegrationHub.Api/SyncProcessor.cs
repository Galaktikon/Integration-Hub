using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntegrationHub.Application;
using IntegrationHub.Domain;
using IntegrationHub.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IntegrationHub.Api;

public sealed class SyncProcessor(HubDbContext db, SecretProtector secrets,
    IOrderConnectorFactory connectorFactory, ILogger<SyncProcessor> logger)
{
    public async Task ProcessAsync(Guid runId, string owner, long generation, CancellationToken ct)
    {
        var run = await db.SyncRuns.SingleAsync(x => x.Id == runId, ct);
        var snapshot = JsonSerializer.Deserialize<RunSnapshot>(run.ConfigSnapshotJson, RunEndpoints.Json)
            ?? throw new InvalidOperationException("Run snapshot is missing.");
        IOrderSource source = connectorFactory.CreateSource(snapshot.Source, secrets.Decrypt(snapshot.SourceEncryptedSecret));
        IOrderDestination destination = connectorFactory.CreateDestination(snapshot.Destination,
            secrets.Decrypt(snapshot.DestinationEncryptedSecret));
        db.SyncRunEvents.Add(new SyncRunEvent { RunId = runId, EventType = "started" });
        await db.SaveChangesAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            await RenewAsync(runId, owner, generation, ct);
            if (run.DeadlineAtUtc <= DateTimeOffset.UtcNow)
            { await FailRunAsync(run, "run_deadline_exceeded", ct); return; }
            var record = await db.SyncRecords.Where(x => x.RunId == runId &&
                    (x.Status == "pending" || x.Status == "processing"))
                .OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
            if (record is not null)
            {
                var continueRun = await ProcessRecordAsync(run, record, snapshot, destination, owner, generation, ct);
                if (!continueRun) return;
                continue;
            }
            if (run.SourceComplete)
            {
                var permanent = await db.SyncRecords.AnyAsync(x => x.RunId == runId && x.Status == "failed_permanent", ct);
                run.Status = permanent ? RunStatuses.CompletedWithErrors : RunStatuses.Completed;
                run.FinishedAtUtc = DateTimeOffset.UtcNow;
                run.LeaseOwner = null;
                run.LeaseExpiresAtUtc = null;
                db.SyncRunEvents.Add(new SyncRunEvent { RunId = runId, EventType = run.Status });
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Run {RunId} finished as {Status}", runId, run.Status);
                return;
            }
            try
            {
                var page = await source.FetchPageAsync(run.NextPageCursor, run.SourceSnapshotToken, ct);
                await RenewAsync(runId, owner, generation, ct);
                if (run.SourceSnapshotToken is not null && page.Snapshot != run.SourceSnapshotToken)
                    throw new ConnectorException(ConnectorErrorCategory.MalformedResponse, "source_snapshot_changed");
                var pageNumber = await db.SyncPages.CountAsync(x => x.RunId == runId, ct) + 1;
                var checkpoint = new SyncPage { RunId = runId, PageNumber = pageNumber,
                    RequestCursor = run.NextPageCursor, NextCursor = page.NextCursor, RecordCount = page.Items.Count };
                db.SyncPages.Add(checkpoint);
                for (var i = 0; i < page.Items.Count; i++)
                {
                    var item = page.Items[i];
                    var id = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idElement) &&
                        idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
                    var invalidId = string.IsNullOrWhiteSpace(id) || id.Length > 80;
                    if (invalidId) id = $"invalid-{pageNumber}-{i}";
                    var payload = item.GetRawText();
                    db.SyncRecords.Add(new SyncRecord { RunId = runId, PageId = checkpoint.Id,
                        SourceRecordId = id!, SourcePayloadJson = payload,
                        SourceVersionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
                        IdempotencyKey = $"{run.IntegrationId:N}:{id}",
                        Status = invalidId ? "failed_permanent" : "pending",
                        ErrorCategory = invalidId ? "validation" : null,
                        ErrorCode = invalidId ? "invalid_source_id" : null,
                        SafeErrorMessage = invalidId ? "Source record ID is missing or invalid." : null,
                        FinishedAtUtc = invalidId ? DateTimeOffset.UtcNow : null });
                }
                run.SourceSnapshotToken ??= page.Snapshot;
                run.NextPageCursor = page.NextCursor;
                run.SourceComplete = page.NextCursor is null;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Run {RunId} checkpointed page {PageNumber} with {Count} records", runId, pageNumber, page.Items.Count);
            }
            catch (ConnectorException ex)
            {
                await FailRunAsync(run, ex.Code, ct);
                return;
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                var freshRun = await db.SyncRuns.SingleAsync(x => x.Id == runId, ct);
                await FailRunAsync(freshRun, "duplicate_source_record_id", ct);
                return;
            }
        }
    }

    private async Task<bool> ProcessRecordAsync(SyncRun run, SyncRecord record, RunSnapshot snapshot,
        IOrderDestination destination, string owner, long generation, CancellationToken ct)
    {
        if (record.Status == "processing")
        {
            var openAttempt = await db.SyncAttempts.Where(x => x.RecordId == record.Id && x.Outcome == "started")
                .OrderByDescending(x => x.AttemptNumber).FirstOrDefaultAsync(ct);
            if (openAttempt is not null)
            {
                openAttempt.Outcome = "uncertain";
                openAttempt.FinishedAtUtc = DateTimeOffset.UtcNow;
            }
        }
        using var payload = JsonDocument.Parse(record.SourcePayloadJson);
        var mapped = OrderMapping.Map(payload.RootElement, snapshot.Mapping, snapshot.Validation);
        if (!mapped.Valid)
        {
            record.Status = "failed_permanent";
            record.ErrorCategory = "validation";
            record.ErrorCode = mapped.Code;
            record.SafeErrorMessage = mapped.Message;
            record.FinishedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return true;
        }
        var attempt = new SyncAttempt { RecordId = record.Id, AttemptNumber = ++record.AttemptCount,
            RecoveryGeneration = run.RecoveryGeneration };
        record.Status = "processing";
        db.SyncAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        var timer = Stopwatch.StartNew();
        try
        {
            var result = await destination.UpsertAsync(run.IntegrationId, record.SourceRecordId, mapped.Order!, ct);
            await RenewAsync(run.Id, owner, generation, ct);
            record.Status = result.Outcome;
            record.DestinationRecordId = result.DestinationOrderId.ToString();
            record.ErrorCategory = null;
            record.ErrorCode = null;
            record.SafeErrorMessage = null;
            record.FinishedAtUtc = DateTimeOffset.UtcNow;
            attempt.Outcome = result.Outcome;
            attempt.HttpStatus = result.Outcome == "created" ? 201 : 200;
            var identity = await db.DestinationIdentities.FirstOrDefaultAsync(x => x.IntegrationId == run.IntegrationId &&
                x.SourceRecordId == record.SourceRecordId, ct);
            if (identity is null) db.DestinationIdentities.Add(new DestinationIdentity
            { IntegrationId = run.IntegrationId, SourceRecordId = record.SourceRecordId,
                DestinationRecordId = record.DestinationRecordId, LastSourceHash = record.SourceVersionHash });
            else
            {
                identity.DestinationRecordId = record.DestinationRecordId;
                identity.LastSourceHash = record.SourceVersionHash;
                identity.LastSyncedAtUtc = DateTimeOffset.UtcNow;
            }
        }
        catch (ConnectorException ex)
        {
            record.Status = ex.IsRetryable || ex.Category is ConnectorErrorCategory.Authentication or ConnectorErrorCategory.MalformedResponse
                ? "failed_retryable" : "failed_permanent";
            record.ErrorCategory = ex.Category.ToString().ToLowerInvariant();
            record.ErrorCode = ex.Code;
            record.SafeErrorMessage = ex.Code.Replace('_', ' ');
            record.FinishedAtUtc = DateTimeOffset.UtcNow;
            attempt.Outcome = record.Status;
            attempt.HttpStatus = ex.HttpStatus;
            attempt.ErrorCategory = record.ErrorCategory;
            attempt.ErrorCode = ex.Code;
        }
        finally
        {
            timer.Stop();
            attempt.FinishedAtUtc = DateTimeOffset.UtcNow;
            attempt.DurationMs = (int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds);
        }
        await db.SaveChangesAsync(ct);
        if (record.Status == "failed_retryable")
        {
            await FailRunAsync(run, record.ErrorCode ?? "destination_failed", ct);
            return false;
        }
        return true;
    }

    private async Task RenewAsync(Guid runId, string owner, long generation, CancellationToken ct)
    {
        var updated = await db.SyncRuns.Where(x => x.Id == runId && x.Status == RunStatuses.Running &&
                x.LeaseOwner == owner && x.LeaseGeneration == generation)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(2)), ct);
        if (updated != 1) throw new InvalidOperationException("Run lease was lost.");
    }

    private async Task FailRunAsync(SyncRun run, string code, CancellationToken ct)
    {
        run.Status = RunStatuses.Failed;
        run.LastErrorCode = code;
        run.FinishedAtUtc = DateTimeOffset.UtcNow;
        run.LeaseOwner = null;
        run.LeaseExpiresAtUtc = null;
        db.SyncRunEvents.Add(new SyncRunEvent { RunId = run.Id, EventType = "failed",
            SafeDetailsJson = JsonSerializer.Serialize(new { code }) });
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Run {RunId} failed with {Code}", run.Id, code);
    }
}
