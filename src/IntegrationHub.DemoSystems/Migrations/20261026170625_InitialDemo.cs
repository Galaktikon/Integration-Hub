using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IntegrationHub.DemoSystems.Migrations
{
    /// <inheritdoc />
    public partial class InitialDemo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "demo");

            migrationBuilder.CreateTable(
                name: "destination_order",
                schema: "demo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRecordId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    ExternalOrderId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CustomerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PlacedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_destination_order", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "source_order",
                schema: "demo",
                columns: table => new
                {
                    Sequence = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    OrderNumber = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CustomerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PlacedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_order", x => x.Sequence);
                });

            migrationBuilder.CreateIndex(
                name: "IX_destination_order_IdempotencyKey",
                schema: "demo",
                table: "destination_order",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_destination_order_IntegrationId_SourceRecordId",
                schema: "demo",
                table: "destination_order",
                columns: new[] { "IntegrationId", "SourceRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_order_SourceId",
                schema: "demo",
                table: "source_order",
                column: "SourceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "destination_order",
                schema: "demo");

            migrationBuilder.DropTable(
                name: "source_order",
                schema: "demo");
        }
    }
}
