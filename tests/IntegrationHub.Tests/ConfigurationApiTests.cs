using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntegrationHub.Application;
using IntegrationHub.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntegrationHub.Tests;

[Collection("HubDatabase")]
public sealed class ConfigurationApiTests
{
    [Fact]
    public async Task ConnectorSecretsAreRedactedAndStaleWritesFail()
    {
        var connection = Environment.GetEnvironmentVariable("INTEGRATION_TEST_DATABASE");
        Assert.False(string.IsNullOrWhiteSpace(connection),
            "Set INTEGRATION_TEST_DATABASE to a disposable PostgreSQL database before running the full suite.");

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Hub", connection);
            builder.UseSetting("Security:EncryptionKey", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
            builder.UseSetting("Security:AllowedConnectorHosts", "localhost");
            builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
        });
        using var client = factory.CreateClient();
        using (var migrationScope = factory.Services.CreateScope())
            await migrationScope.ServiceProvider.GetRequiredService<HubDbContext>().Database.MigrateAsync();

        var request = new ConnectorWriteRequest("Integration test source", "restOrdersV1", "source",
            new ConnectorConfiguration(1, "http://localhost:8081/commerce", "apiKeyHeader", 100, 10),
            "test-secret-value", true);
        var created = await client.PostAsJsonAsync("/api/v1/connectors", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var response = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain("test-secret-value", response);
        Assert.DoesNotContain("encryptedSecret", response, StringComparison.OrdinalIgnoreCase);
        var id = JsonDocument.Parse(response).RootElement.GetProperty("id").GetGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<HubDbContext>().Connectors.FindAsync(id);
            Assert.NotNull(stored);
            Assert.NotEqual("test-secret-value", stored.EncryptedSecret);
        }

        var read = await client.GetAsync($"/api/v1/connectors/{id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.True(read.Headers.ETag is not null);
        var staleWrite = await client.PutAsJsonAsync($"/api/v1/connectors/{id}", request);
        Assert.Equal((HttpStatusCode)428, staleWrite.StatusCode);

        var bad = request with { Configuration = request.Configuration with { BaseUrl = "http://169.254.169.254/metadata" } };
        var rejected = await client.PostAsJsonAsync("/api/v1/connectors", bad);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

        var destination = request with { Name = "Integration test destination", Role = "destination" };
        var destinationCreated = await client.PostAsJsonAsync("/api/v1/connectors", destination);
        Assert.Equal(HttpStatusCode.Created, destinationCreated.StatusCode);
        var destinationId = (await destinationCreated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var integration = new IntegrationWriteRequest("Orders", id, destinationId, "orders",
            new MappingConfiguration(1,
            [new("orderNumber", "externalOrderId", true, null),
             new("customer.email", "customerEmail", true, "trim"),
             new("total", "amount", true, "decimal"),
             new("placedAt", "placedAtUtc", true, "utcDate")]),
            new ValidationConfiguration(1, 0), new RetryPolicy(4, 2, 60, 2), true);
        var integrationCreated = await client.PostAsJsonAsync("/api/v1/integrations", integration);
        Assert.Equal(HttpStatusCode.Created, integrationCreated.StatusCode);
        var integrationId = (await integrationCreated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var validated = await client.PostAsync($"/api/v1/integrations/{integrationId}/validate", null);
        Assert.Equal(HttpStatusCode.OK, validated.StatusCode);
        Assert.True((await validated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("valid").GetBoolean());

        using var connectorDelete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/connectors/{id}");
        connectorDelete.Headers.IfMatch.Add(read.Headers.ETag!);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(connectorDelete)).StatusCode);

        using var integrationDelete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/integrations/{integrationId}");
        integrationDelete.Headers.IfMatch.Add(integrationCreated.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(integrationDelete)).StatusCode);

        using var sourceCleanup = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/connectors/{id}");
        sourceCleanup.Headers.IfMatch.Add(read.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(sourceCleanup)).StatusCode);
        using var destinationCleanup = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/connectors/{destinationId}");
        destinationCleanup.Headers.IfMatch.Add(destinationCreated.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(destinationCleanup)).StatusCode);
    }
}
