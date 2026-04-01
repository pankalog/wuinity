#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
UNITY_PREACT_DIR="$ROOT_DIR/WUInity/Assets/PREACT/Release/netstandard2.1"
SUMO_DST_DIR="$UNITY_PREACT_DIR/ThirdParty/SUMO/linux-x64"
GDAL_DST_DIR="$UNITY_PREACT_DIR/ThirdParty/GDAL/linux-x64"

CUSTOM_LIBSUMOCS="$ROOT_DIR/PREACT/PREACTcore/ThirdParty/Eclipse.Sumo.Libsumo/linux-x64/libsumocs.so"
GDAL_SRC_DIR="$ROOT_DIR/PREACT/PREACTcore/ThirdParty/GDAL/x64"

log() {
  printf "\n==> %s\n" "$1"
}

die() {
  printf "ERROR: %s\n" "$1" >&2
  exit 1
}

resolve_sumo_lib_dir() {
  if [ -n "${SUMO_HOME:-}" ] && [ -d "$SUMO_HOME/lib" ]; then
    (cd "$SUMO_HOME/lib" && pwd)
    return 0
  fi

  if command -v sumo >/dev/null 2>&1; then
    local sumo_bin
    sumo_bin="$(command -v sumo)"
    if [ -d "$(dirname "$sumo_bin")/../lib" ]; then
      (cd "$(dirname "$sumo_bin")/../lib" && pwd)
      return 0
    fi
  fi

  for p in "/usr/lib/x86_64-linux-gnu/sumo" "/usr/lib/sumo"; do
    if [ -d "$p" ]; then
      printf "%s" "$p"
      return 0
    fi
  done

  return 1
}

resolve_libsumocs_source() {
  if [ -f "$CUSTOM_LIBSUMOCS" ]; then
    printf "%s" "$CUSTOM_LIBSUMOCS"
    return 0
  fi

  if [ -n "${SUMO_LIB_SRC:-}" ] && [ -f "$SUMO_LIB_SRC/libsumocs.so" ]; then
    printf "%s" "$SUMO_LIB_SRC/libsumocs.so"
    return 0
  fi

  return 1
}

[ -d "$UNITY_PREACT_DIR" ] || die "Missing Unity PREACT dir: $UNITY_PREACT_DIR"
[ -d "$GDAL_SRC_DIR" ] || die "Missing GDAL source dir: $GDAL_SRC_DIR"

SUMO_LIB_SRC="$(resolve_sumo_lib_dir || true)"
if [ -z "$SUMO_LIB_SRC" ]; then
  SUMO_LIB_SRC="/nonexistent"
fi

LIBSUMOCS_SRC="$(resolve_libsumocs_source || true)"
if [ -z "$LIBSUMOCS_SRC" ]; then
  die "Could not find libsumocs.so. Build and sync first (example: ./build_sumo_official_libsumocs.sh --sync on Linux host)."
fi

log "Using source folders"
printf "- SUMO_HOME lib: %s\n" "$SUMO_LIB_SRC"
printf "- libsumocs:    %s\n" "$LIBSUMOCS_SRC"
printf "- GDAL source:  %s\n" "$GDAL_SRC_DIR"

mkdir -p "$SUMO_DST_DIR"
mkdir -p "$GDAL_DST_DIR"
rm -f "$SUMO_DST_DIR"/*.so
rm -f "$GDAL_DST_DIR"/*.so

log "Copying GDAL Linux wrappers"
for gdal_so in "$GDAL_SRC_DIR"/*.so; do
  [ -f "$gdal_so" ] || continue
  cp -f "$gdal_so" "$GDAL_DST_DIR/"
done

cp -f "$LIBSUMOCS_SRC" "$SUMO_DST_DIR/libsumocs.so"

log "Vendoring transitive dependencies"
python3 - "$SUMO_DST_DIR" "$SUMO_LIB_SRC" <<'PY'
import glob
import os
import re
import subprocess
import sys
from collections import deque

dst = sys.argv[1]
sumo_lib = sys.argv[2]
seed = os.path.join(dst, 'libsumocs.so')

if not os.path.isfile(seed):
    raise SystemExit(f'missing seed library: {seed}')


def lib_prefix(name):
    stem = name[:-3] if name.endswith('.so') else name
    return stem.split('.so')[0].split('.')[0]


def choose_candidate(dep_base, search_dir):
    exact = os.path.join(search_dir, dep_base)
    if os.path.isfile(exact):
        return exact

    prefix = lib_prefix(dep_base)
    candidates = sorted(glob.glob(os.path.join(search_dir, prefix + '*.so*')))
    if not candidates:
        return None

    dep_stem = dep_base
    major_matches = [c for c in candidates if os.path.basename(c).startswith(dep_stem)]
    if major_matches:
        return major_matches[0]

    versioned_request = re.search(r'\.so\.[0-9]+', dep_stem) is not None
    if versioned_request:
        return None

    return candidates[0]


def find_source(dep):
    base = os.path.basename(dep)

    for root in (dst, sumo_lib, '/usr/lib/x86_64-linux-gnu', '/lib/x86_64-linux-gnu'):
        if os.path.isdir(root):
            cand = choose_candidate(base, root)
            if cand:
                return cand

    return None


queue = deque([seed])
visited = set()

while queue:
    lib = queue.popleft()
    if lib in visited or not os.path.isfile(lib):
        continue
    visited.add(lib)

    out = subprocess.check_output(['ldd', lib], text=True)
    for line in out.splitlines():
        line = line.strip()
        if '=>' not in line:
            continue

        left, right = [x.strip() for x in line.split('=>', 1)]

        if right == 'not found':
            src = find_source(left)
            if src:
                dst_path = os.path.join(dst, os.path.basename(src))
                if not os.path.isfile(dst_path):
                    subprocess.check_call(['cp', '-f', src, dst_path])
                queue.append(dst_path)
            else:
                raise SystemExit(f'could not resolve dependency: {left} for {lib}')
            continue

        path = right.split('(')[0].strip()
        if path.startswith('/lib') or path.startswith('/usr/lib'):
            continue

        dst_path = os.path.join(dst, os.path.basename(path))
        if not os.path.isfile(dst_path):
            subprocess.check_call(['cp', '-f', path, dst_path])
        queue.append(dst_path)

print('dependency vendoring complete')
PY

log "Validating"
[ -f "$SUMO_DST_DIR/libsumocs.so" ] || die "Missing libsumocs.so in $SUMO_DST_DIR"

if command -v patchelf >/dev/null 2>&1; then
  log "Setting local rpath on vendored libraries"
  for sofile in "$SUMO_DST_DIR"/*.so* "$GDAL_DST_DIR"/*.so*; do
    [ -f "$sofile" ] || continue
    patchelf --set-rpath '$ORIGIN' "$sofile" || true
  done
fi

log "Done"
printf "Staged Linux x64 natives under: %s\n" "$SUMO_DST_DIR"
printf "Staged Linux x64 natives under: %s\n" "$GDAL_DST_DIR"
