#!/usr/bin/env bash
# Nightly backup on the VPS (cron: see docs/deploy-vps.md): a full database backup and a tarball of
# the uploaded files into ./backups, keeping BACKUP_KEEP_DAYS days. Copy ./backups off the server
# as well (another machine, object storage): a backup on the same disk does not survive losing it.
set -euo pipefail
cd "$(dirname "$0")"
set -a; source .env; set +a

stamp="$(date -u +%Y%m%d-%H%M)"
keep="${BACKUP_KEEP_DAYS:-14}"
mkdir -p backups

# SQL Server writes the file itself, into the ./backups bind mount (owned by uid 10001, the
# mssql user). Express has no backup compression, so gzip it afterwards.
docker compose -f docker-compose.prod.yml exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$SQLSERVER_SA_PASSWORD" -C -b \
  -Q "BACKUP DATABASE [StreetBizDB] TO DISK = N'/var/opt/mssql/backups/StreetBizDB-$stamp.bak' WITH INIT, CHECKSUM"
gzip -f "backups/StreetBizDB-$stamp.bak"

# Uploads live on the named volume streetbiz_uploads.
docker run --rm -v streetbiz_uploads:/data:ro -v "$PWD/backups:/out" alpine:3.20 \
  tar -czf "/out/uploads-$stamp.tar.gz" -C /data .

find backups -type f \( -name 'StreetBizDB-*.bak.gz' -o -name 'uploads-*.tar.gz' \) -mtime "+$keep" -delete
echo "Backup $stamp done: $(du -sh backups | cut -f1) in ./backups"
