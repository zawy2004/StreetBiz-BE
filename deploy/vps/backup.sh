#!/usr/bin/env bash
# Nightly backup on the VPS (cron: see docs/deploy-vps.md): a full database backup and a tarball of
# the uploaded files into ./backups, keeping BACKUP_KEEP_DAYS days. Copy ./backups off the server
# as well (another machine, object storage): a backup on the same disk does not survive losing it.
set -euo pipefail
cd "$(dirname "$0")"
set -a; source .env; set +a

compose() { docker compose -f docker-compose.prod.yml "$@"; }
stamp="$(date -u +%Y%m%d-%H%M)"
keep="${BACKUP_KEEP_DAYS:-14}"
file="StreetBizDB-$stamp.bak"
inside="/var/opt/mssql/backups"
mkdir -p backups

# SQL Server (uid 10001 in the container) writes the file inside its own volume; docker compose cp
# then copies it out owned by the user running this script, so ./backups stays an ordinary folder.
compose exec -T sqlserver mkdir -p "$inside"
compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$SQLSERVER_SA_PASSWORD" -C -b \
  -Q "BACKUP DATABASE [StreetBizDB] TO DISK = N'$inside/$file' WITH INIT, CHECKSUM"
compose cp "sqlserver:$inside/$file" "backups/$file"
compose exec -T sqlserver rm -f "$inside/$file"
# Express has no backup compression, so gzip it here.
gzip -f "backups/$file"

# Uploads live on the named volume streetbiz_uploads.
docker run --rm -v streetbiz_uploads:/data:ro -v "$PWD/backups:/out" alpine:3.20 \
  sh -c 'tar -czf "/out/$1" -C /data . && chown "$2" "/out/$1"' sh "uploads-$stamp.tar.gz" "$(id -u):$(id -g)"

find backups -type f \( -name 'StreetBizDB-*.bak.gz' -o -name 'uploads-*.tar.gz' \) -mtime "+$keep" -delete
echo "Backup $stamp done: $(du -sh backups | cut -f1) in ./backups"
