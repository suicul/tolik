using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

# nullable disable

namespace TOLIK.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(TolikDbContext))]
[Migration("20260924090000_MemoryCompaction")]
public partial class MemoryCompaction : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Marks memories folded into a compaction summary; content stays for auditability.
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "CompactedAt",
            table: "tolik_memories",
            type: "timestamp with time zone",
            nullable: true);

        // Compaction passes scan old non-compacted memories per owner.
        migrationBuilder.CreateIndex(
            name: "IX_tolik_memories_OwnerId_CompactedAt",
            table: "tolik_memories",
            columns: new[] { "OwnerId", "CompactedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_tolik_memories_OwnerId_CompactedAt",
            table: "tolik_memories");

        migrationBuilder.DropColumn(
            name: "CompactedAt",
            table: "tolik_memories");
    }
}
