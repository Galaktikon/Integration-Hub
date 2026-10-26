using System.Text.Json;
using System.Net.Http.Json;
using IntegrationHub.Application;
using IntegrationHub.Infrastructure;
using IntegrationHub.Rest;
using Microsoft.EntityFrameworkCore;

namespace IntegrationHub.Api;

public static class ConnectorTestEndpoint
{
    public static void MapConnectorTestEndpoint(this WebApplication app)
    {
        app.MapPost("/api/v1/connectors/{id:guid}/test", async (Guid id, HubDbContext db,
            SecretProtector secrets, IHostEnvironment env, CancellationToken ct) =>
        {
            var connector = await db.Connectors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (connector is null) return Results.Problem(statusCode: 404, title: "Connector was not found.");
            if (connector.EncryptedSecret is null) return Results.Ok(new { healthy = false, code = "missing_secret" });
            var configuration = JsonSerializer.Deserialize<ConnectorConfiguration>(connector.ConfigurationJson,
                RunEndpoints.Json)!;
            try
            {
                using var client = RestHttpClientFactory.Create(!env.IsDevelopment());
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(10, configuration.TimeoutSeconds)));
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{configuration.BaseUrl.TrimEnd('/')}/capabilities");
                request.Headers.Add("X-Api-Key", secrets.Decrypt(connector.EncryptedSecret));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    return Results.Ok(new { healthy = false, code = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                        ? "authentication_failed" : "connector_unavailable" });
                await response.Content.LoadIntoBufferAsync(8192, timeout.Token);
                var capabilities = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                var valid = capabilities.ValueKind == JsonValueKind.Object &&
                    capabilities.TryGetProperty("schemaVersion", out var version) && version.GetInt32() == 1 &&
                    capabilities.TryGetProperty("role", out var role) && role.GetString() == connector.Role;
                return Results.Ok(new { healthy = valid, code = valid ? "ok" : "capability_mismatch" });
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
            { return Results.Ok(new { healthy = false, code = "connector_unavailable" }); }
        });
    }
}
