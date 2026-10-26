using System.Data;
using IntegrationHub.Domain;
using IntegrationHub.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace IntegrationHub.Api;

public sealed class LeaseLostException : Exception;
public sealed record LeaseState(bool CancellationRequested);

public sealed class RunLease(HubDbContext db)
{
    public async Task<T> MutateAsync<T>(Guid runId, string owner, long generation,
        Func<LeaseState, Task<T>> change, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var dbTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();
        await using var command = new NpgsqlCommand("""
            SELECT "LeaseOwner", "LeaseGeneration", "Status", "LeaseExpiresAtUtc", "CancellationRequestedAtUtc"
            FROM hub.sync_run WHERE "Id" = @id FOR UPDATE
            """, connection, dbTransaction);
        command.Parameters.AddWithValue("id", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new LeaseLostException();
        var valid = !reader.IsDBNull(0) && reader.GetString(0) == owner &&
            reader.GetInt64(1) == generation && reader.GetString(2) == RunStatuses.Running &&
            !reader.IsDBNull(3) && reader.GetFieldValue<DateTimeOffset>(3) > DateTimeOffset.UtcNow;
        var cancellationRequested = !reader.IsDBNull(4);
        await reader.DisposeAsync();
        if (!valid) throw new LeaseLostException();

        var result = await change(new LeaseState(cancellationRequested));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task RenewAsync(Guid runId, string owner, long generation, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = await db.SyncRuns.Where(x => x.Id == runId && x.Status == RunStatuses.Running &&
                x.LeaseOwner == owner && x.LeaseGeneration == generation && x.LeaseExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LeaseExpiresAtUtc, now.AddMinutes(2)), ct);
        if (updated != 1) throw new LeaseLostException();
    }
}
