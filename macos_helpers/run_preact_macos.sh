#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
PREACT_EXEC_DIR="$ROOT_DIR/PREACT/PREACTexecute"
DEFAULT_WUI="$ROOT_DIR/Examples/NFDRS4_Behave/Roxborough/Roxborough_global_smoke.wui"
WUI_FILE="${1:-$DEFAULT_WUI}"

# Resolve to absolute path if necessary
if [[ ! "$WUI_FILE" = /* ]]; then
    WUI_FILE="$(pwd)/$WUI_FILE"
fi

if [ ! -f "$WUI_FILE" ]; then
    echo "Error: WUI file not found: $WUI_FILE"
    exit 1
fi

echo "Building PREACTexecute..."
cd "$PREACT_EXEC_DIR"
dotnet build -q

OUT_DIR="$PREACT_EXEC_DIR/bin/Debug/net8.0"
mkdir -p "$OUT_DIR"

echo "Staging macOS native libraries..."
cp -a "$ROOT_DIR/PREACT/PREACTcore/ThirdParty/GDAL/osx-arm64/"*.dylib "$OUT_DIR/"
cp "$ROOT_DIR/PREACT/PREACTcore/ThirdParty/Eclipse.Sumo.Libsumo/osx-arm64/libsumocs.dylib" "$OUT_DIR/"

export DYLD_LIBRARY_PATH="$OUT_DIR"
export PROJ_LIB="${PROJ_LIB:-/opt/homebrew/opt/proj/share/proj}"
export SUMO_HOME="${SUMO_HOME:-/Library/Frameworks/EclipseSUMO.framework/Versions/Current/EclipseSUMO/share/sumo}"

echo "Environment:"
echo "  DYLD_LIBRARY_PATH=$DYLD_LIBRARY_PATH"
echo "  PROJ_LIB=$PROJ_LIB"
echo "  SUMO_HOME=$SUMO_HOME"
echo ""

echo "Running simulation with $(basename "$WUI_FILE")..."
dotnet run -- "$WUI_FILE"
