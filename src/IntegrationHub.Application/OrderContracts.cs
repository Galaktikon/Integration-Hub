using System.Text.Json;

namespace IntegrationHub.Application;

public sealed record CommerceCustomer(string Email);
public sealed record CommerceOrder(string Id, string OrderNumber, CommerceCustomer Customer, decimal Total, DateTimeOffset PlacedAt);
public sealed record CommercePage(IReadOnlyList<JsonElement> Items, string Snapshot, string? NextCursor);
public sealed record ErpOrder(string ExternalOrderId, string CustomerEmail, decimal Amount, DateTimeOffset PlacedAtUtc);
public sealed record ErpWriteResult(Guid DestinationOrderId, string Outcome);

public interface IOrderSource
{
    Task<CommercePage> FetchPageAsync(string? cursor, string? snapshot, CancellationToken cancellationToken);
}

public interface IOrderDestination
{
    Task<ErpWriteResult> UpsertAsync(Guid integrationId, string sourceRecordId, ErpOrder order, CancellationToken cancellationToken);
}

public interface IOrderConnectorFactory
{
    IOrderSource CreateSource(ConnectorConfiguration configuration, string apiKey);
    IOrderDestination CreateDestination(ConnectorConfiguration configuration, string apiKey);
}

public enum ConnectorErrorCategory { Transient, Authentication, Permanent, MalformedResponse, Timeout }

public sealed class ConnectorException(ConnectorErrorCategory category, string code, int? httpStatus = null, TimeSpan? retryAfter = null)
    : Exception(code)
{
    public ConnectorErrorCategory Category { get; } = category;
    public string Code { get; } = code;
    public int? HttpStatus { get; } = httpStatus;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool IsRetryable => Category is ConnectorErrorCategory.Transient or ConnectorErrorCategory.Timeout;
}
