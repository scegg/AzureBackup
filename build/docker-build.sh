#!/usr/bin/env bash
# Build the Docker images. Usage: ./build/docker-build.sh [backup|restore|all]
#
# Multi-arch (amd64 + arm64) via Docker buildx.
#   PLATFORMS  target platforms            (default: linux/amd64,linux/arm64)
#   PUSH=1     push to registry            (multi-arch manifests can't load locally)
#   REGISTRY   image prefix, e.g. myacr.azurecr.io/   (trailing slash)
#   TAG        image tag                   (default: local)
#
# Note: a multi-platform build must be pushed (PUSH=1) or exported; `--load`
# only supports a single platform. For a quick local single-arch image:
#   PLATFORMS=linux/amd64 PUSH=0 ./build/docker-build.sh backup
set -euo pipefail

cd "$(dirname "$0")/.."

REGISTRY="${REGISTRY:-}"
TAG="${TAG:-local}"
PLATFORMS="${PLATFORMS:-linux/amd64,linux/arm64}"
PUSH="${PUSH:-0}"
TARGET="${1:-all}"

# --push for registry, otherwise --load (single platform only).
output_flag="--load"
if [[ "$PUSH" == "1" ]]; then
  output_flag="--push"
elif [[ "$PLATFORMS" == *,* ]]; then
  echo "!! Multi-platform build ($PLATFORMS) cannot use --load." >&2
  echo "!! Set PUSH=1 to push, or PLATFORMS=linux/amd64 for a local image." >&2
  exit 2
fi

build_one() {
  local name="$1" dockerfile="$2"
  local image="${REGISTRY}azbackup-${name}:${TAG}"
  echo ">> Building ${image} for ${PLATFORMS}..."
  docker buildx build \
    --platform "${PLATFORMS}" \
    -f "docker/${dockerfile}" \
    -t "${image}" \
    ${output_flag} \
    .
  echo ">> Done: ${image}"
}

case "$TARGET" in
  backup)  build_one backup  backup.Dockerfile ;;
  restore) build_one restore restore.Dockerfile ;;
  all)
    build_one backup  backup.Dockerfile
    build_one restore restore.Dockerfile
    ;;
  *) echo "usage: $0 [backup|restore|all]" >&2; exit 2 ;;
esac
