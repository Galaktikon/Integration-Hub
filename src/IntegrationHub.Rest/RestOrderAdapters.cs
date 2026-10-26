using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntegrationHub.Application;

namespace IntegrationHub.Rest;

public static class RestHttpClientFactory
{
    public static HttpClient Create() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };
}

public sealed class RestOrderSource : IOrderSource
{
    private readonly RestClient client;
    private readonly int pageSize;
    public RestOrderSource(HttpClient http, ConnectorConfiguration configuration, string apiKey)
    {
        client = new RestClient(http, configuration, apiKey);
        pageSize = configuration.PageSize;
    }

    public async Task<CommercePage> FetchPageAsync(string? cursor, string? snapshot, CancellationToken cancellationToken)
    {
        var query = $"orders?pageSize={pageSize}";
        if (cursor is not null) query += $"&cursor={Uri.EscapeDataString(cursor)}";
        if (snapshot is not null) query += $"&snapshot={Uri.EscapeDataString(snapshot)}";
        using var response = await client.SendAsync(HttpMethod.Get, query, null, null, cancellationToken);
        var page = await client.ReadAsync<CommercePage>(response, cancellationToken);
        if (string.IsNullOrWhiteSpace(page.Snapshot) || page.Items is null || page.Items.Count > pageSize ||
            page.NextCursor is not null && (page.NextCursor.Length == 0 || page.Items.Count == 0) ||
            snapshot is not null && page.Snapshot != snapshot)
            throw new ConnectorException(ConnectorErrorCategory.MalformedResponse, "invalid_source_page");
        return page;
    }
}

public sealed class RestOrderDestination : IOrderDestination
{
    private readonly RestClient client;
    public RestOrderDestination(HttpClient http, ConnectorConfiguration configuration, string apiKey) =>
        client = new RestClient(http, configuration, apiKey);

    public async Task<ErpWriteResult> UpsertAsync(Guid integrationId, string sourceRecordId, ErpOrder order, CancellationToken cancellationToken)
    {
        if (integrationId == Guid.Empty || string.IsNullOrWhiteSpace(sourceRecordId) || sourceRecordId.Length > 80)
            throw new ArgumentException("Stable integration and source record IDs are required.");
        var path = $"orders/{integrationId:D}/{Uri.EscapeDataString(sourceRecordId)}";
        using var response = await client.SendAsync(HttpMethod.Put, path, order, $"{integrationId:N}:{sourceRecordId}", cancellationToken);
        var result = await client.ReadAsync<ErpWriteResult>(response, cancellationToken);
        if (result.DestinationOrderId == Guid.Empty || result.Outcome is not ("created" or "updated" or "unchanged") ||
            result.Outcome == "created" != (response.StatusCode == HttpStatusCode.Created))
            throw new ConnectorException(ConnectorErrorCategory.MalformedResponse, "invalid_destination_response");
        return result;
    }
}

internal sealed class RestClient
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly Uri baseUri;
    private readonly string apiKey;
    private readonly TimeSpan timeout;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public RestClient(HttpClient http, ConnectorConfiguration configuration, string apiKey)
    {
        if (!Uri.TryCreate(configuration.BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || configuration.TimeoutSeconds is < 1 or > 60 ||
            configuration.PageSize is < 1 or > 500 || string.IsNullOrEmpty(apiKey))
            throw new ArgumentException("Invalid REST connector configuration.");
        this.http = http;
        baseUri = new Uri(configuration.BaseUrl.TrimEnd('/') + "/");
        this.apiKey = apiKey;
        timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken ct)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.Add("X-Api-Key", apiKey);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            if ((int)response.StatusCode >= 300)
            {
                var exception = Classify(response);
                response.Dispose();
                throw exception;
            }
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConnectorException(ConnectorErrorCategory.Timeout, "connector_timeout");
        }
        catch (HttpRequestException)
        {
            throw new ConnectorException(ConnectorErrorCategory.Transient, "network_error");
        }
    }

    public async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeoutSource.Token)) > 0)
            {
                if (buffer.Length + count > MaxResponseBytes)
                    throw new ConnectorException(ConnectorErrorCategory.MalformedResponse, "response_too_large");
                buffer.Write(chunk, 0, count);
            }
            buffer.Position = 0;
            return await JsonSerializer.DeserializeAsync<T>(buffer, JsonOptions, timeoutSource.Token)
                ?? throw new ConnectorException(ConnectorErrorCategory.MalformedResponse, "empty_response");
        }
        catch (JsonException)
        {
            throw new ConnectorException(ConnectorErrorCategory.MalformedResponse, "invalid_json");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConnectorException(ConnectorErrorCategory.Timeout, "connector_timeout");
        }
    }

    private static ConnectorException Classify(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var category = status switch
        {
            401 or 403 => ConnectorErrorCategory.Authentication,
            499 => ConnectorErrorCategory.Timeout,
            408 or 429 or >= 500 => ConnectorErrorCategory.Transient,
            _ => ConnectorErrorCategory.Permanent
        };
        return new ConnectorException(category, status switch
        {
            401 or 403 => "connector_authentication_failed",
            429 => "rate_limited",
            499 => "connector_timeout",
            503 => "service_unavailable",
            _ => $"http_{status}"
        }, status, response.Headers.RetryAfter?.Delta);
    }
}
