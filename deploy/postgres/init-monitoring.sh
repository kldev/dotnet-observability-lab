#!/bin/sh
# Runs once, on the first start of an empty data volume (for an existing volume run it by hand, see README).
# - postgres_exporter role: read-only monitoring (pg_monitor), no access to table data
# - pg_stat_statements: per-query statistics (calls, time, rows) for the PostgreSQL dashboard
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<SQL
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'postgres_exporter') THEN
    CREATE ROLE postgres_exporter LOGIN PASSWORD '${POSTGRES_EXPORTER_PASSWORD}';
  END IF;
END
\$\$;
GRANT pg_monitor TO postgres_exporter;
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
SQL
