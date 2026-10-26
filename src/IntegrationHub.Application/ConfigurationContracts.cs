using System.Text.RegularExpressions;

namespace IntegrationHub.Application;

public sealed record ConnectorConfiguration(
    int SchemaVersion,
    string BaseUrl,
    string AuthMode,
    int PageSize,
    int TimeoutSeconds);

public sealed record ConnectorWriteRequest(
    string Name,
    string Kind,
    string Role,
    ConnectorConfiguration Configuration,
    string? Secret,
    bool Enabled);

public sealed record FieldMapping(string Source, string Destination, bool Required, string? Transform);
public sealed record MappingConfiguration(int SchemaVersion, IReadOnlyList<FieldMapping> Fields);
public sealed record ValidationConfiguration(int SchemaVersion, decimal AmountMinimum);
public sealed record RetryPolicy(int MaxAttempts, int BaseDelaySeconds, int MaxDelaySeconds, int MaxRecoveries);

public sealed record IntegrationWriteRequest(
    string Name,
    Guid SourceConnectorId,
    Guid DestinationConnectorId,
    string EntityType,
    MappingConfiguration Mapping,
    ValidationConfiguration Validation,
    RetryPolicy RetryPolicy,
    bool Enabled);

public sealed record ValidationIssue(string Path, string Code, string Message);

public static class ConfigurationRules
{
    private static readonly Regex FieldPath = new(
        @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static IReadOnlyList<ValidationIssue> ValidateConnector(
        ConnectorWriteRequest request, IReadOnlySet<string> allowedHosts, bool requireHttps)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 160)
            issues.Add(new("name", "invalid_name", "Name must contain 1 to 160 characters."));
        if (request.Kind != "restOrdersV1")
            issues.Add(new("kind", "unsupported_connector", "Only restOrdersV1 is supported."));
        if (request.Role is not ("source" or "destination"))
            issues.Add(new("role", "invalid_role", "Role must be source or destination."));
        if (request.Configuration is null)
        {
            issues.Add(new("configuration", "required", "Configuration is required."));
            return issues;
        }

        if (request.Configuration.SchemaVersion != 1)
            issues.Add(new("configuration.schemaVersion", "unsupported_version", "Schema version must be 1."));
        if (request.Configuration.AuthMode != "apiKeyHeader")
            issues.Add(new("configuration.authMode", "unsupported_auth_mode", "Only apiKeyHeader is supported."));
        if (request.Configuration.PageSize is < 1 or > 500)
            issues.Add(new("configuration.pageSize", "out_of_range", "Page size must be 1 to 500."));
        if (request.Configuration.TimeoutSeconds is < 1 or > 60)
            issues.Add(new("configuration.timeoutSeconds", "out_of_range", "Timeout must be 1 to 60 seconds."));
        if (!Uri.TryCreate(request.Configuration.BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            issues.Add(new("configuration.baseUrl", "invalid_url", "Use an absolute HTTP URL without credentials, query, or fragment."));
        }
        else
        {
            if (requireHttps && uri.Scheme != "https")
                issues.Add(new("configuration.baseUrl", "https_required", "Hosted connector URLs must use HTTPS."));
            if (!allowedHosts.Contains(uri.Host))
                issues.Add(new("configuration.baseUrl", "host_not_allowed", "The connector host is not on the server allowlist."));
        }
        if (request.Secret is { Length: > 1024 })
            issues.Add(new("secret", "too_long", "The secret exceeds the supported length."));
        return issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateIntegration(IntegrationWriteRequest request)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 160)
            issues.Add(new("name", "invalid_name", "Name must contain 1 to 160 characters."));
        if (request.SourceConnectorId == Guid.Empty || request.DestinationConnectorId == Guid.Empty ||
            request.SourceConnectorId == request.DestinationConnectorId)
            issues.Add(new("sourceConnectorId", "invalid_connectors", "Choose two distinct connectors."));
        if (request.EntityType != "orders")
            issues.Add(new("entityType", "unsupported_entity", "Only orders is supported."));

        if (request.Mapping is null || request.Mapping.SchemaVersion != 1 || request.Mapping.Fields is null ||
            request.Mapping.Fields.Count is < 1 or > 50)
        {
            issues.Add(new("mapping", "invalid_mapping", "Version 1 mapping needs 1 to 50 fields."));
        }
        else
        {
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < request.Mapping.Fields.Count; i++)
            {
                var field = request.Mapping.Fields[i];
                if (field is null || !ValidPath(field.Source) || !ValidPath(field.Destination))
                    issues.Add(new($"mapping.fields[{i}]", "invalid_path", "Use dotted field names without arrays or expressions."));
                if (field is not null && !destinations.Add(field.Destination))
                    issues.Add(new($"mapping.fields[{i}].destination", "duplicate_destination", "Destination fields must be unique."));
                if (field?.Transform is not (null or "trim" or "decimal" or "utcDate"))
                    issues.Add(new($"mapping.fields[{i}].transform", "unsupported_transform", "Transform is not supported."));
            }
            if (!new[] { "externalOrderId", "customerEmail", "amount", "placedAtUtc" }.All(destinations.Contains))
                issues.Add(new("mapping.fields", "missing_required_destination", "Map externalOrderId, customerEmail, amount, and placedAtUtc."));
            foreach (var field in request.Mapping.Fields)
                if (field is not null && field.Destination is not ("externalOrderId" or "customerEmail" or "amount" or "placedAtUtc"))
                    issues.Add(new("mapping.fields", "unsupported_destination", "Only the four order destination fields are supported."));
        }

        if (request.Validation is null || request.Validation.SchemaVersion != 1 || request.Validation.AmountMinimum < 0)
            issues.Add(new("validation", "invalid_validation", "Version 1 validation needs a nonnegative amount minimum."));
        if (request.RetryPolicy is null || request.RetryPolicy.MaxAttempts is < 1 or > 10 ||
            request.RetryPolicy.BaseDelaySeconds is < 1 or > 300 ||
            request.RetryPolicy.MaxDelaySeconds is < 1 or > 3600 ||
            request.RetryPolicy.MaxDelaySeconds < request.RetryPolicy.BaseDelaySeconds ||
            request.RetryPolicy.MaxRecoveries is < 0 or > 10)
            issues.Add(new("retryPolicy", "invalid_retry_policy", "Retry limits are outside supported ranges."));
        return issues;
    }

    private static bool ValidPath(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 120 && FieldPath.IsMatch(value);
}
