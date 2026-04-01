#!/usr/bin/env bash

set -euo pipefail

SUMO_REPO_URL="https://github.com/eclipse-sumo/sumo.git"
SUMO_TAG="v1_26_0"
PATCH_URL="https://github.com/bran-jnw/sumo/commit/b72b275591e8ff58cf9ae9052c2f49cfbf568d49.patch"
WORKDIR="/tmp/sumo-official-1.26.0"
BUILD_DIR_NAME="build-libsumocs-min"
SYNC=false

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"

usage() {
  cat <<'EOF'
Usage: ./build_sumo_official_libsumocs.sh [--sync] [--workdir PATH]

Builds official SUMO v1.26.0 libsumocs with minimal dependencies.

Options:
  --sync            Copy generated C# wrappers and libsumocs into this repo
  --workdir PATH    Clone/build location (default: /tmp/sumo-official-1.26.0)
  -h, --help        Show this help
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --sync)
      SYNC=true
      shift
      ;;
    --workdir)
      WORKDIR="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

BUILD_DIR="$WORKDIR/$BUILD_DIR_NAME"

log() {
  printf "\n==> %s\n" "$1"
}

if [ ! -d "$WORKDIR/.git" ]; then
  log "Cloning SUMO official repository"
  git clone "$SUMO_REPO_URL" "$WORKDIR"
fi

log "Checking out $SUMO_TAG"
git -C "$WORKDIR" fetch --tags
git -C "$WORKDIR" checkout "$SUMO_TAG"

log "Ensuring C# namespace patch is present"
if grep -q 'set(CMAKE_SWIG_FLAGS -namespace ${CS_NAMESPACE})' "$WORKDIR/src/libsumo/CMakeLists.txt"; then
  printf "Patch content already present in %s\n" "$SUMO_TAG"
else
  PATCH_FILE="$WORKDIR/b72b275.patch"
  curl -fL "$PATCH_URL" -o "$PATCH_FILE"
  git -C "$WORKDIR" apply "$PATCH_FILE"
  rm -f "$PATCH_FILE"
fi

log "Configuring SUMO (minimal dependency libsumocs build)"
cmake -S "$WORKDIR" -B "$BUILD_DIR" \
  -DCMAKE_BUILD_TYPE=Release \
  -DENABLE_CS_BINDINGS=ON \
  -DENABLE_JAVA_BINDINGS=OFF \
  -DENABLE_PYTHON_BINDINGS=OFF \
  -DNETEDIT=OFF \
  -DFMI=OFF \
  -DPARQUET=OFF \
  -DCHECK_OPTIONAL_LIBS=OFF

log "Building libsumocs"
cmake --build "$BUILD_DIR" --target libsumocs -j2

LIBSUMOCS_BASENAME="libsumocs.so"
if [ "$(uname -s)" = "Darwin" ]; then
  LIBSUMOCS_BASENAME="libsumocs.dylib"
fi

LIBSUMOCS_PATH="$WORKDIR/bin/$LIBSUMOCS_BASENAME"
WRAPPER_DIR="$BUILD_DIR/src/libsumo/Eclipse.Sumo.Libsumo"

if [ ! -f "$LIBSUMOCS_PATH" ]; then
  echo "Build succeeded but $LIBSUMOCS_PATH not found" >&2
  exit 1
fi

if [ ! -d "$WRAPPER_DIR" ]; then
  echo "Wrapper directory not found: $WRAPPER_DIR" >&2
  exit 1
fi

log "Build outputs"
printf "libsumocs: %s\n" "$LIBSUMOCS_PATH"
printf "wrappers:  %s\n" "$WRAPPER_DIR"

if [ "$SYNC" = true ]; then
  log "Syncing outputs into WUInity repo"

  TARGET_RUNTIME="linux-x64"
  if [ "$(uname -s)" = "Darwin" ] && [ "$(uname -m)" = "arm64" ]; then
    TARGET_RUNTIME="osx-arm64"
  fi

  TARGET_LIB_DIR="$ROOT_DIR/PREACT/PREACTcore/ThirdParty/Eclipse.Sumo.Libsumo/$TARGET_RUNTIME"
  mkdir -p "$TARGET_LIB_DIR"
  cp -f "$LIBSUMOCS_PATH" "$TARGET_LIB_DIR/$LIBSUMOCS_BASENAME"
  cp -f "$WRAPPER_DIR"/*.cs "$ROOT_DIR/PREACT/PREACTcore/ThirdParty/Eclipse.Sumo.Libsumo/"

  printf "Synced native lib to: %s/%s\n" "$TARGET_LIB_DIR" "$LIBSUMOCS_BASENAME"
  printf "Synced wrappers to:   %s\n" "$ROOT_DIR/PREACT/PREACTcore/ThirdParty/Eclipse.Sumo.Libsumo"
fi
