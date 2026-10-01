using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TOLIK.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IdentityLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tolik_users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tolik_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tolik_devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PublicKey = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tolik_devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tolik_devices_tolik_users_UserId",
                        column: x => x.UserId,
                        principalTable: "tolik_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tolik_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tolik_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tolik_sessions_tolik_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "tolik_devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tolik_sessions_tolik_users_UserId",
                        column: x => x.UserId,
                        principalTable: "tolik_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tolik_devices_InstallationId",
                table: "tolik_devices",
                column: "InstallationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tolik_devices_UserId",
                table: "tolik_devices",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_tolik_sessions_DeviceId",
                table: "tolik_sessions",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_tolik_sessions_TokenHash",
                table: "tolik_sessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tolik_sessions_UserId",
                table: "tolik_sessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_tolik_users_Role",
                table: "tolik_users",
                column: "Role",
                unique: true,
                filter: "\"Role\" = 'Owner'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tolik_sessions");

            migrationBuilder.DropTable(
                name: "tolik_devices");

            migrationBuilder.DropTable(
                name: "tolik_users");
        }
    }
}
