#!/usr/bin/env bash
# Launches the Unseen headless server. Every knob is an environment variable so the same image can
# run a 64-entity production match, a 16-entity smoke test or a soak test with a fixed seed.
set -euo pipefail

PORT="${UNSEEN_PORT:-7770}"
ENTITIES="${UNSEEN_ENTITIES:-64}"
SEED="${UNSEEN_SEED:-20260824}"
LOG_FILE="${UNSEEN_LOG_FILE:-/dev/stdout}"

echo "[unseen] starting server: port=${PORT} entities=${ENTITIES} seed=${SEED}"

exec /opt/unseen/unseen-server \
  -batchmode \
  -nographics \
  -server \
  -port "${PORT}" \
  -entities "${ENTITIES}" \
  -seed "${SEED}" \
  -logFile "${LOG_FILE}" \
  "$@"
