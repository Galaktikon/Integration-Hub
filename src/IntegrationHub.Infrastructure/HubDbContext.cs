using IntegrationHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace IntegrationHub.Infrastructure;

public sealed class HubDbContext(DbContextOptions<HubDbContext> options) : DbContext(options)
{
    public DbSet<Connector> Connectors => Set<Connector>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<IntegrationSchedule> IntegrationSchedules => Set<IntegrationSchedule>();
    public DbSet<ScheduleEvent> ScheduleEvents => Set<ScheduleEvent>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<SyncPage> SyncPages => Set<SyncPage>();
    public DbSet<SyncRecord> SyncRecords => Set<SyncRecord>();
    public DbSet<SyncAttempt> SyncAttempts => Set<SyncAttempt>();
    public DbSet<SyncRunEvent> SyncRunEvents => Set<SyncRunEvent>();
    public DbSet<DestinationIdentity> DestinationIdentities => Set<DestinationIdentity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("hub");

        model.Entity<Connector>(e =>
        {
            e.ToTable("connector");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(160).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            e.Property(x => x.Role).HasMaxLength(20).IsRequired();
            e.Property(x => x.ConfigurationJson).HasColumnType("jsonb");
            e.Property(x => x.RowVersion).IsConcurrencyToken();
            e.HasIndex(x => new { x.Role, x.Enabled });
        });

        model.Entity<Integration>(e =>
        {
            e.ToTable("integration");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(160).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(40).IsRequired();
            e.Property(x => x.MappingJson).HasColumnType("jsonb");
            e.Property(x => x.ValidationJson).HasColumnType("jsonb");
            e.Property(x => x.RetryPolicyJson).HasColumnType("jsonb");
            e.Property(x => x.RowVersion).IsConcurrencyToken();
            e.HasOne<Connector>().WithMany().HasForeignKey(x => x.SourceConnectorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Connector>().WithMany().HasForeignKey(x => x.DestinationConnectorId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<IntegrationSchedule>(e =>
        {
            e.ToTable("integration_schedule");
            e.HasKey(x => x.Id);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
            e.HasOne<Integration>().WithMany().HasForeignKey(x => x.IntegrationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.IntegrationId).IsUnique();
            e.HasIndex(x => x.NextRunAtUtc).HasFilter("\"Enabled\" = true");
            e.ToTable(t => t.HasCheckConstraint("ck_schedule_min_interval", "\"IntervalSeconds\" >= 300"));
        });

        model.Entity<ScheduleEvent>(e =>
        {
            e.ToTable("schedule_event");
            e.HasKey(x => x.Id);
            e.Property(x => x.Outcome).HasMaxLength(40);
            e.HasOne<IntegrationSchedule>().WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<SyncRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.ScheduleId, x.DueAtUtc }).IsUnique();
        });

        model.Entity<SyncRun>(e =>
        {
            e.ToTable("sync_run");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(40);
            e.Property(x => x.TriggerType).HasMaxLength(20);
            e.Property(x => x.ConfigSnapshotJson).HasColumnType("jsonb");
            e.Property(x => x.SourceSnapshotToken).HasMaxLength(256);
            e.Property(x => x.NextPageCursor).HasMaxLength(256);
            e.Property(x => x.LeaseOwner).HasMaxLength(160);
            e.Property(x => x.LastErrorCode).HasMaxLength(100);
            e.Property(x => x.LastErrorCategory).HasMaxLength(60);
            e.HasOne<Integration>().WithMany().HasForeignKey(x => x.IntegrationId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.IntegrationId).IsUnique().HasFilter("\"Status\" IN ('queued', 'running')");
            e.HasIndex(x => new { x.Status, x.CreatedAtUtc });
            e.HasIndex(x => new { x.Status, x.LeaseExpiresAtUtc });
        });

        model.Entity<SyncPage>(e =>
        {
            e.ToTable("sync_page");
            e.HasKey(x => x.Id);
            e.Property(x => x.RequestCursor).HasMaxLength(256);
            e.Property(x => x.NextCursor).HasMaxLength(256);
            e.HasOne<SyncRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RunId, x.PageNumber }).IsUnique();
        });

        model.Entity<SyncRecord>(e =>
        {
            e.ToTable("sync_record");
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceRecordId).HasMaxLength(200);
            e.Property(x => x.SourcePayloadJson).HasColumnType("jsonb");
            e.Property(x => x.SourceVersionHash).HasMaxLength(128);
            e.Property(x => x.IdempotencyKey).HasMaxLength(300);
            e.Property(x => x.Status).HasMaxLength(40);
            e.Property(x => x.DestinationRecordId).HasMaxLength(200);
            e.Property(x => x.ErrorCategory).HasMaxLength(60);
            e.Property(x => x.ErrorCode).HasMaxLength(100);
            e.Property(x => x.SafeErrorMessage).HasMaxLength(500);
            e.HasOne<SyncRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<SyncPage>().WithMany().HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RunId, x.SourceRecordId }).IsUnique();
            e.HasIndex(x => new { x.RunId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.RunId, x.Status, x.NextAttemptAtUtc });
        });

        model.Entity<SyncAttempt>(e =>
        {
            e.ToTable("sync_attempt");
            e.HasKey(x => x.Id);
            e.Property(x => x.Outcome).HasMaxLength(40);
            e.Property(x => x.ErrorCategory).HasMaxLength(60);
            e.Property(x => x.ErrorCode).HasMaxLength(100);
            e.HasOne<SyncRecord>().WithMany().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RecordId, x.AttemptNumber }).IsUnique();
        });

        model.Entity<SyncRunEvent>(e =>
        {
            e.ToTable("sync_run_event");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(80);
            e.Property(x => x.ActorType).HasMaxLength(40);
            e.Property(x => x.SafeDetailsJson).HasColumnType("jsonb");
            e.HasOne<SyncRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RunId, x.OccurredAtUtc });
        });

        model.Entity<DestinationIdentity>(e =>
        {
            e.ToTable("destination_identity");
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceRecordId).HasMaxLength(200);
            e.Property(x => x.DestinationRecordId).HasMaxLength(200);
            e.Property(x => x.LastSourceHash).HasMaxLength(128);
            e.HasOne<Integration>().WithMany().HasForeignKey(x => x.IntegrationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.IntegrationId, x.SourceRecordId }).IsUnique();
        });
    }
}
