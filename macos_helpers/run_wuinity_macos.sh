#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
UNITY_PROJECT_DIR="$REPO_ROOT/WUInity"
UNITY_BIN="/Applications/Unity/Hub/Editor/6000.3.10f1/Unity.app/Contents/MacOS/Unity"

# Default SUMO framework install path from official pkg.
DEFAULT_SUMO_HOME="/Library/Frameworks/EclipseSUMO.framework/Versions/Current/EclipseSUMO/share/sumo"
DEFAULT_PROJ_LIB="/opt/homebrew/opt/proj/share/proj"
export PREACT_UNITY_CONFIG="${PREACT_UNITY_CONFIG:-Debug}"

if [ "$(uname -s)" != "Darwin" ]; then
  printf "This helper is for macOS only.\n" >&2
  exit 1
fi

if [ ! -d "$UNITY_PROJECT_DIR" ]; then
  printf "Unity project not found: %s\n" "$UNITY_PROJECT_DIR" >&2
  exit 1
fi

if [ ! -x "$UNITY_BIN" ]; then
  printf "Unity editor not found at: %s\n" "$UNITY_BIN" >&2
  printf "Install Unity 6000.3.10f1 first.\n" >&2
  exit 1
fi

if [ -z "${SUMO_HOME:-}" ]; then
  export SUMO_HOME="$DEFAULT_SUMO_HOME"
fi

if [ ! -x "$SUMO_HOME/bin/sumo" ]; then
  printf "SUMO binary not found at: %s/bin/sumo\n" "$SUMO_HOME" >&2
  printf "Set SUMO_HOME correctly, then rerun.\n" >&2
  exit 1
fi 

export PATH="$SUMO_HOME/bin:$PATH"

# Ensure UTF-8 locale so libsumo string marshalling does not fail on macOS.
export LANG="${LANG:-en_US.UTF-8}"
export LC_ALL="${LC_ALL:-en_US.UTF-8}"

if [ -z "${PROJ_LIB:-}" ] && [ -f "$DEFAULT_PROJ_LIB/proj.db" ]; then
  export PROJ_LIB="$DEFAULT_PROJ_LIB"
fi

if [ -z "${PROJ_LIB:-}" ] || [ ! -f "$PROJ_LIB/proj.db" ]; then
  printf "PROJ_LIB is invalid or proj.db missing.\n" >&2
  printf "Set PROJ_LIB to a folder containing proj.db, e.g. %s\n" "$DEFAULT_PROJ_LIB" >&2
  exit 1
fi

if [ -f "$PROJ_LIB/proj.db" ]; then
  export PROJ_LIB
fi

printf "Using SUMO_HOME: %s\n" "$SUMO_HOME"
if [ -n "${PROJ_LIB:-}" ]; then
  printf "Using PROJ_LIB: %s\n" "$PROJ_LIB"
fi
printf "Using PREACT_UNITY_CONFIG: %s\n" "$PREACT_UNITY_CONFIG"
printf "Using locale: LANG=%s LC_ALL=%s\n" "$LANG" "$LC_ALL"
printf "Staging macOS native plugins...\n"
"$SCRIPT_DIR/stage_macos_minimal_plugins.sh"

printf "\nLaunching Unity...\n"
"$UNITY_BIN" -projectPath "$UNITY_PROJECT_DIR"
