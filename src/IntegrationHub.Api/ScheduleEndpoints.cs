using IntegrationHub.Domain;
using IntegrationHub.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IntegrationHub.Api;

public sealed record ScheduleWriteRequest(int IntervalSeconds, bool Enabled);

public static class ScheduleEndpoints
{
    public static void MapScheduleEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/integrations/{id:guid}/schedule");
        api.MapGet("", Get);
        api.MapPut("", Put);
        api.MapDelete("", Delete);
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, HubDbContext db, CancellationToken ct)
    {
        var schedule = await db.IntegrationSchedules.AsNoTracking().FirstOrDefaultAsync(x => x.IntegrationId == id, ct);
        if (schedule is null) return Error(404, "not_found", "Schedule was not found.");
        Etag(http, schedule.RowVersion);
        return Results.Ok(Response(schedule));
    }

    private static async Task<IResult> Put(Guid id, ScheduleWriteRequest body, HttpContext http,
        HubDbContext db, CancellationToken ct)
    {
        if (body.IntervalSeconds is < 300 or > 2592000)
            return Error(422, "invalid_configuration", "Interval must be between 300 and 2592000 seconds.");
        if (!await db.Integrations.AnyAsync(x => x.Id == id, ct))
            return Error(404, "not_found", "Integration was not found.");
        var schedule = await db.IntegrationSchedules.FirstOrDefaultAsync(x => x.IntegrationId == id, ct);
        if (schedule is null)
        {
            schedule = new IntegrationSchedule { IntegrationId = id };
            db.IntegrationSchedules.Add(schedule);
        }
        else if (!Match(http, schedule.RowVersion, out var error)) return error!;
        schedule.IntervalSeconds = body.IntervalSeconds;
        schedule.Enabled = body.Enabled;
        schedule.NextRunAtUtc = DateTimeOffset.UtcNow.AddSeconds(body.IntervalSeconds);
        schedule.RowVersion = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Error(412, "precondition_failed", "Schedule changed since it was read."); }
        Etag(http, schedule.RowVersion);
        return Results.Ok(Response(schedule));
    }

    private static async Task<IResult> Delete(Guid id, HttpContext http, HubDbContext db, CancellationToken ct)
    {
        var schedule = await db.IntegrationSchedules.FirstOrDefaultAsync(x => x.IntegrationId == id, ct);
        if (schedule is null) return Error(404, "not_found", "Schedule was not found.");
        if (!Match(http, schedule.RowVersion, out var error)) return error!;
        schedule.Enabled = false;
        schedule.RowVersion = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Error(412, "precondition_failed", "Schedule changed since it was read."); }
        return Results.NoContent();
    }

    private static object Response(IntegrationSchedule schedule) => new
    {
        schedule.Id, schedule.IntegrationId, schedule.IntervalSeconds, schedule.Enabled,
        schedule.NextRunAtUtc, schedule.LastHandledAtUtc
    };

    private static void Etag(HttpContext http, Guid version) => http.Response.Headers.ETag = $"\"{version:N}\"";

    private static bool Match(HttpContext http, Guid version, out IResult? error)
    {
        var value = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(value))
        {
            error = Error(428, "precondition_required", "Send the ETag from the last GET in If-Match.");
            return false;
        }
        if (value != $"\"{version:N}\"")
        {
            error = Error(412, "precondition_failed", "The schedule changed since it was read.");
            return false;
        }
        error = null;
        return true;
    }

    private static IResult Error(int status, string code, string detail) => Results.Problem(
        statusCode: status, title: code.Replace('_', ' '), detail: detail,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
