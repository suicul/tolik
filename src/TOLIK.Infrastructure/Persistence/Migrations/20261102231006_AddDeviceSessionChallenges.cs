using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TOLIK.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceSessionChallenges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tolik_device_session_challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tolik_device_session_challenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tolik_device_session_challenges_tolik_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "tolik_devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tolik_device_session_challenges_DeviceId",
                table: "tolik_device_session_challenges",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_tolik_device_session_challenges_ExpiresAt",
                table: "tolik_device_session_challenges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_tolik_device_session_challenges_TokenHash",
                table: "tolik_device_session_challenges",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tolik_device_session_challenges");
        }
    }
}
