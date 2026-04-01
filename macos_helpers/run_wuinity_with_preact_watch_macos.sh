#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PREACT_CSPROJ="$REPO_ROOT/PREACT/PREACTcore/PREACTcore.csproj"
PREACT_UNITY_CONFIG="${PREACT_UNITY_CONFIG:-Debug}"
UNITY_PREACT_EDITOR_DLL_DIR="$REPO_ROOT/WUInity/Assets/PREACT/$PREACT_UNITY_CONFIG/netstandard2.1"

if [ "$PREACT_UNITY_CONFIG" != "Debug" ] && [ "$PREACT_UNITY_CONFIG" != "Release" ]; then
  printf "Invalid PREACT_UNITY_CONFIG: %s (expected Debug or Release)\n" "$PREACT_UNITY_CONFIG" >&2
  exit 1
fi

if [ ! -f "$PREACT_CSPROJ" ]; then
  printf "PREACTcore project not found: %s\n" "$PREACT_CSPROJ" >&2
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  printf "dotnet is required but not found in PATH.\n" >&2
  exit 1
fi

WATCH_PID=""
cleanup() {
  if [ -n "$WATCH_PID" ] && kill -0 "$WATCH_PID" >/dev/null 2>&1; then
    printf "\nStopping PREACT watcher (pid %s)...\n" "$WATCH_PID"
    kill "$WATCH_PID" >/dev/null 2>&1 || true
    wait "$WATCH_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

printf "Starting PREACT auto-build watcher...\n"
printf "Watching: %s\n" "$PREACT_CSPROJ"
printf "Config: %s\n" "$PREACT_UNITY_CONFIG"
printf "Output: %s\n\n" "$UNITY_PREACT_EDITOR_DLL_DIR"

dotnet watch --project "$PREACT_CSPROJ" build -c Debug -o "$UNITY_PREACT_EDITOR_DLL_DIR" &
WATCH_PID=$!

sleep 1
if ! kill -0 "$WATCH_PID" >/dev/null 2>&1; then
  printf "PREACT watcher exited unexpectedly.\n" >&2
  exit 1
fi

printf "Launching Unity with watcher enabled...\n\n"
"$SCRIPT_DIR/run_wuinity_macos.sh"
