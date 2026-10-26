using System.Text;
using System.Text.Json;
using IntegrationHub.Application;
using IntegrationHub.Domain;
using IntegrationHub.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IntegrationHub.Api;

public static class ConfigurationEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapConfigurationEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        api.MapGet("/connectors", ListConnectors);
        api.MapPost("/connectors", CreateConnector);
        api.MapGet("/connectors/{id:guid}", GetConnector);
        api.MapPut("/connectors/{id:guid}", UpdateConnector);
        api.MapDelete("/connectors/{id:guid}", DeleteConnector);
        api.MapGet("/integrations", ListIntegrations);
        api.MapPost("/integrations", CreateIntegration);
        api.MapGet("/integrations/{id:guid}", GetIntegration);
        api.MapPut("/integrations/{id:guid}", UpdateIntegration);
        api.MapDelete("/integrations/{id:guid}", DeleteIntegration);
        api.MapPost("/integrations/{id:guid}/validate", ValidateIntegration);
    }

    private static async Task<IResult> ListConnectors(HttpRequest request, HubDbContext db, CancellationToken ct)
    {
        if (!TryPage(request, out var offset, out var size)) return Error(400, "invalid_cursor", "Invalid pagination cursor or page size.");
        var rows = await db.Connectors.AsNoTracking().OrderBy(x => x.Id).Skip(offset).Take(size + 1).ToListAsync(ct);
        return Results.Ok(new { items = rows.Take(size).Select(ToResponse), nextCursor = rows.Count > size ? Cursor(offset + size) : null });
    }

    private static async Task<IResult> GetConnector(Guid id, HttpContext http, HubDbContext db, CancellationToken ct)
    {
        var row = await db.Connectors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return Error(404, "not_found", "Connector was not found.");
        SetEtag(http, row.RowVersion);
        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> CreateConnector(
        ConnectorWriteRequest body, HttpContext http, HubDbContext db, SecretProtector secrets,
        IConfiguration config, IHostEnvironment env, CancellationToken ct)
    {
        var issues = ConfigurationRules.ValidateConnector(body, AllowedHosts(config), !env.IsDevelopment()).ToList();
        if (string.IsNullOrWhiteSpace(body.Secret))
            issues.Add(new("secret", "required", "An API key is required for a new connector."));
        if (issues.Count > 0) return Invalid(issues);

        var row = new Connector
        {
            Name = body.Name.Trim(), Kind = body.Kind, Role = body.Role,
            ConfigurationJson = JsonSerializer.Serialize(body.Configuration, Json),
            EncryptedSecret = secrets.Encrypt(body.Secret!), Enabled = body.Enabled
        };
        db.Connectors.Add(row);
        await db.SaveChangesAsync(ct);
        SetEtag(http, row.RowVersion);
        return Results.Created($"/api/v1/connectors/{row.Id}", ToResponse(row));
    }

    private static async Task<IResult> UpdateConnector(
        Guid id, ConnectorWriteRequest body, HttpContext http, HubDbContext db,
        SecretProtector secrets, IConfiguration config, IHostEnvironment env, CancellationToken ct)
    {
        var row = await db.Connectors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return Error(404, "not_found", "Connector was not found.");
        if (!HasMatchingEtag(http, row.RowVersion, out var etagError)) return etagError!;
        var issues = ConfigurationRules.ValidateConnector(body, AllowedHosts(config), !env.IsDevelopment()).ToList();
        if (issues.Count > 0) return Invalid(issues);
        if (await db.Integrations.AnyAsync(x => (x.SourceConnectorId == id || x.DestinationConnectorId == id) && x.Enabled, ct) &&
            (row.Role != body.Role || !body.Enabled))
            return Error(409, "connector_in_use", "Disable dependent integrations before changing role or disabling this connector.");

        row.Name = body.Name.Trim();
        row.Kind = body.Kind;
        row.Role = body.Role;
        row.ConfigurationJson = JsonSerializer.Serialize(body.Configuration, Json);
        if (body.Secret is not null) row.EncryptedSecret = secrets.Encrypt(body.Secret);
        row.Enabled = body.Enabled;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        row.RowVersion = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Error(412, "precondition_failed", "The connector changed since it was read."); }
        SetEtag(http, row.RowVersion);
        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> DeleteConnector(Guid id, HttpContext http, HubDbContext db, CancellationToken ct)
    {
        var row = await db.Connectors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return Error(404, "not_found", "Connector was not found.");
        if (!HasMatchingEtag(http, row.RowVersion, out var etagError)) return etagError!;
        if (await db.Integrations.AnyAsync(x => x.SourceConnectorId == id || x.DestinationConnectorId == id, ct))
            return Error(409, "connector_in_use", "Remove dependent integrations before deleting this connector.");
        db.Connectors.Remove(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Error(412, "precondition_failed", "The connector changed since it was read."); }
        catch (DbUpdateException) { return Error(409, "connector_in_use", "A dependent integration was added before deletion."); }
        return Results.NoContent();
    }

    private static async Task<IResult> ListIntegrations(HttpRequest request, HubDbContext db, CancellationToken ct)
    {
        if (!TryPage(request, out var offset, out var size)) return Error(400, "invalid_cursor", "Invalid pagination cursor or page size.");
        var rows = await db.Integrations.AsNoTracking().OrderBy(x => x.Id).Skip(offset).Take(size + 1).ToListAsync(ct);
        return Results.Ok(new { items = rows.Take(size).Select(ToResponse), nextCursor = rows.Count > size ? Cursor(offset + size) : null });
    }

    private static async Task<IResult> GetIntegration(Guid id, HttpContext http, HubDbContext db, CancellationToken ct)
    {
        var row = await db.Integrations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return Error(404, "not_found", "Integration was not found.");
        SetEtag(http, row.RowVersion);
        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> CreateIntegration(
        IntegrationWriteRequest body, HttpContext http, HubDbContext db, CancellationToken ct)
    {
        var issues = await IntegrationIssues(body, db, ct);
        if (issues.Count > 0) return Invalid(issues);
        var row = new Integration();
        Apply(row, body);
        db.Integrations.Add(row);
        await db.SaveChangesAsync(ct);
        SetEtag(http, row.RowVersion);
        return Results.Created($"/api/v1/integrations/{row.Id}", ToResponse(row));
    }

    private static async Task<IResult> UpdateIntegration(
        Guid id, IntegrationWriteRequest body, HttpContext http, HubDbContext db, CancellationToken ct)
    {
        var row = await db.Integrations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return Error(404, "not_found", "Integration was not found.");
        if (!HasMatchingEtag(http, row.RowVersion, out var etagError)) return etagError!;
        var issues = await IntegrationIssues(body, db, ct);
        if (issues.Count > 0) return Invalid(issues);
        Apply(row, body);
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        row.RowVersion = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Error(412, "precondition_failed", "The integration changed since it was read."); }
        SetEtag(http, row.RowVersion);
        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> DeleteIntegration(Guid id, HttpContext http, HubDbContext db, CancellationToken ct)
    {
        var row = await db.Integrations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return Error(404, "not_found", "Integration was not found.");
        if (!HasMatchingEtag(http, row.RowVersion, out var etagError)) return etagError!;
        if (await db.SyncRuns.AnyAsync(x => x.IntegrationId == id, ct))
            return Error(409, "integration_has_history", "Integrations with run history cannot be deleted.");
        db.Integrations.Remove(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Error(412, "precondition_failed", "The integration changed since it was read."); }
        catch (DbUpdateException) { return Error(409, "integration_has_history", "Run history was added before deletion."); }
        return Results.NoContent();
    }

    private static async Task<IResult> ValidateIntegration(Guid id, HubDbContext db, CancellationToken ct)
    {
        var row = await db.Integrations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return Error(404, "not_found", "Integration was not found.");
        var issues = await IntegrationIssues(new IntegrationWriteRequest(
            row.Name, row.SourceConnectorId, row.DestinationConnectorId, row.EntityType,
            JsonSerializer.Deserialize<MappingConfiguration>(row.MappingJson, Json)!,
            JsonSerializer.Deserialize<ValidationConfiguration>(row.ValidationJson, Json)!,
            JsonSerializer.Deserialize<RetryPolicy>(row.RetryPolicyJson, Json)!, row.Enabled), db, ct);
        return Results.Ok(new { valid = issues.Count == 0, issues });
    }

    internal static async Task<List<ValidationIssue>> IntegrationIssues(
        IntegrationWriteRequest body, HubDbContext db, CancellationToken ct)
    {
        var issues = ConfigurationRules.ValidateIntegration(body).ToList();
        if (body.SourceConnectorId == Guid.Empty || body.DestinationConnectorId == Guid.Empty) return issues;
        var connectors = await db.Connectors.AsNoTracking()
            .Where(x => x.Id == body.SourceConnectorId || x.Id == body.DestinationConnectorId).ToListAsync(ct);
        var source = connectors.FirstOrDefault(x => x.Id == body.SourceConnectorId);
        var destination = connectors.FirstOrDefault(x => x.Id == body.DestinationConnectorId);
        if (source?.Role != ConnectorRoles.Source)
            issues.Add(new("sourceConnectorId", "invalid_source", "Source connector is missing or has the wrong role."));
        if (destination?.Role != ConnectorRoles.Destination)
            issues.Add(new("destinationConnectorId", "invalid_destination", "Destination connector is missing or has the wrong role."));
        if (body.Enabled && (source?.Enabled != true || destination?.Enabled != true))
            issues.Add(new("enabled", "connector_disabled", "Both connectors must be enabled."));
        return issues;
    }

    private static void Apply(Integration row, IntegrationWriteRequest body)
    {
        row.Name = body.Name.Trim();
        row.SourceConnectorId = body.SourceConnectorId;
        row.DestinationConnectorId = body.DestinationConnectorId;
        row.EntityType = body.EntityType;
        row.MappingJson = JsonSerializer.Serialize(body.Mapping, Json);
        row.ValidationJson = JsonSerializer.Serialize(body.Validation, Json);
        row.RetryPolicyJson = JsonSerializer.Serialize(body.RetryPolicy, Json);
        row.Enabled = body.Enabled;
    }

    private static object ToResponse(Connector row) => new
    {
        row.Id, row.Name, row.Kind, row.Role,
        configuration = JsonSerializer.Deserialize<ConnectorConfiguration>(row.ConfigurationJson, Json),
        secretSet = row.EncryptedSecret is not null,
        row.Enabled, row.CreatedAtUtc, row.UpdatedAtUtc
    };

    private static object ToResponse(Integration row) => new
    {
        row.Id, row.Name, row.SourceConnectorId, row.DestinationConnectorId, row.EntityType,
        mapping = JsonSerializer.Deserialize<MappingConfiguration>(row.MappingJson, Json),
        validation = JsonSerializer.Deserialize<ValidationConfiguration>(row.ValidationJson, Json),
        retryPolicy = JsonSerializer.Deserialize<RetryPolicy>(row.RetryPolicyJson, Json),
        row.Enabled, row.CreatedAtUtc, row.UpdatedAtUtc
    };

    private static IReadOnlySet<string> AllowedHosts(IConfiguration config) =>
        new HashSet<string>((config["Security:AllowedConnectorHosts"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    private static void SetEtag(HttpContext http, Guid version) => http.Response.Headers.ETag = $"\"{version:N}\"";

    private static bool HasMatchingEtag(HttpContext http, Guid version, out IResult? error)
    {
        var value = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = Error(428, "precondition_required", "Send the ETag from the last GET in If-Match.");
            return false;
        }
        if (value != $"\"{version:N}\"")
        {
            error = Error(412, "precondition_failed", "The resource changed since it was read.");
            return false;
        }
        error = null;
        return true;
    }

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

    private static IResult Invalid(IReadOnlyList<ValidationIssue> issues) =>
        Results.Problem(statusCode: 422, title: "Invalid configuration", detail: "Correct the listed fields.",
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_configuration", ["errors"] = issues });

    private static IResult Error(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code.Replace('_', ' '), detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
