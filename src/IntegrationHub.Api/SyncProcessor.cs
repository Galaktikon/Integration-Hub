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
    IOrderConnectorFactory connectorFactory, RunLease lease, ILogger<SyncProcessor> logger)
{
    public async Task ProcessAsync(Guid runId, string owner, long generation, CancellationToken ct)
    {
        var run = await db.SyncRuns.SingleAsync(x => x.Id == runId, ct);
        var snapshot = JsonSerializer.Deserialize<RunSnapshot>(run.ConfigSnapshotJson, RunEndpoints.Json)
            ?? throw new InvalidOperationException("Run snapshot is missing.");
        var source = connectorFactory.CreateSource(snapshot.Source, secrets.Decrypt(snapshot.SourceEncryptedSecret));
        var destination = connectorFactory.CreateDestination(snapshot.Destination, secrets.Decrypt(snapshot.DestinationEncryptedSecret));
        await lease.MutateAsync(runId, owner, generation, _ =>
        {
            db.SyncRunEvents.Add(new SyncRunEvent { RunId = runId, EventType = "claimed",
                SafeDetailsJson = JsonSerializer.Serialize(new { generation }) });
            return Task.FromResult(true);
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            await lease.RenewAsync(runId, owner, generation, ct);
            var cancel = await lease.MutateAsync(runId, owner, generation,
                state => Task.FromResult(state.CancellationRequested), ct);
            if (cancel)
            {
                await TerminalAsync(run, owner, generation, RunStatuses.Cancelled, null, null, ct);
                return;
            }
            if (run.DeadlineAtUtc is { } deadline && deadline <= DateTimeOffset.UtcNow)
            {
                await TerminalAsync(run, owner, generation, RunStatuses.Failed,
                    "run_deadline_exceeded", "timeout", ct);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var record = await db.SyncRecords.Where(x => x.RunId == runId &&
                    (x.Status == "pending" || x.Status == "processing" ||
                     x.Status == "retry_wait" && x.NextAttemptAtUtc <= now))
                .OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
            if (record is not null)
            {
                if (!await ProcessRecordAsync(run, record, snapshot, destination, owner, generation, ct)) return;
                continue;
            }

            var nextRecord = await db.SyncRecords.Where(x => x.RunId == runId && x.Status == "retry_wait")
                .MinAsync(x => (DateTimeOffset?)x.NextAttemptAtUtc, ct);
            if (nextRecord is { } recordDue && recordDue > now)
            {
                await DeferAsync(run, owner, generation, recordDue, ct);
                return;
            }
            if (run.NextSourceAttemptAtUtc is { } sourceDue && sourceDue > now)
            {
                await DeferAsync(run, owner, generation, sourceDue, ct);
                return;
            }
            if (run.SourceComplete)
            {
                var errors = await db.SyncRecords.AnyAsync(x => x.RunId == runId && x.Status == "failed_permanent", ct);
                await TerminalAsync(run, owner, generation,
                    errors ? RunStatuses.CompletedWithErrors : RunStatuses.Completed, null, null, ct);
                return;
            }
            if (!await FetchPageAsync(run, snapshot, source, owner, generation, ct)) return;
        }
    }

    private async Task<bool> FetchPageAsync(SyncRun run, RunSnapshot snapshot, IOrderSource source,
        string owner, long generation, CancellationToken ct)
    {
        try
        {
            var page = await source.FetchPageAsync(run.NextPageCursor, run.SourceSnapshotToken, ct);
            await lease.RenewAsync(run.Id, owner, generation, ct);
            if (run.SourceSnapshotToken is not null && page.Snapshot != run.SourceSnapshotToken)
                throw new ConnectorException(ConnectorErrorCategory.MalformedResponse, "source_snapshot_changed");
            if (page.Items.Count > snapshot.Source.PageSize || page.Items.Count == 0 && page.NextCursor is not null ||
                page.Items.Any(x => Encoding.UTF8.GetByteCount(x.GetRawText()) > 64 * 1024) ||
                page.Items.Sum(x => Encoding.UTF8.GetByteCount(x.GetRawText())) > 2 * 1024 * 1024)
                throw new ConnectorException(ConnectorErrorCategory.MalformedResponse, "source_page_limit_exceeded");
            await lease.MutateAsync(run.Id, owner, generation, _ =>
            {
                return PersistPageAsync(run, page, ct);
            }, ct);
            logger.LogInformation("Run {RunId} checkpointed {Count} source records", run.Id, page.Items.Count);
            return true;
        }
        catch (ConnectorException ex)
        {
            await HandleSourceFailureAsync(run, snapshot.RetryPolicy, ex, owner, generation, ct);
            return false;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var fresh = await db.SyncRuns.SingleAsync(x => x.Id == run.Id, ct);
            await TerminalAsync(fresh, owner, generation, RunStatuses.Failed,
                "duplicate_source_record_id", "permanent", ct);
            return false;
        }
    }

    private async Task<bool> PersistPageAsync(SyncRun run, CommercePage page, CancellationToken ct)
    {
        var number = await db.SyncPages.CountAsync(x => x.RunId == run.Id, ct) + 1;
        var totalRecords = await db.SyncRecords.CountAsync(x => x.RunId == run.Id, ct);
        if (number > 1000 || totalRecords + page.Items.Count > 50000)
            throw new ConnectorException(ConnectorErrorCategory.MalformedResponse, "run_limit_exceeded");
        var checkpoint = new SyncPage { RunId = run.Id, PageNumber = number,
            RequestCursor = run.NextPageCursor, NextCursor = page.NextCursor, RecordCount = page.Items.Count };
        db.SyncPages.Add(checkpoint);
        for (var i = 0; i < page.Items.Count; i++)
        {
            var item = page.Items[i];
            var id = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idElement) &&
                idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
            var invalidId = string.IsNullOrWhiteSpace(id) || id.Length > 80;
            if (invalidId) id = $"invalid-{number}-{i}";
            var payload = item.GetRawText();
            db.SyncRecords.Add(new SyncRecord { RunId = run.Id, PageId = checkpoint.Id,
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
        run.SourceAttemptCount = 0;
        run.NextSourceAttemptAtUtc = null;
        return true;
    }

    private async Task HandleSourceFailureAsync(SyncRun run, RetryPolicy policy, ConnectorException ex,
        string owner, long generation, CancellationToken ct)
    {
        await lease.MutateAsync(run.Id, owner, generation, state =>
        {
            if (state.CancellationRequested)
            {
                SetTerminal(run, RunStatuses.Cancelled);
                return Task.FromResult(true);
            }
            run.SourceAttemptCount++;
            var due = DateTimeOffset.UtcNow + RetryTiming.Delay(policy, run.SourceAttemptCount,
                ex.RetryAfter, Random.Shared.NextDouble());
            var retry = ex.IsRetryable && run.SourceAttemptCount < policy.MaxAttempts &&
                (run.DeadlineAtUtc is null || due < run.DeadlineAtUtc);
            db.SyncRunEvents.Add(new SyncRunEvent { RunId = run.Id,
                EventType = retry ? "source_retry_scheduled" : "source_failed",
                SafeDetailsJson = JsonSerializer.Serialize(new { ex.Code, category = Category(ex.Category), attempt = run.SourceAttemptCount }) });
            if (retry)
            {
                run.NextSourceAttemptAtUtc = due;
                ReleaseUntil(run, due);
            }
            else SetFailure(run, ex.Code, Category(ex.Category));
            return Task.FromResult(true);
        }, ct);
    }

    private async Task<bool> ProcessRecordAsync(SyncRun run, SyncRecord record, RunSnapshot snapshot,
        IOrderDestination destination, string owner, long generation, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(record.SourcePayloadJson);
        var mapped = OrderMapping.Map(payload.RootElement, snapshot.Mapping, snapshot.Validation);
        var attemptsInGeneration = await db.SyncAttempts.CountAsync(x => x.RecordId == record.Id &&
            x.RecoveryGeneration == run.RecoveryGeneration, ct);
        if (!mapped.Valid || attemptsInGeneration >= snapshot.RetryPolicy.MaxAttempts)
        {
            await lease.MutateAsync(run.Id, owner, generation, _ =>
            {
                MarkUncertain(record);
                if (!mapped.Valid) SetRecordFailure(record, "validation", mapped.Code!, mapped.Message!, false);
                else
                {
                    SetRecordFailure(record, "transient", "attempt_budget_exhausted", "Attempt budget exhausted.", true);
                    SetFailure(run, "attempt_budget_exhausted", "transient");
                }
                return Task.FromResult(true);
            }, ct);
            return mapped.Valid ? false : true;
        }

        var attempt = await lease.MutateAsync(run.Id, owner, generation, state =>
        {
            MarkUncertain(record);
            if (state.CancellationRequested)
            {
                record.Status = "pending";
                SetTerminal(run, RunStatuses.Cancelled);
                return Task.FromResult<SyncAttempt?>(null);
            }
            var started = new SyncAttempt { RecordId = record.Id, AttemptNumber = ++record.AttemptCount,
                RecoveryGeneration = run.RecoveryGeneration };
            record.Status = "processing";
            record.NextAttemptAtUtc = null;
            record.FinishedAtUtc = null;
            db.SyncAttempts.Add(started);
            return Task.FromResult<SyncAttempt?>(started);
        }, ct);
        if (attempt is null) return false;

        var timer = Stopwatch.StartNew();
        try
        {
            var result = await destination.UpsertAsync(run.IntegrationId, record.SourceRecordId, mapped.Order!, ct);
            await lease.MutateAsync(run.Id, owner, generation, async _ =>
            {
                record.Status = result.Outcome;
                record.DestinationRecordId = result.DestinationOrderId.ToString();
                record.ErrorCategory = null;
                record.ErrorCode = null;
                record.SafeErrorMessage = null;
                record.FinishedAtUtc = DateTimeOffset.UtcNow;
                attempt.Outcome = result.Outcome;
                attempt.HttpStatus = result.Outcome == "created" ? 201 : 200;
                FinishAttempt(attempt, timer);
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
                return true;
            }, ct);
            return true;
        }
        catch (ConnectorException ex)
        {
            return await lease.MutateAsync(run.Id, owner, generation, state =>
            {
                attempt.HttpStatus = ex.HttpStatus;
                attempt.ErrorCategory = Category(ex.Category);
                attempt.ErrorCode = ex.Code;
                FinishAttempt(attempt, timer);
                var attemptNumber = attemptsInGeneration + 1;
                var due = DateTimeOffset.UtcNow + RetryTiming.Delay(snapshot.RetryPolicy,
                    attemptNumber, ex.RetryAfter, Random.Shared.NextDouble());
                if (ex.IsRetryable && attemptNumber < snapshot.RetryPolicy.MaxAttempts &&
                    (run.DeadlineAtUtc is null || due < run.DeadlineAtUtc))
                {
                    record.Status = "retry_wait";
                    record.NextAttemptAtUtc = due;
                    record.ErrorCategory = Category(ex.Category);
                    record.ErrorCode = ex.Code;
                    record.SafeErrorMessage = ex.Code.Replace('_', ' ');
                    attempt.Outcome = "retry_wait";
                    db.SyncRunEvents.Add(new SyncRunEvent { RunId = run.Id, EventType = "record_retry_scheduled",
                        SafeDetailsJson = JsonSerializer.Serialize(new { recordId = record.Id, ex.Code, attempt = attemptNumber }) });
                    if (state.CancellationRequested) SetTerminal(run, RunStatuses.Cancelled);
                    return Task.FromResult(!state.CancellationRequested);
                }
                if (ex.IsRetryable)
                {
                    SetRecordFailure(record, Category(ex.Category), ex.Code, ex.Code.Replace('_', ' '), true);
                    SetFailure(run, ex.Code, Category(ex.Category));
                    attempt.Outcome = "failed_retryable";
                    return Task.FromResult(false);
                }
                SetRecordFailure(record, Category(ex.Category), ex.Code, ex.Code.Replace('_', ' '), false);
                attempt.Outcome = "failed_permanent";
                if (ex.Category is ConnectorErrorCategory.Authentication or ConnectorErrorCategory.MalformedResponse)
                {
                    SetFailure(run, ex.Code, Category(ex.Category));
                    return Task.FromResult(false);
                }
                return Task.FromResult(true);
            }, ct);
        }
    }

    private async Task DeferAsync(SyncRun run, string owner, long generation, DateTimeOffset due, CancellationToken ct)
    {
        await lease.MutateAsync(run.Id, owner, generation, state =>
        {
            if (state.CancellationRequested) SetTerminal(run, RunStatuses.Cancelled);
            else ReleaseUntil(run, due);
            return Task.FromResult(true);
        }, ct);
    }

    private async Task TerminalAsync(SyncRun run, string owner, long generation, string status,
        string? code, string? category, CancellationToken ct)
    {
        await lease.MutateAsync(run.Id, owner, generation, _ =>
        {
            if (status == RunStatuses.Failed) SetFailure(run, code!, category!);
            else SetTerminal(run, status);
            return Task.FromResult(true);
        }, ct);
        logger.LogInformation("Run {RunId} ended as {Status}", run.Id, status);
    }

    private void MarkUncertain(SyncRecord record)
    {
        if (record.Status != "processing") return;
        var open = db.SyncAttempts.Local.FirstOrDefault(x => x.RecordId == record.Id && x.Outcome == "started")
            ?? db.SyncAttempts.FirstOrDefault(x => x.RecordId == record.Id && x.Outcome == "started");
        if (open is null) return;
        open.Outcome = "uncertain";
        open.FinishedAtUtc = DateTimeOffset.UtcNow;
    }

    private static void SetRecordFailure(SyncRecord record, string category, string code, string message, bool retryable)
    {
        record.Status = retryable ? "failed_retryable" : "failed_permanent";
        record.ErrorCategory = category;
        record.ErrorCode = code;
        record.SafeErrorMessage = message;
        record.NextAttemptAtUtc = null;
        record.FinishedAtUtc = DateTimeOffset.UtcNow;
    }

    private void SetFailure(SyncRun run, string code, string category)
    {
        run.LastErrorCode = code;
        run.LastErrorCategory = category;
        SetTerminal(run, RunStatuses.Failed);
    }

    private void SetTerminal(SyncRun run, string status)
    {
        run.Status = status;
        run.NextSourceAttemptAtUtc = null;
        run.FinishedAtUtc = DateTimeOffset.UtcNow;
        run.LeaseOwner = null;
        run.LeaseExpiresAtUtc = null;
        db.SyncRunEvents.Add(new SyncRunEvent { RunId = run.Id, EventType = status,
            SafeDetailsJson = JsonSerializer.Serialize(new { run.LastErrorCode, run.LastErrorCategory }) });
    }

    private static void ReleaseUntil(SyncRun run, DateTimeOffset due)
    {
        run.LeaseOwner = null;
        run.LeaseExpiresAtUtc = due;
    }

    private static void FinishAttempt(SyncAttempt attempt, Stopwatch timer)
    {
        timer.Stop();
        attempt.FinishedAtUtc = DateTimeOffset.UtcNow;
        attempt.DurationMs = (int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds);
    }

    private static string Category(ConnectorErrorCategory category) => category.ToString().ToLowerInvariant();
}
