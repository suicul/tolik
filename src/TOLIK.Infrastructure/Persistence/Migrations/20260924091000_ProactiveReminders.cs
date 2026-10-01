using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TOLIK.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(TolikDbContext))]
[Migration("20260924091000_ProactiveReminders")]
public partial class ProactiveReminders : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "tolik_proactive",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Text = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                Priority = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tolik_proactive", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_tolik_proactive_UserId_DeliveredAt_DueAt",
            table: "tolik_proactive",
            columns: new[] { "UserId", "DeliveredAt", "DueAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "tolik_proactive");
    }
}
