using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntegrationHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialHub : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "hub");

            migrationBuilder.CreateTable(
                name: "connector",
                schema: "hub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ConfigurationJson = table.Column<string>(type: "jsonb", nullable: false),
                    EncryptedSecret = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connector", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "integration",
                schema: "hub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SourceConnectorId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationConnectorId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MappingJson = table.Column<string>(type: "jsonb", nullable: false),
                    ValidationJson = table.Column<string>(type: "jsonb", nullable: false),
                    RetryPolicyJson = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_integration_connector_DestinationConnectorId",
                        column: x => x.DestinationConnectorId,
                        principalSchema: "hub",
                        principalTable: "connector",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_integration_connector_SourceConnectorId",
                        column: x => x.SourceConnectorId,
                        principalSchema: "hub",
                        principalTable: "connector",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "destination_identity",
                schema: "hub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRecordId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DestinationRecordId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LastSourceHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastSyncedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_destination_identity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_destination_identity_integration_IntegrationId",
                        column: x => x.IntegrationId,
                        principalSchema: "hub",
                        principalTable: "integration",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "integration_schedule",
                schema: "hub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    IntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    NextRunAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastHandledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_schedule", x => x.Id);
                    table.CheckConstraint("ck_schedule_min_interval", "\"IntervalSeconds\" >= 300");
                    table.ForeignKey(
                        name: "FK_integration_schedule_integration_IntegrationId",
                        column: x => x.IntegrationId,
                        principalSchema: "hub",
                        principalTable: "integration",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_run",
                schema: "hub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TriggerType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ConfigSnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    SourceSnapshotToken = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NextPageCursor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SourceComplete = table.Column<bool>(type: "boolean", nullable: false),
                    SourceAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextSourceAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RecoveryGeneration = table.Column<int>(type: "integer", nullable: false),
                    CancellationRequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeadlineAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_run", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_run_integration_IntegrationId",
                        column: x => x.IntegrationId,
                        principalSchema: "hub",
                        principalTable: "integration",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "schedule_event",
                schema: "hub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_event", x => x.Id);
                    table.ForeignKey(
                        name: "FK_schedule_event_integration_schedule_ScheduleId",
                        column: x => x.ScheduleId,
                        principalSchema: "hub",
                        principalTable: "integration_schedule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_schedule_event_sync_run_RunId",
                        column: x => x.RunId,
                        principalSchema: "hub",
                        principalTable: "sync_run",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "sync_page",
                schema: "hub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestCursor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NextCursor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PageNumber = table.Column<int>(type: "integer", nullable: false),
                    RecordCount = table.Column<int>(type: "integer", nullable: false),
                    PersistedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_page", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_page_sync_run_RunId",
                        column: x => x.RunId,
                        principalSchema: "hub",
                        principalTable: "sync_run",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_run_event",
                schema: "hub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SafeDetailsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_run_event", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_run_event_sync_run_RunId",
                        column: x => x.RunId,
                        principalSchema: "hub",
                        principalTable: "sync_run",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_record",
                schema: "hub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRecordId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourcePayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    SourceVersionHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DestinationRecordId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ErrorCategory = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SafeErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_record", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_record_sync_page_PageId",
                        column: x => x.PageId,
                        principalSchema: "hub",
                        principalTable: "sync_page",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sync_record_sync_run_RunId",
                        column: x => x.RunId,
                        principalSchema: "hub",
                        principalTable: "sync_run",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_attempt",
                schema: "hub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    RecoveryGeneration = table.Column<int>(type: "integer", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    ErrorCategory = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_attempt", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_attempt_sync_record_RecordId",
                        column: x => x.RecordId,
                        principalSchema: "hub",
                        principalTable: "sync_record",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_connector_Role_Enabled",
                schema: "hub",
                table: "connector",
                columns: new[] { "Role", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_destination_identity_IntegrationId_SourceRecordId",
                schema: "hub",
                table: "destination_identity",
                columns: new[] { "IntegrationId", "SourceRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integration_DestinationConnectorId",
                schema: "hub",
                table: "integration",
                column: "DestinationConnectorId");

            migrationBuilder.CreateIndex(
                name: "IX_integration_SourceConnectorId",
                schema: "hub",
                table: "integration",
                column: "SourceConnectorId");

            migrationBuilder.CreateIndex(
                name: "IX_integration_schedule_IntegrationId",
                schema: "hub",
                table: "integration_schedule",
                column: "IntegrationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integration_schedule_NextRunAtUtc",
                schema: "hub",
                table: "integration_schedule",
                column: "NextRunAtUtc",
                filter: "\"Enabled\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_schedule_event_RunId",
                schema: "hub",
                table: "schedule_event",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_schedule_event_ScheduleId_DueAtUtc",
                schema: "hub",
                table: "schedule_event",
                columns: new[] { "ScheduleId", "DueAtUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_attempt_RecordId_AttemptNumber",
                schema: "hub",
                table: "sync_attempt",
                columns: new[] { "RecordId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_page_RunId_PageNumber",
                schema: "hub",
                table: "sync_page",
                columns: new[] { "RunId", "PageNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_record_PageId",
                schema: "hub",
                table: "sync_record",
                column: "PageId");

            migrationBuilder.CreateIndex(
                name: "IX_sync_record_RunId_IdempotencyKey",
                schema: "hub",
                table: "sync_record",
                columns: new[] { "RunId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_record_RunId_SourceRecordId",
                schema: "hub",
                table: "sync_record",
                columns: new[] { "RunId", "SourceRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_record_RunId_Status_NextAttemptAtUtc",
                schema: "hub",
                table: "sync_record",
                columns: new[] { "RunId", "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_sync_run_IntegrationId",
                schema: "hub",
                table: "sync_run",
                column: "IntegrationId",
                unique: true,
                filter: "\"Status\" IN ('queued', 'running')");

            migrationBuilder.CreateIndex(
                name: "IX_sync_run_Status_CreatedAtUtc",
                schema: "hub",
                table: "sync_run",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_sync_run_Status_LeaseExpiresAtUtc",
                schema: "hub",
                table: "sync_run",
                columns: new[] { "Status", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_sync_run_event_RunId_OccurredAtUtc",
                schema: "hub",
                table: "sync_run_event",
                columns: new[] { "RunId", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "destination_identity",
                schema: "hub");

            migrationBuilder.DropTable(
                name: "schedule_event",
                schema: "hub");

            migrationBuilder.DropTable(
                name: "sync_attempt",
                schema: "hub");

            migrationBuilder.DropTable(
                name: "sync_run_event",
                schema: "hub");

            migrationBuilder.DropTable(
                name: "integration_schedule",
                schema: "hub");

            migrationBuilder.DropTable(
                name: "sync_record",
                schema: "hub");

            migrationBuilder.DropTable(
                name: "sync_page",
                schema: "hub");

            migrationBuilder.DropTable(
                name: "sync_run",
                schema: "hub");

            migrationBuilder.DropTable(
                name: "integration",
                schema: "hub");

            migrationBuilder.DropTable(
                name: "connector",
                schema: "hub");
        }
    }
}
