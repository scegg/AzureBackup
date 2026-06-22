#!/usr/bin/env bash
# Build and test the solution locally (no Docker).
set -euo pipefail

cd "$(dirname "$0")/.."

CONFIG="${1:-Release}"

echo ">> Restoring..."
dotnet restore

echo ">> Building ($CONFIG)..."
dotnet build -c "$CONFIG" --no-restore

echo ">> Testing..."
dotnet test -c "$CONFIG" --no-build

echo ">> Done."
