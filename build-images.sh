#!/bin/bash
# Build all Qubic Atlas images in dependency order, then bring the stack up.
set -e
cd "$(dirname "$0")"

echo "==> 1/4  verifier (C++ Qubic Core scorer, self-tests score 321 during build)"
docker build -t qubic-atlas/verifier:latest ./scorer

echo "==> 2/4  api (ASP.NET Core)"
docker build -t qubic-atlas/api:latest ./backend

echo "==> 3/4  worker (distributed Atlas Verifier)"
docker build -t qubic-atlas/worker:latest -f ./backend/AtlasWorker/Dockerfile ./backend

echo "==> 4/4  frontend (Vue 3 + nginx)"
docker build -t qubic-atlas/frontend:latest ./frontend

echo "==> up  (frontend on http://localhost:8080 ; scale workers with --scale worker=N)"
docker compose up -d
docker compose ps
