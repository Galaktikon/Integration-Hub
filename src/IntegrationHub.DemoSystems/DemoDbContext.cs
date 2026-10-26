using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IntegrationHub.DemoSystems;

public sealed class SourceOrder
{
    public int Sequence { get; set; }
    public string SourceId { get; set; } = "";
    public string OrderNumber { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public decimal Total { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
}

public sealed class DestinationOrder
{
    public Guid Id { get; set; }
    public Guid IntegrationId { get; set; }
    public string SourceRecordId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string ExternalOrderId { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTimeOffset PlacedAtUtc { get; set; }
}

public sealed class DemoDbContext(DbContextOptions<DemoDbContext> options) : DbContext(options)
{
    public DbSet<SourceOrder> SourceOrders => Set<SourceOrder>();
    public DbSet<DestinationOrder> DestinationOrders => Set<DestinationOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("demo");
        modelBuilder.Entity<SourceOrder>(e =>
        {
            e.ToTable("source_order");
            e.HasKey(x => x.Sequence);
            e.Property(x => x.SourceId).HasMaxLength(80);
            e.HasIndex(x => x.SourceId).IsUnique();
            e.Property(x => x.OrderNumber).HasMaxLength(80);
            e.Property(x => x.CustomerEmail).HasMaxLength(320);
            e.Property(x => x.Total).HasPrecision(18, 2);
        });
        modelBuilder.Entity<DestinationOrder>(e =>
        {
            e.ToTable("destination_order");
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceRecordId).HasMaxLength(80);
            e.Property(x => x.IdempotencyKey).HasMaxLength(180);
            e.HasIndex(x => new { x.IntegrationId, x.SourceRecordId }).IsUnique();
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.Property(x => x.ExternalOrderId).HasMaxLength(80);
            e.Property(x => x.CustomerEmail).HasMaxLength(320);
            e.Property(x => x.Amount).HasPrecision(18, 2);
        });
    }
}

public sealed class DesignTimeDemoDbContextFactory : IDesignTimeDbContextFactory<DemoDbContext>
{
    public DemoDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("INTEGRATION_TEST_DATABASE")
            ?? "Host=localhost;Port=5432;Database=integration_hub;Username=hub;Password=local_only_password";
        return new DemoDbContext(new DbContextOptionsBuilder<DemoDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "demo")).Options);
    }
}
