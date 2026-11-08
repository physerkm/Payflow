#!/bin/sh
# Creates the least-privileged application role. Tables are owned by POSTGRES_USER (used only by the migrator
# and for cross-tenant system lookups); the API and Worker connect as payflow_app, for which Row-Level Security
# policies are enforced. Default privileges grant access to tables created later by migrations.
set -e
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
  CREATE ROLE payflow_app LOGIN PASSWORD '${PAYFLOW_APP_PASSWORD:-payflow_app}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
  GRANT CONNECT ON DATABASE "${POSTGRES_DB}" TO payflow_app;
  GRANT USAGE ON SCHEMA public TO payflow_app;
  ALTER DEFAULT PRIVILEGES FOR ROLE "${POSTGRES_USER}" IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO payflow_app;
  ALTER DEFAULT PRIVILEGES FOR ROLE "${POSTGRES_USER}" IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO payflow_app;
EOSQL
