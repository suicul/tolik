using Microsoft.EntityFrameworkCore.Migrations;

namespace TOLIK.Infrastructure.Persistence.Migrations;

public partial class AuditAppendOnlyTrigger : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION tolik_reject_audit_mutation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'audit_events is append-only';
            END;
            $$;

            DROP TRIGGER IF EXISTS tolik_audit_append_only ON audit_events;
            CREATE TRIGGER tolik_audit_append_only
            BEFORE UPDATE OR DELETE ON audit_events
            FOR EACH ROW EXECUTE FUNCTION tolik_reject_audit_mutation();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS tolik_audit_append_only ON audit_events;
            DROP FUNCTION IF EXISTS tolik_reject_audit_mutation();
            """);
    }
}
