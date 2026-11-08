#!/bin/sh
# Runs once, when the PostgreSQL data directory is first initialised (docker-entrypoint-initdb.d).
# Least privilege:
#   raf_migrator  owns the "raf", "raf_admin" and "hangfire" schemas and runs all DDL (our migrations and the
#                 Hangfire storage install).
#   raf_app       is what the API connects as. DML through default privileges, which migrations narrow further:
#                 price observations are append-only (SELECT, INSERT), so not even a bug in the API can rewrite history.
#   "raf_admin" holds the migration history; the application role cannot see it.
set -eu

: "${MIGRATOR_PASSWORD:?MIGRATOR_PASSWORD must be set}"
: "${APP_PASSWORD:?APP_PASSWORD must be set}"

psql -v ON_ERROR_STOP=1 \
     -v migrator_password="$MIGRATOR_PASSWORD" \
     -v app_password="$APP_PASSWORD" \
     -v db="$POSTGRES_DB" \
     --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
CREATE ROLE raf_migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE raf_app LOGIN PASSWORD :'app_password';

REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO raf_migrator, raf_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE SCHEMA raf AUTHORIZATION raf_migrator;
CREATE SCHEMA raf_admin AUTHORIZATION raf_migrator;
CREATE SCHEMA hangfire AUTHORIZATION raf_migrator;
GRANT USAGE ON SCHEMA raf, hangfire TO raf_app;

ALTER DEFAULT PRIVILEGES FOR ROLE raf_migrator IN SCHEMA raf
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO raf_app;
ALTER DEFAULT PRIVILEGES FOR ROLE raf_migrator IN SCHEMA raf
    GRANT USAGE, SELECT ON SEQUENCES TO raf_app;

-- Hangfire's job storage is read and written at runtime but never altered: DML only.
ALTER DEFAULT PRIVILEGES FOR ROLE raf_migrator IN SCHEMA hangfire
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO raf_app;
ALTER DEFAULT PRIVILEGES FOR ROLE raf_migrator IN SCHEMA hangfire
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO raf_app;
SQL
