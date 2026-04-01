#!/usr/bin/env bash

# Reproducible macOS ARM64 native staging for WUInity (minimal path):
# - SUMO core natives (libsumocs + traci/jupedsim)
# - GDAL wrappers + bundled geospatial dependencies
#
# Strategy:
# - Keep SUMO-specific libs in SUMO folder
# - Reuse GDAL folder for shared libs to avoid duplicate plugin names in Unity
# - Rewrite SUMO dylib dependencies to @loader_path paths for deterministic loading

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TARGET_CONFIG="${PREACT_UNITY_CONFIG:-Debug}"
GDAL_SRC_DIR="$REPO_ROOT/PREACT/PREACTcore/ThirdParty/GDAL/osx-arm64"

log() {
  printf "\n==> %s\n" "$1"
}

die() {
  printf "ERROR: %s\n" "$1" >&2
  exit 1
}

if [ "$TARGET_CONFIG" != "Debug" ] && [ "$TARGET_CONFIG" != "Release" ]; then
  die "Invalid PREACT_UNITY_CONFIG='$TARGET_CONFIG' (expected Debug or Release)."
fi

UNITY_PREACT_DIR="$REPO_ROOT/WUInity/Assets/PREACT/$TARGET_CONFIG/netstandard2.1"
if [ ! -d "$UNITY_PREACT_DIR" ]; then
  ALT_CONFIG="Release"
  if [ "$TARGET_CONFIG" = "Release" ]; then
    ALT_CONFIG="Debug"
  fi
  ALT_DIR="$REPO_ROOT/WUInity/Assets/PREACT/$ALT_CONFIG/netstandard2.1"
  if [ -d "$ALT_DIR" ]; then
    UNITY_PREACT_DIR="$ALT_DIR"
    log "Requested $TARGET_CONFIG not found; falling back to $ALT_CONFIG"
  fi
fi

GDAL_DST_DIR="$UNITY_PREACT_DIR/ThirdParty/GDAL/osx-arm64"
SUMO_DST_DIR="$UNITY_PREACT_DIR/ThirdParty/SUMO/osx-arm64"

if [ "$(uname -s)" != "Darwin" ]; then
  die "This script is intended for macOS only."
fi

if [ ! -d "$UNITY_PREACT_DIR" ]; then
  die "Unity PREACT output folder not found (checked Release and Debug): $REPO_ROOT/WUInity/Assets/PREACT"
fi

resolve_sumo_lib_dir() {
  if [ -n "${SUMO_HOME:-}" ] && [ -d "$SUMO_HOME/../../lib" ]; then
    (cd "$SUMO_HOME/../../lib" && pwd)
    return 0
  fi

  if [ -d "/Library/Frameworks/EclipseSUMO.framework/Versions/Current/EclipseSUMO/lib" ]; then
    printf "/Library/Frameworks/EclipseSUMO.framework/Versions/Current/EclipseSUMO/lib"
    return 0
  fi

  return 1
}

SUMO_LIB_SRC="$(resolve_sumo_lib_dir || true)"
CUSTOM_LIBSUMOCS="$REPO_ROOT/PREACT/PREACTcore/ThirdParty/Eclipse.Sumo.Libsumo/osx-arm64/libsumocs.dylib"

[ -d "$GDAL_SRC_DIR" ] || die "GDAL macOS source folder not found: $GDAL_SRC_DIR"
[ -n "$SUMO_LIB_SRC" ] || die "Could not locate SUMO lib directory. Set SUMO_HOME first."
[ -d "$SUMO_LIB_SRC" ] || die "Resolved SUMO lib directory does not exist: $SUMO_LIB_SRC"

log "Using source folders"
printf -- "- GDAL: %s\n" "$GDAL_SRC_DIR"
printf -- "- SUMO: %s\n" "$SUMO_LIB_SRC"
printf -- "- Unity PREACT target: %s\n" "$UNITY_PREACT_DIR"

log "Preparing destination folders"
mkdir -p "$GDAL_DST_DIR" "$SUMO_DST_DIR"

