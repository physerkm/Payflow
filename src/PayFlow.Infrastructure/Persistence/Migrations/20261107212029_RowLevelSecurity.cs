using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayFlow.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Defense in depth (ADR-003): PostgreSQL Row-Level Security on tenant-owned tables.
    /// Policies compare tenant_id with the session setting app.tenant_id, which the application sets per command.
    /// They are enforced for the non-owner application role (payflow_app); the owner/migration role bypasses
    /// RLS because FORCE ROW LEVEL SECURITY is intentionally not used (system jobs use that role for scans).
    /// </summary>
    public partial class RowLevelSecurity : Migration
    {
        private static readonly string[] TenantTables =
        [
            "customers", "products", "orders", "order_items", "payments", "payment_attempts", "refunds",
            "notifications", "idempotency_records",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY tenant_isolation ON {table}
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                    """);
            }

            // Least privilege for the application role, if it exists (created by infrastructure/docker/postgres/init).
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'payflow_app') THEN
                        GRANT USAGE ON SCHEMA public TO payflow_app;
                        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO payflow_app;
                        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO payflow_app;
                        -- The audit trail is append-only for the application.
                        REVOKE UPDATE, DELETE ON audit_logs FROM payflow_app;
                        -- Schema history is managed by the migrator only.
                        REVOKE INSERT, UPDATE, DELETE ON __ef_migrations_history FROM payflow_app;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
                migrationBuilder.Sql($"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
            }
        }
    }
}
