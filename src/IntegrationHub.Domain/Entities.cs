namespace IntegrationHub.Domain;

public static class ConnectorRoles
{
    public const string Source = "source";
    public const string Destination = "destination";
}

public static class RunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string CompletedWithErrors = "completed_with_errors";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed class Connector
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "restOrdersV1";
    public string Role { get; set; } = "";
    public string ConfigurationJson { get; set; } = "{}";
    public string? EncryptedSecret { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid RowVersion { get; set; } = Guid.NewGuid();
}

public sealed class Integration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid SourceConnectorId { get; set; }
    public Guid DestinationConnectorId { get; set; }
    public string EntityType { get; set; } = "orders";
    public string MappingJson { get; set; } = "{}";
    public string ValidationJson { get; set; } = "{}";
    public string RetryPolicyJson { get; set; } = "{}";
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid RowVersion { get; set; } = Guid.NewGuid();
}

public sealed class IntegrationSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IntegrationId { get; set; }
    public int IntervalSeconds { get; set; }
    public DateTimeOffset NextRunAtUtc { get; set; }
    public DateTimeOffset? LastHandledAtUtc { get; set; }
    public bool Enabled { get; set; }
    public Guid RowVersion { get; set; } = Guid.NewGuid();
}

public sealed class ScheduleEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScheduleId { get; set; }
    public DateTimeOffset DueAtUtc { get; set; }
    public string Outcome { get; set; } = "";
    public Guid? RunId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SyncRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IntegrationId { get; set; }
    public string Status { get; set; } = RunStatuses.Queued;
    public string TriggerType { get; set; } = "manual";
    public string ConfigSnapshotJson { get; set; } = "{}";
    public string? SourceSnapshotToken { get; set; }
    public string? NextPageCursor { get; set; }
    public bool SourceComplete { get; set; }
    public int SourceAttemptCount { get; set; }
    public DateTimeOffset? NextSourceAttemptAtUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public long LeaseGeneration { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public int RecoveryGeneration { get; set; }
    public DateTimeOffset? CancellationRequestedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public DateTimeOffset? DeadlineAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorCategory { get; set; }
}

public sealed class SyncPage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public string? RequestCursor { get; set; }
    public string? NextCursor { get; set; }
    public int PageNumber { get; set; }
    public int RecordCount { get; set; }
    public DateTimeOffset PersistedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SyncRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Guid PageId { get; set; }
    public string SourceRecordId { get; set; } = "";
    public string SourcePayloadJson { get; set; } = "{}";
    public string SourceVersionHash { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string Status { get; set; } = "pending";
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public string? DestinationRecordId { get; set; }
    public string? ErrorCategory { get; set; }
    public string? ErrorCode { get; set; }
    public string? SafeErrorMessage { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
}

public sealed class SyncAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RecordId { get; set; }
    public int AttemptNumber { get; set; }
    public int RecoveryGeneration { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string Outcome { get; set; } = "started";
    public int? HttpStatus { get; set; }
    public string? ErrorCategory { get; set; }
    public string? ErrorCode { get; set; }
    public int? DurationMs { get; set; }
}

public sealed class SyncRunEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public string EventType { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string ActorType { get; set; } = "worker";
    public string SafeDetailsJson { get; set; } = "{}";
}

public sealed class DestinationIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IntegrationId { get; set; }
    public string SourceRecordId { get; set; } = "";
    public string DestinationRecordId { get; set; } = "";
    public string LastSourceHash { get; set; } = "";
    public DateTimeOffset LastSyncedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
