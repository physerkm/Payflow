#!/bin/sh
# Monitoring access for the observability profile: the pg_stat_statements extension (slow-query statistics)
# and a monitoring role for postgres-exporter. pg_monitor grants statistics views only - no business tables.
# Idempotent, so it can also be run on an existing database:
#   docker compose exec postgres sh /docker-entrypoint-initdb.d/02-monitoring.sh
set -e
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
  CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
  DO \$\$
  BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'payflow_monitor') THEN
      CREATE ROLE payflow_monitor LOGIN PASSWORD '${PAYFLOW_MONITOR_PASSWORD:-payflow_monitor}' NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;
  END
  \$\$;
  GRANT pg_monitor TO payflow_monitor;
  GRANT CONNECT ON DATABASE "${POSTGRES_DB}" TO payflow_monitor;
EOSQL
