#!/usr/bin/env bash
# Builds the Linux headless server and, optionally, the container image.
#
# Usage:
#   UNITY=/opt/unity/Editor/Unity ./Server/build-server.sh            # build the player only
#   UNITY=... ./Server/build-server.sh --docker                       # build the player and image
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_DIR="${OUTPUT_DIR:-Server/out/linux}"
UNITY="${UNITY:-}"
LOG="${LOG:-Server/out/build.log}"

if [[ -z "${UNITY}" ]]; then
  echo "error: set UNITY to your Unity editor executable" >&2
  echo "  e.g. export UNITY=\"\$HOME/Unity/Hub/Editor/6000.5.9f1/Editor/Unity\"" >&2
  exit 1
fi

mkdir -p "${PROJECT_ROOT}/$(dirname "${LOG}")"

echo "[unseen] building headless Linux server into ${OUTPUT_DIR}"
"${UNITY}" \
  -quit \
  -batchmode \
  -nographics \
  -projectPath "${PROJECT_ROOT}" \
  -executeMethod Unseen.EditorTools.UnseenBuild.BuildLinuxServer \
  -buildOutput "${OUTPUT_DIR}" \
  -logFile "${PROJECT_ROOT}/${LOG}"

echo "[unseen] player build complete"

if [[ "${1:-}" == "--docker" ]]; then
  echo "[unseen] building container image unseen/server:dev"
  docker build \
    -f "${PROJECT_ROOT}/Server/docker/Dockerfile" \
    --build-arg "BUILD_DIR=$(basename "$(dirname "${OUTPUT_DIR}")")/$(basename "${OUTPUT_DIR}")" \
    -t unseen/server:dev \
    "${PROJECT_ROOT}/Server"
  echo "[unseen] image built. Run it with:"
  echo "  docker run --rm -p 7770:7770/udp unseen/server:dev"
fi
