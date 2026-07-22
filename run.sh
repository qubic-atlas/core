#!/bin/bash
# One-shot local run: build images (if needed) and start the full Atlas stack via docker compose.
# api + frontend + clickhouse + workers. Open http://localhost:8080
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

echo "==> starting Qubic Atlas (docker compose) — http://localhost:8080"
docker compose up -d --build "$@"
echo "==> up. logs: docker compose logs -f api"
