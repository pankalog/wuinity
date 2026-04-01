#!/usr/bin/env bash

# Best-effort prerequisite installer for running/building WUInity on Linux.
# Targets Debian/Ubuntu-like systems first.

set -u

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

is_debian_like() {
  [ -f /etc/debian_version ]
}

if [ "$(uname -s)" != "Linux" ]; then
  warn "This script is for Linux only."
  exit 1
fi

if ! is_debian_like; then
  warn "Non Debian/Ubuntu distro detected."
  warn "Install equivalent packages manually, then run runtime scripts."
  exit 1
fi

log "Refreshing apt indexes"
run_or_warn sudo apt-get update

APT_PACKAGES=(
  build-essential
  ca-certificates
  curl
  git
  cmake
  swig
  pkg-config
  python3
  python3-pip
  mono-devel
  gdal-bin
  libgdal-dev
  proj-bin
  proj-data
  patchelf
  sumo
  sumo-tools
)

log "Installing core apt packages"
run_or_warn sudo apt-get install -y "${APT_PACKAGES[@]}"

log "Installing .NET 8 SDK"
if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -E '^8\.' >/dev/null 2>&1; then
  printf "- dotnet SDK 8 already available\n"
else
  run_or_warn sudo apt-get install -y dotnet-sdk-8.0
fi

log "Environment hints"
if [ -d "/usr/share/sumo" ]; then
  printf 'export SUMO_HOME="/usr/share/sumo"\n'
  printf 'export PATH="$SUMO_HOME/bin:$PATH"\n'
fi

if [ -f "/usr/share/proj/proj.db" ]; then
  printf 'export PROJ_LIB="/usr/share/proj"\n'
fi

printf 'export LANG="C.UTF-8"\n'
printf 'export LC_ALL="C.UTF-8"\n'

log "Quick verification"
run_or_warn git --version
run_or_warn dotnet --info
run_or_warn cmake --version
run_or_warn swig -version
run_or_warn gdalinfo --version
run_or_warn sumo --version

log "Done (best effort)."
printf "Next: build/sync Linux libsumocs, then stage Linux native plugins.\n"
