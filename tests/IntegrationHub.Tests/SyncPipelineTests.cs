using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntegrationHub.Application;
using IntegrationHub.Api;
using IntegrationHub.Domain;
using IntegrationHub.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntegrationHub.Tests;

[Collection("HubDatabase")]
public sealed class SyncPipelineTests
{
    [Fact]
    public async Task RestartReplaysUncertainWriteWithoutCreatingADuplicate()
    {
        var connection = Environment.GetEnvironmentVariable("INTEGRATION_TEST_DATABASE");
        Assert.False(string.IsNullOrWhiteSpace(connection));
        var fake = new FakeConnectors(blockAfterFirstWrite: true);
        var runId = Guid.NewGuid();
        await using (var first = Factory(connection!, fake))
        {
            using var client = await first.CreateAdminClientAsync();
            using var scope = first.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            await db.Database.MigrateAsync();
            var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();
            var sourceConfig = new ConnectorConfiguration(1, "http://localhost:8081/commerce", "apiKeyHeader", 50, 5);
            var destinationConfig = sourceConfig with { BaseUrl = "http://localhost:8081/erp" };
            var source = new Connector { Name = "Restart source", Role = "source", Enabled = true,
                ConfigurationJson = JsonSerializer.Serialize(sourceConfig), EncryptedSecret = protector.Encrypt("source-key") };
            var destination = new Connector { Name = "Restart destination", Role = "destination", Enabled = true,
                ConfigurationJson = JsonSerializer.Serialize(destinationConfig), EncryptedSecret = protector.Encrypt("destination-key") };
            var mapping = new MappingConfiguration(1,
            [new("orderNumber", "externalOrderId", true, null),
             new("customer.email", "customerEmail", true, "trim"),
             new("total", "amount", true, "decimal"),
             new("placedAt", "placedAtUtc", true, "utcDate")]);
            var validation = new ValidationConfiguration(1, 0);
            var policy = new RetryPolicy(4, 2, 60, 2);
            var integration = new Integration { Name = "Restart integration", SourceConnectorId = source.Id,
                DestinationConnectorId = destination.Id, MappingJson = JsonSerializer.Serialize(mapping),
                ValidationJson = JsonSerializer.Serialize(validation), RetryPolicyJson = JsonSerializer.Serialize(policy), Enabled = true };
            db.Connectors.AddRange(source, destination);
            db.Integrations.Add(integration);
            db.SyncRuns.Add(new SyncRun { Id = runId, IntegrationId = integration.Id,
                ConfigSnapshotJson = JsonSerializer.Serialize(new RunSnapshot(sourceConfig, source.EncryptedSecret!,
                    destinationConfig, destination.EncryptedSecret!, mapping, validation, policy)),
                DeadlineAtUtc = DateTimeOffset.UtcNow.AddHours(1) });
            await db.SaveChangesAsync();
            await fake.FirstWriteCommitted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        await using (var db = new HubDbContext(new DbContextOptionsBuilder<HubDbContext>().UseNpgsql(connection).Options))
            await db.SyncRuns.Where(x => x.Id == runId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));

        await using var restarted = Factory(connection!, fake);
        using var restartedClient = await restarted.CreateAdminClientAsync();
        JsonElement state = default;
        for (var i = 0; i < 300; i++)
        {
            state = await restartedClient.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}");
            if (state.GetProperty("status").GetString() is "completed_with_errors" or "completed" or "failed") break;
            await Task.Delay(100);
        }
        Assert.Equal("completed_with_errors", state.GetProperty("status").GetString());
        Assert.Equal(125, state.GetProperty("counts").GetProperty("fetched").GetInt32());
        Assert.Equal(1, state.GetProperty("counts").GetProperty("unchanged").GetInt32());
        Assert.Equal(124, fake.DestinationCount);
        using var scope2 = restarted.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<HubDbContext>();
        var uncertain = await db2.SyncAttempts.CountAsync(x => x.Outcome == "uncertain" &&
            db2.SyncRecords.Any(r => r.Id == x.RecordId && r.RunId == runId));
        Assert.Equal(1, uncertain);
    }

    [Fact]
    public async Task QueuedRunProcessesPagesAndPersistsRecordOutcomesAcrossHostRestart()
    {
        var connection = Environment.GetEnvironmentVariable("INTEGRATION_TEST_DATABASE");
        Assert.False(string.IsNullOrWhiteSpace(connection));
        var fake = new FakeConnectors();
        await using var factory = Factory(connection!, fake);
        using var client = await factory.CreateAdminClientAsync();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<HubDbContext>().Database.MigrateAsync();

        var configuration = new ConnectorConfiguration(1, "http://localhost:8081/commerce", "apiKeyHeader", 50, 5);
        var sourceResponse = await client.PostAsJsonAsync("/api/v1/connectors",
            new ConnectorWriteRequest("Sync test source", "restOrdersV1", "source", configuration, "source-key", true));
        Assert.Equal(HttpStatusCode.Created, sourceResponse.StatusCode);
        var sourceId = (await sourceResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var destinationResponse = await client.PostAsJsonAsync("/api/v1/connectors",
            new ConnectorWriteRequest("Sync test destination", "restOrdersV1", "destination",
                configuration with { BaseUrl = "http://localhost:8081/erp" }, "destination-key", true));
        Assert.Equal(HttpStatusCode.Created, destinationResponse.StatusCode);
        var destinationId = (await destinationResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var integration = new IntegrationWriteRequest("Sync test orders", sourceId, destinationId, "orders",
            new MappingConfiguration(1,
            [new("orderNumber", "externalOrderId", true, null),
             new("customer.email", "customerEmail", true, "trim"),
             new("total", "amount", true, "decimal"),
             new("placedAt", "placedAtUtc", true, "utcDate")]),
            new ValidationConfiguration(1, 0), new RetryPolicy(4, 2, 60, 2), true);
        var integrationResponse = await client.PostAsJsonAsync("/api/v1/integrations", integration);
        Assert.Equal(HttpStatusCode.Created, integrationResponse.StatusCode);
        var integrationId = (await integrationResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var stored = await db.Integrations.SingleAsync(x => x.Id == integrationId);
            var validMapping = stored.MappingJson;
            stored.MappingJson = "{}";
            await db.SaveChangesAsync();
            var rejected = await client.PostAsync($"/api/v1/integrations/{integrationId}/runs", null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            Assert.False(await db.SyncRuns.AnyAsync(x => x.IntegrationId == integrationId));
            stored.MappingJson = validMapping;
            await db.SaveChangesAsync();
        }

        var queued = await client.PostAsync($"/api/v1/integrations/{integrationId}/runs", null);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var runId = (await queued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        Assert.Equal($"/api/v1/runs/{runId}", queued.Headers.Location?.ToString());
        JsonElement state = default;
        for (var i = 0; i < 300; i++)
        {
            state = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}");
            if (state.GetProperty("status").GetString() is "completed_with_errors" or "completed" or "failed") break;
            await Task.Delay(100);
        }
        Assert.Equal("completed_with_errors", state.GetProperty("status").GetString());
        var counts = state.GetProperty("counts");
        Assert.Equal(125, counts.GetProperty("fetched").GetInt32());
        Assert.Equal(124, counts.GetProperty("created").GetInt32());
        Assert.Equal(1, counts.GetProperty("failedPermanent").GetInt32());
        Assert.Equal(0, counts.GetProperty("pending").GetInt32());
        Assert.Equal(124, fake.DestinationCount);
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/{integrationId}/runs?pageSize=1");
        Assert.Equal(runId, history.GetProperty("items")[0].GetProperty("runId").GetGuid());
        var firstRecordPage = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/records?pageSize=1");
        Assert.Single(firstRecordPage.GetProperty("items").EnumerateArray());
        Assert.NotEqual(JsonValueKind.Null, firstRecordPage.GetProperty("nextCursor").ValueKind);
        var records = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/records?status=failed_permanent");
        Assert.Equal("required_field_missing", records.GetProperty("items")[0].GetProperty("errorCode").GetString());

        await factory.DisposeAsync();
        await using var restarted = Factory(connection!, fake);
        using var restartedClient = await restarted.CreateAdminClientAsync();
        var persisted = await restartedClient.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}");
        Assert.Equal("completed_with_errors", persisted.GetProperty("status").GetString());
        Assert.Equal(125, persisted.GetProperty("counts").GetProperty("fetched").GetInt32());
    }

    private static WebApplicationFactory<Program> Factory(string connection, FakeConnectors fake) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Hub", connection);
            builder.UseSetting("Security:EncryptionKey", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
            builder.UseSetting("Security:AllowedConnectorHosts", "localhost");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services => services.AddSingleton<IOrderConnectorFactory>(fake));
        });

    private sealed class FakeConnectors(bool blockAfterFirstWrite = false) : IOrderConnectorFactory, IOrderSource, IOrderDestination
    {
        private readonly ConcurrentDictionary<string, Guid> destination = new();
        private int blockRemaining = blockAfterFirstWrite ? 1 : 0;
        public TaskCompletionSource FirstWriteCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DestinationCount => destination.Count;
        public IOrderSource CreateSource(ConnectorConfiguration configuration, string apiKey) => this;
        public IOrderDestination CreateDestination(ConnectorConfiguration configuration, string apiKey) => this;
        public Task<CommercePage> FetchPageAsync(string? cursor, string? snapshot, CancellationToken ct)
        {
            var offset = cursor is null ? 0 : int.Parse(cursor);
            var items = Enumerable.Range(offset + 1, Math.Min(50, 125 - offset)).Select(i =>
                JsonSerializer.SerializeToElement(i == 42
                    ? new { id = $"commerce-{i:0000}", orderNumber = $"ORD-{i:0000}",
                        customer = new { email = "test@example.test" }, placedAt = "2026-01-01T12:00:00Z" } as object
                    : new { id = $"commerce-{i:0000}", orderNumber = $"ORD-{i:0000}",
                        customer = new { email = "test@example.test" }, total = 10 + i,
                        placedAt = "2026-01-01T12:00:00Z" } as object)).ToList();
            var next = offset + items.Count < 125 ? (offset + items.Count).ToString() : null;
            return Task.FromResult(new CommercePage(items, "test-snapshot", next));
        }
        public async Task<ErpWriteResult> UpsertAsync(Guid integrationId, string sourceRecordId, ErpOrder order, CancellationToken ct)
        {
            var key = $"{integrationId:N}:{sourceRecordId}";
            var existed = destination.ContainsKey(key);
            var id = destination.GetOrAdd(key, _ => Guid.NewGuid());
            if (Interlocked.Exchange(ref blockRemaining, 0) == 1)
            {
                FirstWriteCommitted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return new ErpWriteResult(id, existed ? "unchanged" : "created");
        }
    }
}
