using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IntegrationHub.Tests;

internal static class TestAdminClient
{
    public static async Task<HttpClient> CreateAdminClientAsync(this WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "admin", password = "local-demo-password" });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            result.GetProperty("accessToken").GetString());
        return client;
    }
}
