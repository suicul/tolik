using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TOLIK.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenesisIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tolik_genesis_identity",
                columns: table => new
                {
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    BornAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GenesisVersion = table.Column<int>(type: "integer", nullable: false),
                    CodeIdentity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    KnowledgeIdentity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LifecycleState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OwnerIdentity = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tolik_genesis_identity", x => x.InstanceId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tolik_genesis_identity_LifecycleState",
                table: "tolik_genesis_identity",
                column: "LifecycleState",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tolik_genesis_identity");
        }
    }
}
