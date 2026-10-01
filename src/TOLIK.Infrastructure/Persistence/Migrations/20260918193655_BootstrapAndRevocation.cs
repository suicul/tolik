using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TOLIK.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BootstrapAndRevocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tolik_bootstraps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tolik_bootstraps", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tolik_bootstraps_CodeHash",
                table: "tolik_bootstraps",
                column: "CodeHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tolik_bootstraps");
        }
    }
}
