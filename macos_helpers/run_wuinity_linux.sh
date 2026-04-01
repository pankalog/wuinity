#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
UNITY_PROJECT_DIR="$ROOT_DIR/WUInity"

DEFAULT_SUMO_HOME="/usr/share/sumo"
DEFAULT_PROJ_LIB="/usr/share/proj"

if [ "$(uname -s)" != "Linux" ]; then
  printf "This helper is for Linux only.\n" >&2
  exit 1
fi

if [ ! -d "$UNITY_PROJECT_DIR" ]; then
  printf "Unity project not found: %s\n" "$UNITY_PROJECT_DIR" >&2
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

export LANG="${LANG:-C.UTF-8}"
export LC_ALL="${LC_ALL:-C.UTF-8}"

if [ -z "${PROJ_LIB:-}" ] && [ -f "$DEFAULT_PROJ_LIB/proj.db" ]; then
  export PROJ_LIB="$DEFAULT_PROJ_LIB"
fi

if [ -z "${PROJ_LIB:-}" ] || [ ! -f "$PROJ_LIB/proj.db" ]; then
  printf "PROJ_LIB is invalid or proj.db missing.\n" >&2
  printf "Set PROJ_LIB to a folder containing proj.db, e.g. %s\n" "$DEFAULT_PROJ_LIB" >&2
  exit 1
fi

printf "Using SUMO_HOME: %s\n" "$SUMO_HOME"
printf "Using PROJ_LIB: %s\n" "$PROJ_LIB"
printf "Using locale: LANG=%s LC_ALL=%s\n" "$LANG" "$LC_ALL"

printf "Staging Linux native plugins...\n"
"$ROOT_DIR/stage_linux_minimal_plugins.sh"

printf "\nUnity launch is not automated on Linux in this helper.\n"
printf "Open Unity Hub manually and load project: %s\n" "$UNITY_PROJECT_DIR"
