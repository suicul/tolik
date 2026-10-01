using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TOLIK.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_OccurredAt",
                table: "audit_events",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");
        }
    }
}
