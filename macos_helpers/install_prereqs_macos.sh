#!/usr/bin/env bash

# Best-effort prerequisite installer for running/building WUInity on macOS.
# This script focuses on toolchain and external dependencies.
# It does NOT fully wire Unity plugin import settings or PREACT native loading.

set -u

UNITY_VERSION="6000.3.10f1"
UNITY_CHANGESET="e35f0c77bd8e"
SUMO_RELEASES_INDEX_URL="https://sumo.dlr.de/releases/"
DEFAULT_SUMO_VERSION="1.26.0"

log() {
  printf "\n==> %s\n" "$1"
}

warn() {
  printf "WARNING: %s\n" "$1" >&2
}

run_or_warn() {
  "$@"
  local exit_code=$?
  if [ $exit_code -ne 0 ]; then
    warn "Command failed ($exit_code): $*"
  fi
  return 0
}

ensure_brew_in_path() {
  if [ -x /opt/homebrew/bin/brew ]; then
    eval "$(/opt/homebrew/bin/brew shellenv)"
  elif [ -x /usr/local/bin/brew ]; then
    eval "$(/usr/local/bin/brew shellenv)"
  fi
}

find_latest_sumo_version() {
  local latest
  latest="$(curl -fsSL "$SUMO_RELEASES_INDEX_URL" 2>/dev/null | tr '"' '\n' | grep -E '^[0-9]+\.[0-9]+\.[0-9]+/$' | sed 's:/$::' | sort -V | tail -n 1)"
  if [ -n "$latest" ]; then
    printf -- "%s" "$latest"
  else
    warn "Could not fetch latest SUMO release index; falling back to ${DEFAULT_SUMO_VERSION}."
    printf -- "%s" "$DEFAULT_SUMO_VERSION"
  fi
}

wait_for_user_confirmation() {
  if [ -t 0 ]; then
    printf -- "\nFinish the SUMO installer, then press Enter to continue... "
    read -r _
  else
    warn "Non-interactive shell detected; cannot wait for installer confirmation."
  fi
}

dotnet8_is_available() {
  # Case 1: active dotnet on PATH has .NET 8 SDK installed.
  if command -v dotnet >/dev/null 2>&1; then
    if dotnet --list-sdks 2>/dev/null | grep -E '^8\.' >/dev/null 2>&1; then
      return 0
    fi
  fi

  # Case 2: Homebrew dotnet@8 is installed but not linked into PATH.
  local dotnet8_prefix
  dotnet8_prefix="$(brew --prefix dotnet@8 2>/dev/null || true)"
  if [ -n "$dotnet8_prefix" ]; then
    if [ -x "$dotnet8_prefix/libexec/dotnet" ]; then
      if "$dotnet8_prefix/libexec/dotnet" --list-sdks 2>/dev/null | grep -E '^8\.' >/dev/null 2>&1; then
        return 0
      fi
    fi
  fi

  return 1
}

unity_editor_is_installed() {
  local editor_path
  editor_path="/Applications/Unity/Hub/Editor/${UNITY_VERSION}"
  [ -d "$editor_path" ]
}

