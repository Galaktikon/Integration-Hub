using System.Text.Json;
using IntegrationHub.Application;
using IntegrationHub.Domain;
using IntegrationHub.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IntegrationHub.Api;

public static class DemoSeed
{
    public static async Task RunAsync(IServiceProvider services, IConfiguration config)
    {
        var db = services.GetRequiredService<HubDbContext>();
        var protector = services.GetRequiredService<SecretProtector>();
        var baseUrl = config["Demo:BaseUrl"]?.TrimEnd('/') ?? "http://localhost:8081";
        var secret = config["Demo:ApiKey"] ?? "local-demo-key";
        if (!await db.Connectors.AnyAsync(x => x.Name == "Demo Commerce" && x.Role == "source"))
            db.Connectors.Add(NewConnector("Demo Commerce", "source", $"{baseUrl}/commerce", secret, protector));
        if (!await db.Connectors.AnyAsync(x => x.Name == "Demo ERP" && x.Role == "destination"))
            db.Connectors.Add(NewConnector("Demo ERP", "destination", $"{baseUrl}/erp", secret, protector));
        await db.SaveChangesAsync();
    }

    private static Connector NewConnector(string name, string role, string url, string secret, SecretProtector protector) =>
        new()
        {
            Name = name,
            Role = role,
            Kind = "restOrdersV1",
            ConfigurationJson = JsonSerializer.Serialize(new ConnectorConfiguration(1, url, "apiKeyHeader", 100, 15), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            EncryptedSecret = protector.Encrypt(secret),
            Enabled = true
        };
}
