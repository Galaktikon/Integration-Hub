using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IntegrationHub.Infrastructure;

public sealed class DesignTimeHubDbContextFactory : IDesignTimeDbContextFactory<HubDbContext>
{
    public HubDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Hub")
            ?? "Host=localhost;Database=integration_hub;Username=hub;Password=local_only_password";
        return new HubDbContext(new DbContextOptionsBuilder<HubDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "hub")).Options);
    }
}