log "Copying GDAL macOS dylibs"
cp -f "$GDAL_SRC_DIR"/*.dylib "$GDAL_DST_DIR/"

log "Refreshing SUMO core dylibs"
rm -f "$SUMO_DST_DIR"/*.dylib
SUMO_CORE_LIBS=(
  libsumocs.dylib
)

for lib in "${SUMO_CORE_LIBS[@]}"; do
  if [ "$lib" = "libsumocs.dylib" ] && [ -f "$CUSTOM_LIBSUMOCS" ]; then
    cp -f "$CUSTOM_LIBSUMOCS" "$SUMO_DST_DIR/$lib"
    continue
  fi

  [ -f "$SUMO_LIB_SRC/$lib" ] || die "Missing required SUMO library: $SUMO_LIB_SRC/$lib"
  cp -f "$SUMO_LIB_SRC/$lib" "$SUMO_DST_DIR/$lib"
done

log "Rewriting SUMO dependency install names"
python3 - "$SUMO_LIB_SRC" "$SUMO_DST_DIR" "$GDAL_SRC_DIR" "$GDAL_DST_DIR" <<'PY'
import glob
import os
import re
import subprocess
import sys
from collections import deque

sumo_src = sys.argv[1]
sumo_dst = sys.argv[2]
gdal_src = sys.argv[3]
gdal_dst = sys.argv[4]

sumo_core_names = {
    "libsumocs",
}

seed_targets = [
    os.path.join(sumo_dst, "libsumocs.dylib"),
]

verify_targets = [
    os.path.join(sumo_dst, "libsumocs.dylib"),
]


def lib_prefix(name):
    stem = name[:-6] if name.endswith(".dylib") else name
    return stem.split(".")[0]


def choose_candidate(dep_base, search_dir):
    if not os.path.isdir(search_dir):
        return None

    exact = os.path.join(search_dir, dep_base)
    if os.path.isfile(exact):
        return exact

    dep_stem = dep_base[:-6] if dep_base.endswith(".dylib") else dep_base
    prefix = dep_stem.split(".")[0]
    candidates = sorted(glob.glob(os.path.join(search_dir, prefix + "*.dylib")))
    if not candidates:
        return None

    major_matches = [c for c in candidates if os.path.basename(c).startswith(dep_stem + ".")]
    if major_matches:
        return major_matches[0]

    # If the requested dependency contains version information, do not silently
    # downgrade to a different major/minor variant from this folder.
    versioned_request = re.search(r'(?:\.|-)[0-9]+(?:\.[0-9]+)*$', dep_stem) is not None
    if versioned_request:
        return None

    return candidates[0]


def locate_source(dep_base):
    prefix = lib_prefix(dep_base)

    if prefix in sumo_core_names:
        cand = choose_candidate(dep_base, sumo_src)
        if cand:
            return cand, sumo_dst

    for src in (gdal_dst, gdal_src, sumo_src):
        cand = choose_candidate(dep_base, src)
        if cand:
            return cand, gdal_dst

    # Final fallback: host Homebrew libraries (to be copied into project bundle).
    brew_exact = sorted(glob.glob(os.path.join('/opt/homebrew/opt', '*', 'lib', dep_base)))
    if brew_exact:
        return brew_exact[0], gdal_dst

    dep_stem = dep_base[:-6] if dep_base.endswith('.dylib') else dep_base
    dep_prefix = dep_stem.split('.')[0]
    brew_candidates = sorted(glob.glob(os.path.join('/opt/homebrew/opt', '*', 'lib', dep_prefix + '*.dylib')))
    if brew_candidates:
        major = [c for c in brew_candidates if os.path.basename(c).startswith(dep_stem + '.')]
        return (major[0] if major else brew_candidates[0]), gdal_dst

    return None, None


def loader_ref(from_path, to_path):
    from_dir = os.path.dirname(from_path)
    rel = os.path.relpath(to_path, from_dir)
    rel = rel.replace("\\", "/")
    return "@loader_path/" + rel


def rewrite_or_copy_dependency(current_lib, dep):
    dep_base = os.path.basename(dep)
    source_path, destination_dir = locate_source(dep_base)
    if not source_path:
        return None

    dst_name = os.path.basename(source_path)
    dst_path = os.path.join(destination_dir, dst_name)
    if not os.path.isfile(dst_path):
        subprocess.check_call(["cp", "-f", source_path, dst_path])

    new_dep = loader_ref(current_lib, dst_path)
    subprocess.check_call(["install_name_tool", "-change", dep, new_dep, current_lib])
    return dst_path


def ensure_loader_dependency(current_lib, dep):
    rel = dep.replace("@loader_path/", "", 1)
    expected_path = os.path.normpath(os.path.join(os.path.dirname(current_lib), rel))
    if os.path.isfile(expected_path):
        return expected_path

    dep_base = os.path.basename(expected_path)
    source_path, _ = locate_source(dep_base)
    if not source_path:
        return None

    os.makedirs(os.path.dirname(expected_path), exist_ok=True)
    subprocess.check_call(["cp", "-f", source_path, expected_path])
    subprocess.check_call(["codesign", "--force", "--sign", "-", expected_path])
    return expected_path


queue = deque(seed_targets)
visited = set()

while queue:
    path = queue.popleft()
    if path in visited or not os.path.isfile(path):
        continue
    visited.add(path)

    out = subprocess.check_output(["otool", "-L", path], text=True)
    deps = [ln.strip().split(" (")[0] for ln in out.splitlines()[1:]]

    for dep in deps:
        if dep.startswith("/usr/lib/") or dep.startswith("/System/"):
            continue

        if dep == "@rpath/libjupedsim.dylib":
            new_dep = loader_ref(path, os.path.join(sumo_dst, "libjupedsim.dylib"))
            subprocess.check_call(["install_name_tool", "-change", dep, new_dep, path])
            queue.append(os.path.join(sumo_dst, "libjupedsim.dylib"))
            continue

        if dep.startswith("@loader_path/"):
            abs_dep = ensure_loader_dependency(path, dep)
            if abs_dep and os.path.isfile(abs_dep):
                queue.append(abs_dep)
            continue

        if dep.startswith("/"):
            copied = rewrite_or_copy_dependency(path, dep)
            if copied:
                queue.append(copied)

    subprocess.check_call(["codesign", "--force", "--sign", "-", path])


for path in verify_targets:
    if not os.path.isfile(path):
        continue
    out = subprocess.check_output(["otool", "-L", path], text=True)
    if "@rpath/libjupedsim.dylib" in out:
        raise SystemExit(f"Unpatched jupedsim rpath remains in {path}")


for path in verify_targets:
    if not os.path.isfile(path):
        continue
    out = subprocess.check_output(["otool", "-L", path], text=True)
    for dep in [ln.strip().split(" (")[0] for ln in out.splitlines()[1:]]:
        if dep.startswith("/opt/homebrew/opt/"):
            raise SystemExit(f"Unresolved host dependency remains in {path}: {dep}")
PY

log "Validating critical files"
[ -f "$GDAL_DST_DIR/libgdal_wrap.dylib" ] || die "Missing libgdal_wrap.dylib in $GDAL_DST_DIR"
[ -f "$GDAL_DST_DIR/libogr_wrap.dylib" ] || die "Missing libogr_wrap.dylib in $GDAL_DST_DIR"
[ -f "$GDAL_DST_DIR/libosr_wrap.dylib" ] || die "Missing libosr_wrap.dylib in $GDAL_DST_DIR"
[ -f "$GDAL_DST_DIR/libgdalconst_wrap.dylib" ] || die "Missing libgdalconst_wrap.dylib in $GDAL_DST_DIR"
[ -f "$SUMO_DST_DIR/libsumocs.dylib" ] || die "Missing libsumocs.dylib in $SUMO_DST_DIR"

log "Done"
printf "Staged macOS ARM64 natives under:\n"
printf -- "- %s\n" "$GDAL_DST_DIR"
printf -- "- %s\n" "$SUMO_DST_DIR"

printf "\nNext in Unity (required):\n"
printf "1) For *.dylib in osx-arm64 folders: enable Editor + Standalone OSX, CPU ARM64.\n"
printf "2) For Windows *.dll plugins: disable OSX to avoid load conflicts.\n"
printf "3) Reimport assets and run a SUMO+GDAL scenario first.\n"
