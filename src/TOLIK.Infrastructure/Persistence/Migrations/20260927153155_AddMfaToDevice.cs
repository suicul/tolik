using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TOLIK.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMfaToDevice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MfaLastVerifiedAt",
                table: "tolik_devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TotpSecret",
                table: "tolik_devices",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MfaLastVerifiedAt",
                table: "tolik_devices");

            migrationBuilder.DropColumn(
                name: "TotpSecret",
                table: "tolik_devices");
        }
    }
}
