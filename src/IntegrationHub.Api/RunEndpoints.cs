using System.Text;
using System.Text.Json;
using IntegrationHub.Application;
using IntegrationHub.Domain;
using IntegrationHub.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationHub.Api;

public sealed record RunSnapshot(
    ConnectorConfiguration Source, string SourceEncryptedSecret,
    ConnectorConfiguration Destination, string DestinationEncryptedSecret,
    MappingConfiguration Mapping, ValidationConfiguration Validation, RetryPolicy RetryPolicy);

public static class RunEndpoints
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapRunEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        api.MapPost("/integrations/{id:guid}/runs", CreateRun);
        api.MapGet("/integrations/{id:guid}/runs", ListRuns);
        api.MapGet("/runs/{runId:guid}", GetRun);
        api.MapGet("/runs/{runId:guid}/records", GetRecords);
        api.MapGet("/runs/{runId:guid}/records/{recordId:guid}/attempts", GetAttempts);
    }

    private static async Task<IResult> CreateRun(Guid id, HubDbContext db, CancellationToken ct)
    {
        var integration = await db.Integrations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (integration is null) return Error(404, "not_found", "Integration was not found.");
        if (!integration.Enabled) return Error(409, "integration_disabled", "Enable the integration before starting a run.");
        var body = new IntegrationWriteRequest(integration.Name, integration.SourceConnectorId,
            integration.DestinationConnectorId, integration.EntityType,
            JsonSerializer.Deserialize<MappingConfiguration>(integration.MappingJson, Json)!,
            JsonSerializer.Deserialize<ValidationConfiguration>(integration.ValidationJson, Json)!,
            JsonSerializer.Deserialize<RetryPolicy>(integration.RetryPolicyJson, Json)!, true);
        var issues = await ConfigurationEndpoints.IntegrationIssues(body, db, ct);
        if (issues.Count > 0) return Results.Problem(statusCode: 422, title: "Invalid configuration",
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_configuration", ["errors"] = issues });
        var source = await db.Connectors.AsNoTracking().SingleAsync(x => x.Id == integration.SourceConnectorId, ct);
        var destination = await db.Connectors.AsNoTracking().SingleAsync(x => x.Id == integration.DestinationConnectorId, ct);
        if (source.EncryptedSecret is null || destination.EncryptedSecret is null)
            return Error(422, "invalid_configuration", "Both connector API keys are required.");
        var snapshot = new RunSnapshot(
            JsonSerializer.Deserialize<ConnectorConfiguration>(source.ConfigurationJson, Json)!, source.EncryptedSecret,
            JsonSerializer.Deserialize<ConnectorConfiguration>(destination.ConfigurationJson, Json)!, destination.EncryptedSecret,
            body.Mapping, body.Validation, body.RetryPolicy);
        var run = new SyncRun { IntegrationId = id, ConfigSnapshotJson = JsonSerializer.Serialize(snapshot, Json),
            DeadlineAtUtc = DateTimeOffset.UtcNow.AddHours(1) };
        db.SyncRuns.Add(run);
        db.SyncRunEvents.Add(new SyncRunEvent { RunId = run.Id, EventType = "queued", ActorType = "admin" });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        { return Error(409, "active_run_exists", "This integration already has a queued or running run."); }
        return Results.Accepted($"/api/v1/runs/{run.Id}", new
        { runId = run.Id, integrationId = id, status = run.Status, run.TriggerType, run.CreatedAtUtc });
    }

    private static async Task<IResult> ListRuns(Guid id, HttpRequest request, HubDbContext db, CancellationToken ct)
    {
        if (!await db.Integrations.AnyAsync(x => x.Id == id, ct)) return Error(404, "not_found", "Integration was not found.");
        if (!TryPage(request, out var offset, out var size)) return Error(400, "invalid_cursor", "Invalid pagination cursor or page size.");
        var rows = await db.SyncRuns.AsNoTracking().Where(x => x.IntegrationId == id)
            .OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(offset).Take(size + 1).ToListAsync(ct);
        return Results.Ok(new { items = rows.Take(size).Select(RunSummary),
            nextCursor = rows.Count > size ? Cursor(offset + size) : null });
    }

    private static async Task<IResult> GetRun(Guid runId, HubDbContext db, CancellationToken ct)
    {
        var run = await db.SyncRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId, ct);
        if (run is null) return Error(404, "not_found", "Run was not found.");
        var statuses = await db.SyncRecords.AsNoTracking().Where(x => x.RunId == runId)
            .GroupBy(x => x.Status).Select(x => new { Status = x.Key, Count = x.Count() }).ToListAsync(ct);
        int Count(string status) => statuses.FirstOrDefault(x => x.Status == status)?.Count ?? 0;
        return Results.Ok(new
        {
            runId = run.Id, run.IntegrationId, run.Status, run.TriggerType, run.RecoveryGeneration,
            counts = new { fetched = statuses.Sum(x => x.Count), created = Count("created"),
                updated = Count("updated"), unchanged = Count("unchanged"),
                failedPermanent = Count("failed_permanent"), failedRetryable = Count("failed_retryable"),
                pending = Count("pending") + Count("processing") + Count("retry_wait") },
            run.SourceComplete, allowedActions = Array.Empty<string>(), run.CreatedAtUtc, run.StartedAtUtc,
            run.FinishedAtUtc, run.LastErrorCode
        });
    }

    private static async Task<IResult> GetRecords(Guid runId, string? status, HttpRequest request, HubDbContext db, CancellationToken ct)
    {
        if (!await db.SyncRuns.AnyAsync(x => x.Id == runId, ct)) return Error(404, "not_found", "Run was not found.");
        if (!TryPage(request, out var offset, out var size)) return Error(400, "invalid_cursor", "Invalid pagination cursor or page size.");
        var query = db.SyncRecords.AsNoTracking().Where(x => x.RunId == runId);
        if (status is not null) query = query.Where(x => x.Status == status);
        var rows = await query.OrderBy(x => x.Id).Skip(offset).Take(size + 1).ToListAsync(ct);
        return Results.Ok(new { items = rows.Take(size).Select(x => new { recordId = x.Id, x.SourceRecordId, x.Status,
            x.AttemptCount, x.ErrorCategory, x.ErrorCode, x.SafeErrorMessage, x.DestinationRecordId,
            x.FinishedAtUtc }), nextCursor = rows.Count > size ? Cursor(offset + size) : null });
    }

    private static async Task<IResult> GetAttempts(Guid runId, Guid recordId, HubDbContext db, CancellationToken ct)
    {
        if (!await db.SyncRecords.AnyAsync(x => x.RunId == runId && x.Id == recordId, ct))
            return Error(404, "not_found", "Record was not found.");
        var rows = await db.SyncAttempts.AsNoTracking().Where(x => x.RecordId == recordId)
            .OrderBy(x => x.AttemptNumber).ToListAsync(ct);
        return Results.Ok(new { items = rows.Select(x => new { x.AttemptNumber, x.RecoveryGeneration,
            x.Outcome, x.HttpStatus, x.DurationMs, x.ErrorCategory, x.ErrorCode, x.StartedAtUtc, x.FinishedAtUtc }) });
    }

    private static object RunSummary(SyncRun x) => new
    { runId = x.Id, x.IntegrationId, x.Status, x.TriggerType, x.CreatedAtUtc, x.StartedAtUtc, x.FinishedAtUtc };
    private static bool TryPage(HttpRequest request, out int offset, out int size)
    {
        offset = 0;
        size = 25;
        if (request.Query.TryGetValue("pageSize", out var rawSize) &&
            (!int.TryParse(rawSize, out size) || size is < 1 or > 100)) return false;
        if (!request.Query.TryGetValue("cursor", out var rawCursor)) return true;
        try { return int.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(rawCursor.ToString())), out offset) && offset >= 0; }
        catch (FormatException) { return false; }
    }
    private static string Cursor(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString()));
    private static IResult Error(int status, string code, string detail) => Results.Problem(
        statusCode: status, title: code.Replace('_', ' '), detail: detail,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
