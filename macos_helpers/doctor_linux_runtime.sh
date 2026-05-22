#!/usr/bin/env bash

set -euo pipefail

STRICT=false
if [ "${1:-}" = "--strict" ]; then
  STRICT=true
elif [ "${1:-}" = "-h" ] || [ "${1:-}" = "--help" ]; then
  printf "Usage: %s [--strict]\n" "$(basename "$0")"
  printf "  --strict  Treat warnings as failures\n"
  exit 0
fi

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
UNITY_PROJECT_DIR="$ROOT_DIR/WUInity"
PREACT_RELEASE_DIR="$UNITY_PROJECT_DIR/Assets/PREACT/Release/netstandard2.1"
GDAL_DIR="$PREACT_RELEASE_DIR/ThirdParty/GDAL/linux-x64"
SUMO_DIR="$PREACT_RELEASE_DIR/ThirdParty/SUMO/linux-x64"

DEFAULT_SUMO_HOME="/usr/share/sumo"
DEFAULT_PROJ_LIB="/usr/share/proj"

fail_count=0
warn_count=0

headline() {
  printf "\n==> %s\n" "$1"
}

ok() {
  printf "[OK] %s\n" "$1"
}

warn() {
  warn_count=$((warn_count + 1))
  printf "[WARN] %s\n" "$1"
}

fail() {
  fail_count=$((fail_count + 1))
  printf "[FAIL] %s\n" "$1"
}

check_file() {
  local path="$1"
  local label="$2"
  if [ -f "$path" ]; then
    ok "$label ($path)"
  else
    fail "$label missing ($path)"
  fi
}

check_dir() {
  local path="$1"
  local label="$2"
  if [ -d "$path" ]; then
    ok "$label ($path)"
  else
    fail "$label missing ($path)"
  fi
}

headline "Platform"
if [ "$(uname -s)" = "Linux" ]; then
  ok "Running on Linux"
else
  fail "This doctor targets Linux only"
fi

if [ "$(uname -m)" = "x86_64" ]; then
  ok "Architecture is x86_64"
else
  warn "Architecture is $(uname -m), expected x86_64 for this setup"
fi

headline "Project"
check_dir "$UNITY_PROJECT_DIR" "Unity project root"
check_dir "$PREACT_RELEASE_DIR" "PREACT release directory"
check_dir "$GDAL_DIR" "GDAL native directory"
check_dir "$SUMO_DIR" "SUMO native directory"

headline "Environment"
if [ -n "${SUMO_HOME:-}" ]; then
  if [ -x "$SUMO_HOME/bin/sumo" ]; then
    ok "SUMO_HOME is valid ($SUMO_HOME)"
  else
    fail "SUMO_HOME set but bin/sumo missing ($SUMO_HOME/bin/sumo)"
  fi
else
  warn "SUMO_HOME not set (recommended: $DEFAULT_SUMO_HOME)"
fi

if [ -n "${PROJ_LIB:-}" ]; then
  if [ -f "$PROJ_LIB/proj.db" ]; then
    ok "PROJ_LIB contains proj.db ($PROJ_LIB/proj.db)"
  else
    fail "PROJ_LIB set but proj.db missing ($PROJ_LIB/proj.db)"
  fi
else
  warn "PROJ_LIB not set (recommended: $DEFAULT_PROJ_LIB)"
fi

if command -v sumo >/dev/null 2>&1; then
  ok "sumo found in PATH ($(command -v sumo))"
else
  warn "sumo not found in PATH"
fi

if command -v dotnet >/dev/null 2>&1; then
  if dotnet --list-sdks 2>/dev/null | grep -E '^8\.' >/dev/null 2>&1; then
    ok ".NET 8 SDK available"
  else
    warn ".NET 8 SDK not detected"
  fi
else
  warn "dotnet not found in PATH"
fi

headline "Required Native Files"
check_file "$GDAL_DIR/libgdal_wrap.so" "GDAL SWIG wrapper"
check_file "$GDAL_DIR/libogr_wrap.so" "OGR SWIG wrapper"
check_file "$GDAL_DIR/libosr_wrap.so" "OSR SWIG wrapper"
check_file "$GDAL_DIR/libgdalconst_wrap.so" "GDALConst SWIG wrapper"
check_file "$SUMO_DIR/libsumocs.so" "SUMO C# bridge"

headline "Shared Object Linkage"
while IFS= read -r line; do
  case "$line" in
    FAIL::*) fail "${line#FAIL::}" ;;
    WARN::*) warn "${line#WARN::}" ;;
  esac
done < <(python3 - "$SUMO_DIR" "$GDAL_DIR" <<'PY'
import os
import subprocess
import sys

sumo_dir = sys.argv[1]
gdal_dir = sys.argv[2]
targets = [
    os.path.join(sumo_dir, "libsumocs.so"),
    os.path.join(gdal_dir, "libgdal_wrap.so"),
]

for target in targets:
    if not os.path.isfile(target):
        print(f"FAIL::missing target: {target}")
        continue

    out = subprocess.check_output(["ldd", target], text=True)
    for line in out.splitlines():
        line = line.strip()
        if "=> not found" in line:
            print(f"FAIL::{target}: unresolved dependency: {line}")
        elif "=>" in line:
            dep = line.split("=>", 1)[0].strip()
            rhs = line.split("=>", 1)[1].strip().split("(", 1)[0].strip()
            if rhs.startswith("/") and rhs.startswith("/usr/local/"):
                print(f"WARN::{target}: depends on /usr/local library {dep} => {rhs}")
PY
)

headline "Summary"
printf "Failures: %d\n" "$fail_count"
printf "Warnings: %d\n" "$warn_count"

if [ "$STRICT" = true ] && [ "$warn_count" -gt 0 ]; then
  printf "\nStrict mode enabled: warnings treated as failures.\n" >&2
  fail_count=$((fail_count + warn_count))
fi

if [ "$fail_count" -gt 0 ]; then
  printf "\nDoctor check FAILED.\n" >&2
  exit 1
fi

printf "\nDoctor check passed.\n"
