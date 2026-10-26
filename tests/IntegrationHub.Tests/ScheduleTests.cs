using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntegrationHub.Api;
using IntegrationHub.Application;
using IntegrationHub.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntegrationHub.Tests;

[Collection("HubDatabase")]
public sealed class ScheduleTests
{
    [Fact]
    public async Task CompetingSchedulersHandleEachOccurrenceOnceAndSkipDisabledIntegration()
    {
        var connection = Environment.GetEnvironmentVariable("INTEGRATION_TEST_DATABASE");
        Assert.False(string.IsNullOrWhiteSpace(connection));
        await using var first = Factory(connection!);
        await using var second = Factory(connection!);
        using var client = await first.CreateAdminClientAsync();
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            await db.Database.MigrateAsync();
            var oldIds = await db.Integrations.Where(x => x.Name.StartsWith("Schedule test "))
                .Select(x => x.Id).ToListAsync();
            await db.IntegrationSchedules.Where(x => oldIds.Contains(x.IntegrationId))
                .ExecuteUpdateAsync(x => x.SetProperty(s => s.Enabled, false));
            await db.SyncRuns.Where(x => oldIds.Contains(x.IntegrationId) &&
                    (x.Status == "queued" || x.Status == "running"))
                .ExecuteDeleteAsync();
        }

        var source = await CreateConnector(client, "source", "commerce");
        var destination = await CreateConnector(client, "destination", "erp");
        var enabledId = await CreateIntegration(client, source, destination, true);
        var disabledId = await CreateIntegration(client, source, destination, false);
        foreach (var id in new[] { enabledId, disabledId })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity,
                (await client.PutAsJsonAsync($"/api/v1/integrations/{id}/schedule",
                    new { intervalSeconds = 299, enabled = true })).StatusCode);
            var response = await client.PutAsJsonAsync($"/api/v1/integrations/{id}/schedule",
                new { intervalSeconds = 300, enabled = true });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(response.Headers.ETag);
            Assert.Equal((HttpStatusCode)428,
                (await client.PutAsJsonAsync($"/api/v1/integrations/{id}/schedule",
                    new { intervalSeconds = 600, enabled = true })).StatusCode);
        }
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            await db.IntegrationSchedules.Where(x => x.IntegrationId == enabledId || x.IntegrationId == disabledId)
                .ExecuteUpdateAsync(x => x.SetProperty(s => s.NextRunAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        var one = new ScheduleWorker(first.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduleWorker>.Instance);
        var two = new ScheduleWorker(second.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduleWorker>.Instance);
        await Task.WhenAll(one.ProcessDueAsync(CancellationToken.None), two.ProcessDueAsync(CancellationToken.None));
        using var checkScope = first.Services.CreateScope();
        var check = checkScope.ServiceProvider.GetRequiredService<HubDbContext>();
        var created = await check.ScheduleEvents.Where(x => x.ScheduleId ==
            check.IntegrationSchedules.Where(s => s.IntegrationId == enabledId).Select(s => s.Id).Single())
            .ToListAsync();
        Assert.Single(created);
        Assert.Equal("created", created[0].Outcome);
        Assert.Single(await check.SyncRuns.Where(x => x.IntegrationId == enabledId && x.TriggerType == "scheduled").ToListAsync());
        var skipped = await check.ScheduleEvents.Where(x => x.ScheduleId ==
            check.IntegrationSchedules.Where(s => s.IntegrationId == disabledId).Select(s => s.Id).Single())
            .ToListAsync();
        Assert.Single(skipped);
        Assert.Equal("skipped_disabled", skipped[0].Outcome);
        Assert.False(await check.SyncRuns.AnyAsync(x => x.IntegrationId == disabledId));
        await check.SyncRuns.Where(x => x.IntegrationId == enabledId).ExecuteDeleteAsync();
        await check.IntegrationSchedules.Where(x => x.IntegrationId == enabledId || x.IntegrationId == disabledId)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.Enabled, false));
    }

    private static WebApplicationFactory<Program> Factory(string connection) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Hub", connection);
            builder.UseSetting("Security:AllowedConnectorHosts", "localhost");
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });

    private static async Task<Guid> CreateConnector(HttpClient client, string role, string path)
    {
        var response = await client.PostAsJsonAsync("/api/v1/connectors", new ConnectorWriteRequest(
            $"Schedule test {role} {Guid.NewGuid():N}", "restOrdersV1", role,
            new ConnectorConfiguration(1, $"http://localhost:8081/{path}", "apiKeyHeader", 10, 2),
            "test-key", true));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateIntegration(HttpClient client, Guid source, Guid destination, bool enabled)
    {
        var request = new IntegrationWriteRequest($"Schedule test {Guid.NewGuid():N}", source, destination, "orders",
            new MappingConfiguration(1, [new("orderNumber", "externalOrderId", true, "trim"),
                new("customer.email", "customerEmail", true, "trim"),
                new("total", "amount", true, "decimal"),
                new("placedAt", "placedAtUtc", true, "utcDate")]),
            new ValidationConfiguration(1, 0), new RetryPolicy(2, 1, 2, 0), enabled);
        var response = await client.PostAsJsonAsync("/api/v1/integrations", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
