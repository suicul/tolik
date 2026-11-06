using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TOLIK.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMindContinuity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tolik_genesis_seeds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Temperament = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SeededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Activated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tolik_genesis_seeds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tolik_mind_snapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Boredom = table.Column<double>(type: "double precision", nullable: false),
                    Curiosity = table.Column<double>(type: "double precision", nullable: false),
                    Novelty = table.Column<double>(type: "double precision", nullable: false),
                    Frustration = table.Column<double>(type: "double precision", nullable: false),
                    Satisfaction = table.Column<double>(type: "double precision", nullable: false),
                    SocialDrive = table.Column<double>(type: "double precision", nullable: false),
                    CognitiveLoad = table.Column<double>(type: "double precision", nullable: false),
                    ActivityLevel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AttentionTarget = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UncertaintyNote = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    FailurePatternSurfaced = table.Column<bool>(type: "boolean", nullable: false),
                    LastInteraction = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Uncertainty = table.Column<double>(type: "double precision", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tolik_mind_snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tolik_genesis_seed_values",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SeedId = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tolik_genesis_seed_values", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tolik_genesis_seed_values_tolik_genesis_seeds_SeedId",
                        column: x => x.SeedId,
                        principalTable: "tolik_genesis_seeds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tolik_genesis_seed_values_SeedId_Ordinal",
                table: "tolik_genesis_seed_values",
                columns: new[] { "SeedId", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tolik_genesis_seed_values");

            migrationBuilder.DropTable(
                name: "tolik_mind_snapshots");

            migrationBuilder.DropTable(
                name: "tolik_genesis_seeds");
        }
    }
}
