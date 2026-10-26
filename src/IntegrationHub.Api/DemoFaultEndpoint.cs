using System.Net.Http.Json;

namespace IntegrationHub.Api;

public sealed record DemoFaultRequest(string Target, string Mode, int Remaining);

public static class DemoFaultEndpoint
{
    public static void MapDemoFaultEndpoint(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() && !app.Configuration.GetValue<bool>("Demo:EnableFaultProxy")) return;
        app.MapPost("/api/v1/demo/faults", async (DemoFaultRequest request, IConfiguration config, CancellationToken ct) =>
        {
            if (request.Target != "source" || request.Mode != "503" || request.Remaining is < 1 or > 10)
                return Results.Problem(statusCode: 422, title: "Only 1 to 10 source 503 responses may be injected from the dashboard.");
            var baseUrl = config["Demo:BaseUrl"] ?? throw new InvalidOperationException("Demo base URL is required.");
            var adminKey = config["Demo:AdminKey"] ?? throw new InvalidOperationException("Demo admin key is required.");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Add("X-Api-Key", adminKey);
            using var response = await client.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/__admin/faults", request, ct);
            if (!response.IsSuccessStatusCode)
                return Results.Problem(statusCode: 502, title: "The simulator rejected the fault request.");
            return Results.Accepted();
        });
    }
}