detect_sumo_home() {
  local prefix

  if [ -n "${SUMO_HOME:-}" ] && [ -x "${SUMO_HOME}/bin/sumo" ]; then
    printf -- "%s" "${SUMO_HOME}"
    return 0
  fi

  prefix="$(brew --prefix sumo 2>/dev/null || true)"
  if [ -n "$prefix" ]; then
    if [ -x "$prefix/share/sumo/bin/sumo" ]; then
      printf -- "%s" "$prefix/share/sumo"
      return 0
    fi
    if [ -x "$prefix/bin/sumo" ]; then
      printf -- "%s" "$prefix"
      return 0
    fi
  fi

  if command -v sumo >/dev/null 2>&1; then
    local sumo_bin
    local candidate
    sumo_bin="$(command -v sumo)"
    candidate="$(cd "$(dirname "$sumo_bin")/.." && pwd)"
    if [ -x "$candidate/bin/sumo" ]; then
      printf -- "%s" "$candidate"
      return 0
    fi
    if [ -x "$candidate/share/sumo/bin/sumo" ]; then
      printf -- "%s" "$candidate/share/sumo"
      return 0
    fi
  fi

  # Common locations for package-based installs.
  for prefix in "/Library/Frameworks/EclipseSUMO.framework/Versions/Current/EclipseSUMO/share/sumo" "/opt/sumo/share/sumo" "/usr/local/sumo/share/sumo" "/Applications/Eclipse SUMO.app/Contents/Resources/share/sumo" "/Applications/SUMO.app/Contents/Resources/share/sumo"; do
    if [ -d "$prefix" ]; then
      printf -- "%s" "$prefix"
      return 0
    fi
  done

  # Additional framework-style installs (versioned paths).
  for prefix in /Library/Frameworks/EclipseSUMO.framework/Versions/*/EclipseSUMO/share/sumo; do
    if [ -d "$prefix" ]; then
      printf -- "%s" "$prefix"
      return 0
    fi
  done

  printf -- ""
}

if [ "$(uname -s)" != "Darwin" ]; then
  warn "This script is for macOS only. Aborting."
  exit 1
fi

ARCH="$(uname -m)"
log "Detected macOS architecture: ${ARCH}"

if [ "$ARCH" = "arm64" ]; then
  log "Installing Rosetta 2 (needed by some x86_64 tools/plugins)"
  run_or_warn /usr/sbin/softwareupdate --install-rosetta --agree-to-license
fi

log "Checking Xcode Command Line Tools"
if ! xcode-select -p >/dev/null 2>&1; then
  warn "Xcode Command Line Tools are not installed. Triggering installer UI."
  run_or_warn xcode-select --install
else
  log "Xcode Command Line Tools already installed"
fi

if ! command -v brew >/dev/null 2>&1; then
  log "Installing Homebrew"
  NONINTERACTIVE=1 run_or_warn /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"

  ensure_brew_in_path
fi

ensure_brew_in_path

if ! command -v brew >/dev/null 2>&1; then
  warn "Homebrew is not available after attempted install."
  warn "Install Homebrew manually, then re-run this script."
  exit 1
fi

log "Updating Homebrew"
run_or_warn brew update

FORMULAE=(
  git
  cmake
  swig
  pkg-config
  python@3.12
  dotnet@8
  mono
  gdal
  geos
  proj
)

log "Installing Homebrew formulae"
for formula in "${FORMULAE[@]}"; do
  if brew list --formula "$formula" >/dev/null 2>&1; then
    printf -- "- %s already installed\n" "$formula"
  else
    run_or_warn brew install "$formula"
  fi
done

log "Installing SUMO via official macOS package"
if [ -n "${SUMO_HOME:-}" ] && [ -x "${SUMO_HOME}/bin/sumo" ]; then
  printf -- "- SUMO_HOME already set and valid: %s\n" "$SUMO_HOME"
elif [ -n "${SUMO_HOME:-}" ] && [ ! -x "${SUMO_HOME}/bin/sumo" ]; then
  warn "SUMO_HOME is set but does not contain bin/sumo: ${SUMO_HOME}"
  warn "Will continue with SUMO detection/installation."
  if command -v sumo >/dev/null 2>&1; then
    printf -- "- %s already available in PATH\n" "sumo"
  else
    SUMO_VERSION="$(find_latest_sumo_version)"
    SUMO_PKG_URL="https://sumo.dlr.de/releases/${SUMO_VERSION}/sumo-${SUMO_VERSION}.pkg"
    SUMO_PKG_FILE="${HOME}/Downloads/sumo-${SUMO_VERSION}.pkg"

    printf -- "- Latest SUMO version: %s\n" "$SUMO_VERSION"
    printf -- "- Download URL: %s\n" "$SUMO_PKG_URL"

    run_or_warn curl -fL "$SUMO_PKG_URL" -o "$SUMO_PKG_FILE"
    if [ -f "$SUMO_PKG_FILE" ]; then
      run_or_warn open "$SUMO_PKG_FILE"
      wait_for_user_confirmation
    else
      warn "SUMO package download failed; file not found: $SUMO_PKG_FILE"
    fi

    hash -r

    if ! command -v sumo >/dev/null 2>&1; then
      warn "'sumo' still not found in PATH after package installation."
      warn "You may need a new terminal session or manual PATH/SUMO_HOME setup."
      warn "Attempting Homebrew fallback (dlr-ts/sumo)."
      run_or_warn brew tap dlr-ts/sumo
      run_or_warn brew install sumo
      hash -r
    fi
  fi
elif command -v sumo >/dev/null 2>&1; then
  printf -- "- %s already available in PATH\n" "sumo"
else
  SUMO_VERSION="$(find_latest_sumo_version)"
  SUMO_PKG_URL="https://sumo.dlr.de/releases/${SUMO_VERSION}/sumo-${SUMO_VERSION}.pkg"
  SUMO_PKG_FILE="${HOME}/Downloads/sumo-${SUMO_VERSION}.pkg"

  printf -- "- Latest SUMO version: %s\n" "$SUMO_VERSION"
  printf -- "- Download URL: %s\n" "$SUMO_PKG_URL"

  run_or_warn curl -fL "$SUMO_PKG_URL" -o "$SUMO_PKG_FILE"
  if [ -f "$SUMO_PKG_FILE" ]; then
    run_or_warn open "$SUMO_PKG_FILE"
    wait_for_user_confirmation
  else
    warn "SUMO package download failed; file not found: $SUMO_PKG_FILE"
  fi

  hash -r

  if ! command -v sumo >/dev/null 2>&1; then
    warn "'sumo' still not found in PATH after package installation."
    warn "You may need a new terminal session or manual PATH/SUMO_HOME setup."
    warn "Attempting Homebrew fallback (dlr-ts/sumo)."
    run_or_warn brew tap dlr-ts/sumo
    run_or_warn brew install sumo
    hash -r
  fi
fi

log "Installing optional casks"
OPTIONAL_CASKS=(unity-hub)
for cask in "${OPTIONAL_CASKS[@]}"; do
  if [ "$cask" = "unity-hub" ] && [ -d "/Applications/Unity Hub.app" ]; then
    printf -- "- %s already present at /Applications/Unity Hub.app\n" "$cask"
    continue
  fi
  if brew list --cask "$cask" >/dev/null 2>&1; then
    printf -- "- %s already installed\n" "$cask"
  else
    run_or_warn brew install --cask "$cask"
  fi
done

log "Installing optional SUMO GUI dependency"
if brew list --cask xquartz >/dev/null 2>&1; then
  printf -- "- %s already installed\n" "xquartz"
else
  run_or_warn brew install --cask xquartz
fi

if brew list --cask sumo-gui >/dev/null 2>&1; then
  printf -- "- %s already installed\n" "sumo-gui"
else
  run_or_warn brew install --cask sumo-gui
fi

UNITY_HUB_BIN="/Applications/Unity Hub.app/Contents/MacOS/Unity Hub"
if [ -x "$UNITY_HUB_BIN" ]; then
  if unity_editor_is_installed; then
    printf -- "- Unity Editor %s already installed\n" "$UNITY_VERSION"
  else
    log "Attempting Unity Editor install via Unity Hub CLI (${UNITY_VERSION})"
    run_or_warn "$UNITY_HUB_BIN" -- --headless install --version "$UNITY_VERSION" --changeset "$UNITY_CHANGESET"
  fi
else
  warn "Unity Hub binary not found. Install Unity Hub manually if needed."
fi

log "Environment hints"
SUMO_HOME_DETECTED="$(detect_sumo_home)"
GDAL_PREFIX="$(brew --prefix gdal 2>/dev/null || true)"

if [ -n "$SUMO_HOME_DETECTED" ]; then
  printf -- "- SUMO_HOME candidate: %s\n" "$SUMO_HOME_DETECTED"
  printf -- "  export SUMO_HOME=\"%s\"\n" "$SUMO_HOME_DETECTED"
  printf -- '  export PATH="%s/bin:$PATH"\n' "$SUMO_HOME_DETECTED"

  if [ -x "$SUMO_HOME_DETECTED/bin/sumo" ]; then
    SUMO_EXECUTABLE="$SUMO_HOME_DETECTED/bin/sumo"
  else
    SUMO_EXECUTABLE="sumo"
  fi
else
  warn "SUMO installation was not detected in PATH."
  warn "Try installing official package: https://sumo.dlr.de/releases/${DEFAULT_SUMO_VERSION}/sumo-${DEFAULT_SUMO_VERSION}.pkg"
  SUMO_EXECUTABLE="sumo"
fi

if [ -n "$GDAL_PREFIX" ]; then
  PROJ_PREFIX="$(brew --prefix proj 2>/dev/null || true)"
  printf -- "- GDAL prefix: %s\n" "$GDAL_PREFIX"
  printf -- "  export GDAL_DATA=\"%s/share/gdal\"\n" "$GDAL_PREFIX"
  if [ -n "$PROJ_PREFIX" ]; then
    printf -- "  export PROJ_LIB=\"%s/share/proj\"\n" "$PROJ_PREFIX"
  else
    printf -- "  export PROJ_LIB=\"%s/share/proj\"\n" "$GDAL_PREFIX"
  fi
fi

log "Quick verification"
run_or_warn git --version
run_or_warn dotnet --info
run_or_warn cmake --version
run_or_warn swig -version
run_or_warn gdalinfo --version
run_or_warn "$SUMO_EXECUTABLE" --version

if ! dotnet8_is_available; then
  warn "No .NET 8 SDK detected (required by PREACTexecute)."
  warn "Install with: brew install dotnet@8"
fi

log "Done (best effort)."
printf "Next: wire native libs into PREACT + Unity plugin import settings for macOS.\n"
