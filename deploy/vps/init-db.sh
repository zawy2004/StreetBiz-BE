#!/usr/bin/env bash
# One-time database setup on the VPS: builds StreetBizDB from db/StreetBiz_SQL_Server.sql and
# creates the API's own login. Safe to run again: an existing StreetBizDB is never dropped,
# recreated or re-seeded -- only the app login and its permissions are (re)applied.
#
#   ./init-db.sh                    schema only (production)
#   ./init-db.sh --with-demo-seed   schema + demo accounts/scenario (demo servers only)
set -euo pipefail
cd "$(dirname "$0")"
set -a; source .env; set +a

compose() { docker compose -f docker-compose.prod.yml "$@"; }
sql() {
  compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa \
    -P "$SQLSERVER_SA_PASSWORD" -C -I -b "$@"
}
quote() { printf "%s" "${1//\'/\'\'}"; }   # SQL string literal escaping

compose up -d sqlserver
echo "Waiting for SQL Server..."
for _ in $(seq 1 60); do
  if sql -Q "SELECT 1" >/dev/null 2>&1; then break; fi
  sleep 2
done
sql -Q "SELECT 1" >/dev/null

exists=$(sql -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name = N'StreetBizDB'" | tr -d '[:space:]')
if [ "$exists" != "0" ]; then
  echo "StreetBizDB already exists: schema left untouched (apply db/StreetBiz_Upgrade_*.sql by hand)."
else
  echo "Creating StreetBizDB..."
  sql -Q "CREATE DATABASE [StreetBizDB]"
  sql -d StreetBizDB -i /scripts/StreetBiz_SQL_Server.sql
  if [ "${1:-}" = "--with-demo-seed" ]; then
    echo "Loading the demo seed (public demo password: change or delete those accounts)."
    sql -d StreetBizDB -i /scripts/StreetBiz_Demo_Seed.sql
  fi
fi

user="${DB_APP_USER:-streetbiz_app}"
password="$(quote "${DB_APP_PASSWORD:?Set DB_APP_PASSWORD in .env}")"
echo "Applying the app login '$user' (read/write data on dbo, no schema changes)..."
sql -Q "IF SUSER_ID(N'$user') IS NULL CREATE LOGIN [$user] WITH PASSWORD = N'$password', CHECK_POLICY = ON;
ELSE ALTER LOGIN [$user] WITH PASSWORD = N'$password';"
# ALTER USER ... WITH LOGIN re-links the user after a RESTORE from another server (orphaned SID).
sql -d StreetBizDB -Q "IF USER_ID(N'$user') IS NULL CREATE USER [$user] FOR LOGIN [$user];
ELSE ALTER USER [$user] WITH LOGIN = [$user];
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE, REFERENCES ON SCHEMA::dbo TO [$user];"
echo "Done."
