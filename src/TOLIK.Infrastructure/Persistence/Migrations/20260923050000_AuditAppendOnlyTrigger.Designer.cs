using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TOLIK.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TolikDbContext))]
[Migration("20260923050000_AuditAppendOnlyTrigger")]
partial class AuditAppendOnlyTrigger
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.4");
    }
}
