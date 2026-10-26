extern alias Demo;

using System.Net;
using System.Net.Http.Json;
using IntegrationHub.Application;
using IntegrationHub.Rest;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DemoProgram = Demo::Program;
using DemoDbContext = Demo::IntegrationHub.DemoSystems.DemoDbContext;

namespace IntegrationHub.Tests;

[Collection("HubDatabase")]
public sealed class DemoSystemsTests
{
    [Fact]
    public async Task CommercePaginationAndErpIdempotencySurviveLostResponse()
    {
        var connection = Environment.GetEnvironmentVariable("INTEGRATION_TEST_DATABASE");
        Assert.False(string.IsNullOrWhiteSpace(connection));
        await using var factory = new WebApplicationFactory<DemoProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Demo", connection);
            builder.UseSetting("Demo:ApiKey", "test-api-key");
            builder.UseSetting("Demo:AdminKey", "test-admin-key");
            builder.ConfigureLogging(logging => logging.ClearProviders());
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DemoDbContext>().Database.MigrateAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/commerce/orders")).StatusCode);
        using var seed = new HttpRequestMessage(HttpMethod.Post, "/__admin/seed");
        seed.Headers.Add("X-Api-Key", "test-admin-key");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(seed)).StatusCode);

        var source = new RestOrderSource(client, new ConnectorConfiguration(1, "http://localhost/commerce", "apiKeyHeader", 50, 2), "test-api-key");
        var first = await source.FetchPageAsync(null, null, default);
        Assert.Equal(50, first.Items.Count);
        Assert.Equal("commerce-0001", first.Items[0].GetProperty("id").GetString());
        var second = await source.FetchPageAsync(first.NextCursor, first.Snapshot, default);
        Assert.Equal(50, second.Items.Count);
        var third = await source.FetchPageAsync(second.NextCursor, second.Snapshot, default);
        Assert.Equal(25, third.Items.Count);
        Assert.Null(third.NextCursor);
        var empty = await source.FetchPageAsync(Convert.ToBase64String("500"u8.ToArray()), first.Snapshot, default);
        Assert.Empty(empty.Items);
        Assert.Null(empty.NextCursor);

        await SetFaultAsync(client, "source", "429");
        var rateLimit = await Assert.ThrowsAsync<ConnectorException>(() => source.FetchPageAsync(null, null, default));
        Assert.Equal(ConnectorErrorCategory.Transient, rateLimit.Category);
        Assert.Equal(429, rateLimit.HttpStatus);
        await SetFaultAsync(client, "source", "invalidJson");
        var invalidJson = await Assert.ThrowsAsync<ConnectorException>(() => source.FetchPageAsync(null, null, default));
        Assert.Equal(ConnectorErrorCategory.MalformedResponse, invalidJson.Category);
        await SetFaultAsync(client, "source", "malformedRecord");
        var malformed = await source.FetchPageAsync(null, null, default);
        Assert.False(malformed.Items[0].TryGetProperty("total", out _));

        var destination = new RestOrderDestination(client, new ConnectorConfiguration(1, "http://localhost/erp", "apiKeyHeader", 50, 2), "test-api-key");
        var integrationId = Guid.NewGuid();
        var sourceId = $"test-{Guid.NewGuid():N}";
        var order = new ErpOrder("ORD-1", "user@example.test", 12.50m, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var created = await destination.UpsertAsync(integrationId, sourceId, order, default);
        Assert.Equal("created", created.Outcome);
        var repeated = await destination.UpsertAsync(integrationId, sourceId, order, default);
        Assert.Equal("unchanged", repeated.Outcome);
        Assert.Equal(created.DestinationOrderId, repeated.DestinationOrderId);
        var changed = await destination.UpsertAsync(integrationId, sourceId, order with { Amount = 13 }, default);
        Assert.Equal("updated", changed.Outcome);
        Assert.Equal(created.DestinationOrderId, changed.DestinationOrderId);

        await SetFaultAsync(client, "destination", "lostResponse", 3000);
        var lostSourceId = $"test-{Guid.NewGuid():N}";
        var error = await Assert.ThrowsAsync<ConnectorException>(() => destination.UpsertAsync(integrationId, lostSourceId, order, default));
        Assert.Equal(ConnectorErrorCategory.Timeout, error.Category);
        var recovered = await destination.UpsertAsync(integrationId, lostSourceId, order, default);
        Assert.Equal("unchanged", recovered.Outcome);
        using var scope2 = factory.Services.CreateScope();
        Assert.Equal(1, await scope2.ServiceProvider.GetRequiredService<DemoDbContext>().DestinationOrders
            .CountAsync(x => x.IntegrationId == integrationId && x.SourceRecordId == lostSourceId));

        using var unconfirmedReset = new HttpRequestMessage(HttpMethod.Post, "/__admin/reset");
        unconfirmedReset.Headers.Add("X-Api-Key", "test-admin-key");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(unconfirmedReset)).StatusCode);
        using var reset = new HttpRequestMessage(HttpMethod.Post, "/__admin/reset");
        reset.Headers.Add("X-Api-Key", "test-admin-key");
        reset.Headers.Add("X-Confirm-Reset", "reset-synthetic-demo");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(reset)).StatusCode);
        Assert.Equal(0, await scope2.ServiceProvider.GetRequiredService<DemoDbContext>().DestinationOrders.CountAsync());
    }

    private static async Task SetFaultAsync(HttpClient client, string target, string mode, int delayMilliseconds = 30000)
    {
        using var fault = new HttpRequestMessage(HttpMethod.Post, "/__admin/faults")
        { Content = JsonContent.Create(new { target, mode, remaining = 1, delayMilliseconds }) };
        fault.Headers.Add("X-Api-Key", "test-admin-key");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(fault)).StatusCode);
    }
}
