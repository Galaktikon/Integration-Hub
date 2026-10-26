using System.Text.Json;
using IntegrationHub.Application;

namespace IntegrationHub.Tests;

public sealed class OrderMappingTests
{
    private static readonly MappingConfiguration Mapping = new(1,
    [new("orderNumber", "externalOrderId", true, "trim"),
     new("customer.email", "customerEmail", true, "trim"),
     new("total", "amount", true, "decimal"),
     new("placedAt", "placedAtUtc", true, "utcDate")]);

    [Fact]
    public void MapsNestedFieldsAndConvertsToUtc()
    {
        using var source = JsonDocument.Parse("""
            {"orderNumber":" ORD-1 ","customer":{"email":" user@example.test "},
             "total":"12.50","placedAt":"2026-01-01T07:00:00-05:00"}
            """);
        var result = OrderMapping.Map(source.RootElement, Mapping, new ValidationConfiguration(1, 0));
        Assert.True(result.Valid);
        Assert.Equal("ORD-1", result.Order!.ExternalOrderId);
        Assert.Equal("user@example.test", result.Order.CustomerEmail);
        Assert.Equal(12.50m, result.Order.Amount);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), result.Order.PlacedAtUtc);
    }

    [Fact]
    public void ReportsMissingNestedFieldAndConversionFailure()
    {
        using var missing = JsonDocument.Parse("""
            {"orderNumber":"ORD-1","customer":{},"total":12,"placedAt":"2026-01-01T12:00:00Z"}
            """);
        Assert.Equal("required_field_missing", OrderMapping.Map(missing.RootElement, Mapping,
            new ValidationConfiguration(1, 0)).Code);
        using var invalid = JsonDocument.Parse("""
            {"orderNumber":"ORD-1","customer":{"email":"user@example.test"},"total":"bad",
             "placedAt":"2026-01-01T12:00:00Z"}
            """);
        Assert.Equal("conversion_failed", OrderMapping.Map(invalid.RootElement, Mapping,
            new ValidationConfiguration(1, 0)).Code);
    }
}
