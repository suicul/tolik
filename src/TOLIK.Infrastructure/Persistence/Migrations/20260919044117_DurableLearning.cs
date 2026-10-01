using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TOLIK.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableLearning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tolik_learned_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rule = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    Source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tolik_learned_rules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tolik_memories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Content = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    Source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tolik_memories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tolik_learned_rules_OwnerId_Confidence_CreatedAt",
                table: "tolik_learned_rules",
                columns: new[] { "OwnerId", "Confidence", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tolik_memories_OwnerId_Scope_CreatedAt",
                table: "tolik_memories",
                columns: new[] { "OwnerId", "Scope", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tolik_learned_rules");

            migrationBuilder.DropTable(
                name: "tolik_memories");
        }
    }
}
