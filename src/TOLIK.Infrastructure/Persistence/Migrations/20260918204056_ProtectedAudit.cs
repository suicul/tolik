using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TOLIK.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProtectedAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Capability",
                table: "audit_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntegrityHash",
                table: "audit_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PreviousHash",
                table: "audit_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Resource",
                table: "audit_events",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Result",
                table: "audit_events",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Risk",
                table: "audit_events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionId",
                table: "audit_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Severity",
                table: "audit_events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Capability",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "IntegrityHash",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "PreviousHash",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "Resource",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "Result",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "Risk",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "Severity",
                table: "audit_events");
        }
    }
}
