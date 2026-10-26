using IntegrationHub.Application;

namespace IntegrationHub.Tests;

public sealed class ConfigurationRulesTests
{
    [Fact]
    public void RejectsUnapprovedConnectorHost()
    {
        var request = new ConnectorWriteRequest("Source", "restOrdersV1", "source",
            new ConnectorConfiguration(1, "http://169.254.169.254/metadata", "apiKeyHeader", 100, 10),
            "key", true);

        var issues = ConfigurationRules.ValidateConnector(request, new HashSet<string> { "demo-systems" }, false);

        Assert.Contains(issues, x => x.Code == "host_not_allowed");
    }

    [Fact]
    public void RejectsDuplicateMappingDestinationsAndUnsupportedTransform()
    {
        var request = new IntegrationWriteRequest("Orders", Guid.NewGuid(), Guid.NewGuid(), "orders",
            new MappingConfiguration(1,
            [new("orderNumber", "externalOrderId", true, null),
             new("total", "amount", true, "decimal"),
             new("other", "amount", false, "executeCode")]),
            new ValidationConfiguration(1, 0), new RetryPolicy(4, 2, 60, 2), false);

        var issues = ConfigurationRules.ValidateIntegration(request);

        Assert.Contains(issues, x => x.Code == "duplicate_destination");
        Assert.Contains(issues, x => x.Code == "unsupported_transform");
    }
}
