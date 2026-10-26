using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntegrationHub.Application;
using IntegrationHub.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntegrationHub.Tests;

[Collection("HubDatabase")]
public sealed class ReliabilityTests
{
    [Fact]
    public async Task ExpiredLeaseTakeoverRejectsStalePageCheckpoint()
    {
        var fake = new ScriptedConnectors { HoldSource = true };
        await using var first = Factory(fake);
        using var firstClient = await first.CreateAdminClientAsync();
        await MigrateAsync(first);
        var integrationId = await CreateIntegrationAsync(firstClient, new RetryPolicy(3, 1, 2, 2));
        var runId = await StartAsync(firstClient, integrationId);
        await fake.SourceEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var second = Factory(fake);
        using var secondClient = await second.CreateAdminClientAsync();
        await using (var scope = first.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var run = await db.SyncRuns.AsNoTracking().SingleAsync(x => x.Id == runId);
            Assert.Equal(1, run.LeaseGeneration);
            await db.SyncRuns.Where(x => x.Id == runId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }
        var takenOver = false;
        for (var i = 0; i < 100; i++)
        {
            await using var scope = second.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            if (await db.SyncRuns.AsNoTracking().AnyAsync(x => x.Id == runId && x.LeaseGeneration == 2))
            { takenOver = true; break; }
            await Task.Delay(100);
        }
        Assert.True(takenOver);
        fake.ReleaseSource.TrySetResult();
        var state = await WaitTerminalAsync(secondClient, runId);
        Assert.Equal("completed_with_errors", state.GetProperty("status").GetString());
        Assert.Equal(3, state.GetProperty("counts").GetProperty("fetched").GetInt32());
        Assert.Equal(2, fake.DestinationCount);
        await using var finalScope = second.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<HubDbContext>();
        Assert.Equal(1, await finalDb.SyncPages.CountAsync(x => x.RunId == runId));
        Assert.Equal(2, await finalDb.SyncRunEvents.CountAsync(x => x.RunId == runId && x.EventType == "claimed"));
    }

    [Fact]
    public async Task LateDestinationResponseCannotOverwriteTakeoverResult()
    {
        var fake = new ScriptedConnectors { HoldDestinationAfterCommit = true };
        await using var first = Factory(fake);
        using var firstClient = await first.CreateAdminClientAsync();
        await MigrateAsync(first);
        var integrationId = await CreateIntegrationAsync(firstClient, new RetryPolicy(3, 1, 2, 2));
        var runId = await StartAsync(firstClient, integrationId);
        await fake.DestinationCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var second = Factory(fake);
        using var secondClient = await second.CreateAdminClientAsync();
        await using (var scope = first.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            await db.SyncRuns.Where(x => x.Id == runId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }
        var state = await WaitTerminalAsync(secondClient, runId);
        fake.ReleaseDestination.TrySetResult();
        Assert.Equal("completed_with_errors", state.GetProperty("status").GetString());
        Assert.Equal(1, state.GetProperty("counts").GetProperty("unchanged").GetInt32());
        Assert.Equal(2, fake.DestinationCount);
        await using var finalScope = second.Services.CreateAsyncScope();
        var db2 = finalScope.ServiceProvider.GetRequiredService<HubDbContext>();
        var uncertain = await db2.SyncAttempts.Where(x => x.Outcome == "uncertain")
            .Join(db2.SyncRecords.Where(x => x.RunId == runId), x => x.RecordId, x => x.Id,
                (attempt, record) => new { attempt.RecordId, record.SourceRecordId }).SingleAsync();
        var recordState = await db2.SyncRecords.SingleAsync(x => x.Id == uncertain.RecordId);
        Assert.Equal("unchanged", recordState.Status);
        Assert.Equal(2, recordState.AttemptCount);
    }

    [Fact]
    public async Task LostResponseAndServiceOutageRetryWithoutDuplicateRows()
    {
        var fake = new ScriptedConnectors { LostResponseOnce = true, DestinationUnavailableOnce = true };
        await using var factory = Factory(fake);
        using var client = await factory.CreateAdminClientAsync();
        await MigrateAsync(factory);
        var integrationId = await CreateIntegrationAsync(client, new RetryPolicy(3, 1, 2, 2));
        var runId = await StartAsync(client, integrationId);
        var state = await WaitTerminalAsync(client, runId);
        Assert.Equal("completed_with_errors", state.GetProperty("status").GetString());
        var counts = state.GetProperty("counts");
        Assert.Equal(3, counts.GetProperty("fetched").GetInt32());
        Assert.Equal(1, counts.GetProperty("created").GetInt32());
        Assert.Equal(1, counts.GetProperty("unchanged").GetInt32());
        Assert.Equal(1, counts.GetProperty("failedPermanent").GetInt32());
        Assert.Equal(2, fake.DestinationCount);
        var rows = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/records?pageSize=100");
        var first = rows.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("sourceRecordId").GetString() == "commerce-0001");
        var second = rows.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("sourceRecordId").GetString() == "commerce-0002");
        var invalid = rows.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("sourceRecordId").GetString() == "commerce-0003");
        Assert.Equal(2, first.GetProperty("attemptCount").GetInt32());
        Assert.Equal(2, second.GetProperty("attemptCount").GetInt32());
        Assert.Equal(0, invalid.GetProperty("attemptCount").GetInt32());
        var attempts = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/records/{first.GetProperty("recordId").GetGuid()}/attempts");
        Assert.Equal("retry_wait", attempts.GetProperty("items")[0].GetProperty("outcome").GetString());
        Assert.Equal("unchanged", attempts.GetProperty("items")[1].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task ExhaustedSourceFailureCanBeRetriedWithOriginalSnapshot()
    {
        var fake = new ScriptedConnectors { SourceFailuresRemaining = 1 };
        await using var factory = Factory(fake);
        using var client = await factory.CreateAdminClientAsync();
        await MigrateAsync(factory);
        var integrationId = await CreateIntegrationAsync(client, new RetryPolicy(1, 1, 2, 2));
        var runId = await StartAsync(client, integrationId);
        var failed = await WaitTerminalAsync(client, runId);
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Equal("transient", failed.GetProperty("lastErrorCategory").GetString());
        Assert.Contains("retry", failed.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()));
        var retried = await client.PostAsync($"/api/v1/runs/{runId}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/runs/{runId}/retry", null)).StatusCode);
        var complete = await WaitTerminalAsync(client, runId);
        Assert.Equal("completed_with_errors", complete.GetProperty("status").GetString());
        Assert.Equal(1, complete.GetProperty("recoveryGeneration").GetInt32());
        Assert.Equal(3, complete.GetProperty("counts").GetProperty("fetched").GetInt32());
        Assert.Equal(2, fake.DestinationCount);
    }

    [Fact]
    public async Task SourceOutageUsesPersistedRetryAndCompletesWithinBudget()
    {
        var fake = new ScriptedConnectors { SourceFailuresRemaining = 1 };
        await using var factory = Factory(fake);
        using var client = await factory.CreateAdminClientAsync();
        await MigrateAsync(factory);
        var integrationId = await CreateIntegrationAsync(client, new RetryPolicy(3, 1, 2, 2));
        var runId = await StartAsync(client, integrationId);
        var state = await WaitTerminalAsync(client, runId);
        Assert.Equal("completed_with_errors", state.GetProperty("status").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        Assert.Equal(1, await db.SyncRunEvents.CountAsync(x => x.RunId == runId && x.EventType == "source_retry_scheduled"));
        Assert.Equal(0, await db.SyncRuns.Where(x => x.Id == runId).Select(x => x.SourceAttemptCount).SingleAsync());
    }

    [Fact]
    public async Task SimultaneousManualRequestsAllowOneActiveRunAndCancellation()
    {
        var fake = new ScriptedConnectors { HoldSource = true };
        await using var factory = Factory(fake);
        using var client = await factory.CreateAdminClientAsync();
        await MigrateAsync(factory);
        var integrationId = await CreateIntegrationAsync(client, new RetryPolicy(3, 1, 2, 2));
        var responses = await Task.WhenAll(
            client.PostAsync($"/api/v1/integrations/{integrationId}/runs", null),
            client.PostAsync($"/api/v1/integrations/{integrationId}/runs", null));
        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        var runId = (await responses.Single(x => x.StatusCode == HttpStatusCode.Accepted)
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        await fake.SourceEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondIntegration = await CreateIntegrationAsync(client, new RetryPolicy(3, 1, 2, 2));
        var queuedRun = await StartAsync(client, secondIntegration);
        var queuedCancel = await client.PostAsync($"/api/v1/runs/{queuedRun}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, queuedCancel.StatusCode);
        var queuedState = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{queuedRun}");
        Assert.Equal("cancelled", queuedState.GetProperty("status").GetString());
        Assert.Equal(0, queuedState.GetProperty("counts").GetProperty("fetched").GetInt32());
        var cancel = await client.PostAsync($"/api/v1/runs/{runId}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        fake.ReleaseSource.TrySetResult();
        var state = await WaitTerminalAsync(client, runId);
        Assert.Equal("cancelled", state.GetProperty("status").GetString());
        Assert.Equal(0, fake.DestinationCount);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/runs/{runId}/cancel", null)).StatusCode);
    }

    private static WebApplicationFactory<Program> Factory(ScriptedConnectors fake)
    {
        var connection = Environment.GetEnvironmentVariable("INTEGRATION_TEST_DATABASE");
        Assert.False(string.IsNullOrWhiteSpace(connection));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Hub", connection);
            builder.UseSetting("Security:EncryptionKey", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
            builder.UseSetting("Security:AllowedConnectorHosts", "localhost");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services => services.AddSingleton<IOrderConnectorFactory>(fake));
        });
    }

    private static async Task MigrateAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<HubDbContext>().Database.MigrateAsync();
    }

    private static async Task<Guid> CreateIntegrationAsync(HttpClient client, RetryPolicy policy)
    {
        var configuration = new ConnectorConfiguration(1, "http://localhost:8081/commerce", "apiKeyHeader", 50, 5);
        var source = await client.PostAsJsonAsync("/api/v1/connectors",
            new ConnectorWriteRequest("Reliability source", "restOrdersV1", "source", configuration, "source-key", true));
        Assert.Equal(HttpStatusCode.Created, source.StatusCode);
        var sourceId = (await source.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var destination = await client.PostAsJsonAsync("/api/v1/connectors",
            new ConnectorWriteRequest("Reliability destination", "restOrdersV1", "destination",
                configuration with { BaseUrl = "http://localhost:8081/erp" }, "destination-key", true));
        Assert.Equal(HttpStatusCode.Created, destination.StatusCode);
        var destinationId = (await destination.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var body = new IntegrationWriteRequest("Reliability orders", sourceId, destinationId, "orders",
            new MappingConfiguration(1,
            [new("orderNumber", "externalOrderId", true, null),
             new("customer.email", "customerEmail", true, "trim"),
             new("total", "amount", true, "decimal"),
             new("placedAt", "placedAtUtc", true, "utcDate")]),
            new ValidationConfiguration(1, 0), policy, true);
        var created = await client.PostAsJsonAsync("/api/v1/integrations", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> StartAsync(HttpClient client, Guid integrationId)
    {
        var response = await client.PostAsync($"/api/v1/integrations/{integrationId}/runs", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
    }

    private static async Task<JsonElement> WaitTerminalAsync(HttpClient client, Guid runId)
    {
        for (var i = 0; i < 300; i++)
        {
            var state = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}");
            if (state.GetProperty("status").GetString() is "completed" or "completed_with_errors" or "failed" or "cancelled")
                return state;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Run {runId} did not become terminal.");
    }

    private sealed class ScriptedConnectors : IOrderConnectorFactory, IOrderSource, IOrderDestination
    {
        private readonly ConcurrentDictionary<string, Guid> destination = new();
        private int sourceFailures;
        private int lostResponse;
        private int unavailable;
        private int holdDestination;
        public int SourceFailuresRemaining { set => sourceFailures = value; }
        public bool LostResponseOnce { set => lostResponse = value ? 1 : 0; }
        public bool DestinationUnavailableOnce { set => unavailable = value ? 1 : 0; }
        public bool HoldSource { get; set; }
        public bool HoldDestinationAfterCommit { set => holdDestination = value ? 1 : 0; }
        public TaskCompletionSource SourceEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DestinationCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDestination { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DestinationCount => destination.Count;
        public IOrderSource CreateSource(ConnectorConfiguration configuration, string apiKey) => this;
        public IOrderDestination CreateDestination(ConnectorConfiguration configuration, string apiKey) => this;
        public async Task<CommercePage> FetchPageAsync(string? cursor, string? snapshot, CancellationToken ct)
        {
            if (HoldSource)
            {
                SourceEntered.TrySetResult();
                await ReleaseSource.Task.WaitAsync(ct);
            }
            if (Interlocked.Exchange(ref sourceFailures, 0) > 0)
                throw new ConnectorException(ConnectorErrorCategory.Transient, "service_unavailable", 503);
            var items = Enumerable.Range(1, 3).Select(i => JsonSerializer.SerializeToElement(i == 3
                ? new { id = $"commerce-{i:0000}", orderNumber = $"ORD-{i:0000}",
                    customer = new { email = "test@example.test" }, placedAt = "2026-01-01T12:00:00Z" } as object
                : new { id = $"commerce-{i:0000}", orderNumber = $"ORD-{i:0000}",
                    customer = new { email = "test@example.test" }, total = 10 + i,
                    placedAt = "2026-01-01T12:00:00Z" } as object)).ToList();
            return new CommercePage(items, "reliability-snapshot", null);
        }
        public async Task<ErpWriteResult> UpsertAsync(Guid integrationId, string sourceRecordId, ErpOrder order, CancellationToken ct)
        {
            if (sourceRecordId == "commerce-0002" && Interlocked.Exchange(ref unavailable, 0) == 1)
                throw new ConnectorException(ConnectorErrorCategory.Transient, "service_unavailable", 503);
            var key = $"{integrationId:N}:{sourceRecordId}";
            var existed = destination.TryGetValue(key, out var id);
            if (!existed) id = destination.GetOrAdd(key, _ => Guid.NewGuid());
            if (Interlocked.Exchange(ref holdDestination, 0) == 1)
            {
                DestinationCommitted.TrySetResult();
                await ReleaseDestination.Task.WaitAsync(ct);
            }
            if (sourceRecordId == "commerce-0001" && Interlocked.Exchange(ref lostResponse, 0) == 1)
                throw new ConnectorException(ConnectorErrorCategory.Timeout, "connector_timeout");
            return new ErpWriteResult(id, existed ? "unchanged" : "created");
        }
    }
}
