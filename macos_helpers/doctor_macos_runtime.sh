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
UNITY_EDITOR_DEFAULT="/Applications/Unity/Hub/Editor/6000.3.10f1/Unity.app/Contents/MacOS/Unity"

PREACT_RELEASE_DIR="$UNITY_PROJECT_DIR/Assets/PREACT/Release/netstandard2.1"
GDAL_DIR="$PREACT_RELEASE_DIR/ThirdParty/GDAL/osx-arm64"
SUMO_DIR="$PREACT_RELEASE_DIR/ThirdParty/SUMO/osx-arm64"

DEFAULT_SUMO_HOME="/Library/Frameworks/EclipseSUMO.framework/Versions/Current/EclipseSUMO/share/sumo"
DEFAULT_PROJ_LIB="/opt/homebrew/opt/proj/share/proj"

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

check_exe() {
  local path="$1"
  local label="$2"
  if [ -x "$path" ]; then
    ok "$label ($path)"
  else
    fail "$label not executable ($path)"
  fi
}

headline "Platform"
if [ "$(uname -s)" = "Darwin" ]; then
  ok "Running on macOS"
else
  fail "This doctor targets macOS only"
fi

if [ "$(uname -m)" = "arm64" ]; then
  ok "Architecture is arm64"
else
  warn "Architecture is $(uname -m), expected arm64 for this setup"
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
    ok ".NET 8 SDK available in active dotnet"
  elif [ -x "/opt/homebrew/opt/dotnet@8/libexec/dotnet" ] && /opt/homebrew/opt/dotnet@8/libexec/dotnet --list-sdks 2>/dev/null | grep -E '^8\.' >/dev/null 2>&1; then
    ok ".NET 8 SDK available via /opt/homebrew/opt/dotnet@8/libexec/dotnet"
  else
    warn ".NET 8 SDK not detected"
  fi
else
  warn "dotnet not found in PATH"
fi

headline "Unity Editor"
check_exe "$UNITY_EDITOR_DEFAULT" "Unity editor binary"

headline "Required Native Files"
check_file "$GDAL_DIR/libgdal_wrap.dylib" "GDAL SWIG wrapper"
check_file "$GDAL_DIR/libogr_wrap.dylib" "OGR SWIG wrapper"
check_file "$GDAL_DIR/libosr_wrap.dylib" "OSR SWIG wrapper"
check_file "$GDAL_DIR/libgdalconst_wrap.dylib" "GDALConst SWIG wrapper"
check_file "$SUMO_DIR/libsumocs.dylib" "SUMO C# bridge"
if [ -f "$SUMO_DIR/libsumocpp.dylib" ] || [ -f "$GDAL_DIR/libsumocpp.dylib" ]; then
  ok "SUMO core dependency present"
else
  ok "SUMO core dependency not present as standalone plugin (not required for current libsumocs build)"
fi
if [ -f "$SUMO_DIR/libjupedsim.dylib" ] || [ -f "$GDAL_DIR/libjupedsim.dylib" ]; then
  ok "JuPedSim dependency present"
else
  ok "JuPedSim dependency not present as standalone plugin (not required for current libsumocs build)"
fi
if [ -f "$SUMO_DIR/libtracics.dylib" ] || [ -f "$GDAL_DIR/libtracics.dylib" ]; then
  ok "TraCI C bridge dependency present"
else
  ok "TraCI C bridge dependency not present as standalone plugin (not required for current libsumocs build)"
fi
if [ -f "$SUMO_DIR/libtracicpp.dylib" ] || [ -f "$GDAL_DIR/libtracicpp.dylib" ]; then
  ok "TraCI C++ bridge dependency present"
else
  ok "TraCI C++ bridge dependency not present as standalone plugin (not required for current libsumocs build)"
fi

headline "Dylib Linkage"
while IFS= read -r line; do
  case "$line" in
    FAIL::*) fail "${line#FAIL::}" ;;
    WARN::*) warn "${line#WARN::}" ;;
  esac
done < <(python3 - "$SUMO_DIR" <<'PY'
import os
import subprocess
import sys

sumo_dir = sys.argv[1]
targets = [
    os.path.join(sumo_dir, "libsumocs.dylib"),
]

for target in targets:
    if not os.path.isfile(target):
        print(f"FAIL::missing target: {target}")
        continue

    out = subprocess.check_output(["otool", "-L", target], text=True)
    deps = [ln.strip().split(" (")[0] for ln in out.splitlines()[1:]]

    if "@rpath/libjupedsim.dylib" in deps:
        print(f"FAIL::{target}: unresolved @rpath/libjupedsim.dylib")

    for dep in deps:
        if dep.startswith("@loader_path/"):
            rel = dep.replace("@loader_path/", "", 1)
            abs_path = os.path.normpath(os.path.join(os.path.dirname(target), rel))
            if not os.path.exists(abs_path):
                print(f"FAIL::{target}: missing loader_path dependency {dep} -> {abs_path}")
            continue

        if dep.startswith("/"):
            if dep.startswith("/usr/lib/") or dep.startswith("/System/"):
                continue
            # Expected local environment references that should have been rewritten.
            if dep.startswith("/opt/homebrew/opt/"):
                print(f"FAIL::{target}: unresolved host Homebrew dependency {dep}")
                continue
            if not os.path.exists(dep):
                print(f"FAIL::{target}: missing absolute dependency {dep}")
            else:
                print(f"WARN::{target}: depends on absolute path {dep}")
PY
)

headline "Duplicate Basenames"
while IFS= read -r lib; do
  [ -z "$lib" ] && continue
  warn "Duplicate dylib basename across GDAL and SUMO: $lib"
done < <(python3 - "$GDAL_DIR" "$SUMO_DIR" <<'PY'
import os
import sys

gdal = sys.argv[1]
sumo = sys.argv[2]

def dylibs(path):
    if not os.path.isdir(path):
        return set()
    return {f for f in os.listdir(path) if f.endswith('.dylib')}

dupes = sorted(dylibs(gdal).intersection(dylibs(sumo)))
for d in dupes:
    print(d)
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
