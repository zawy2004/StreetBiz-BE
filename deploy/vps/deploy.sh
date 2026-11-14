#!/usr/bin/env bash
# Runs on the VPS (called by .github/workflows/deploy-vps.yml, or by hand to roll back):
#
#   ./deploy.sh <image-tag>      e.g. ./deploy.sh 3f2c1ab...   (a commit SHA pushed to GHCR)
#
# Pulls that API image, restarts the stack and waits for https://$API_DOMAIN/health. If the new
# version is not healthy within ~2 minutes it goes back to the previous tag and fails.
set -euo pipefail
cd "$(dirname "$0")"
tag="${1:?usage: ./deploy.sh <image-tag>}"

compose() { docker compose -f docker-compose.prod.yml "$@"; }
set_tag() {
  if grep -q '^IMAGE_TAG=' .env; then sed -i -E "s|^IMAGE_TAG=.*|IMAGE_TAG=$1|" .env
  else echo "IMAGE_TAG=$1" >> .env; fi
}
healthy() {
  local domain; domain="$(grep -E '^API_DOMAIN=' .env | cut -d= -f2-)"
  for _ in $(seq 1 40); do
    # Through Caddy on this machine, with the real certificate and host name.
    if curl -fsS --max-time 5 --resolve "$domain:443:127.0.0.1" "https://$domain/health" >/dev/null 2>&1; then
      return 0
    fi
    sleep 3
  done
  return 1
}

previous="$(grep -E '^IMAGE_TAG=' .env | cut -d= -f2- || true)"
echo "Deploying $tag (previous: ${previous:-none})"
set_tag "$tag"
compose pull api
compose up -d --remove-orphans

if healthy; then
  echo "Healthy on $tag"
  docker image prune -f >/dev/null
  exit 0
fi

echo "New version is not healthy; last API log lines:" >&2
compose logs --tail 60 api >&2 || true
if [ -n "$previous" ] && [ "$previous" != "$tag" ]; then
  echo "Rolling back to $previous" >&2
  set_tag "$previous"
  compose up -d api
  healthy && echo "Rolled back to $previous" >&2
fi
exit 1
