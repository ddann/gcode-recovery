#!/usr/bin/env bash
# Deploys the community server to a Docker host over SSH and checks its health.
#   scripts/deploy-community-server.sh [user@host] [remote-dir]
set -euo pipefail
HOST="${1:-ddann@192.168.30.100}"
DIR="${2:-/home/ddann/gcode-recovery-community}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

echo "==> Syncing sources to $HOST:$DIR"
ssh "$HOST" "mkdir -p '$DIR'"
rsync -az --delete --exclude bin/ --exclude obj/ \
  --include 'Directory.Build.props' \
  --include 'src/' --include 'src/GcodeRecovery.Telemetry/***' --include 'src/GcodeRecovery.Server/***' \
  --include 'deploy/' --include 'deploy/community-server/***' \
  --exclude '*' "$ROOT/" "$HOST:$DIR/"

echo "==> Building and starting the container"
ssh "$HOST" "cd '$DIR/deploy/community-server' && docker compose up -d --build 2>&1 | tail -5"

echo "==> Health check"
ssh "$HOST" "for i in \$(seq 1 30); do curl -fsS http://127.0.0.1:4090/healthz && exit 0; sleep 1; done; docker logs --tail 50 gcode-recovery-community; exit 1"
echo
