# WUInity macOS Setup (Reproducible Path)

This is the supported setup for running WUInity on macOS Apple Silicon with the minimal module set (SUMO + GDAL).

## 1) Install prerequisites

From repo root:

```bash
./install_prereqs_macos.sh
```

## 2) Set shell environment

Add to `~/.zshrc`:

```bash
export SUMO_HOME="/Library/Frameworks/EclipseSUMO.framework/Versions/Current/EclipseSUMO/share/sumo"
export PROJ_LIB="/opt/homebrew/opt/proj/share/proj"
export PATH="$SUMO_HOME/bin:$PATH"
export LANG="en_US.UTF-8"
export LC_ALL="en_US.UTF-8"
```

Reload shell:

```bash
source ~/.zshrc
```

## 3) Stage native plugins

```bash
./stage_macos_minimal_plugins.sh
```

This script:

- Copies macOS ARM64 GDAL and SUMO libraries into Unity plugin folders
- Rewrites SUMO library dependency paths to local bundled libraries
- Re-signs patched dylibs for macOS loading

## 4) Launch Unity through helper

```bash
./run_wuinity_macos.sh
```

## Optional: run runtime doctor

```bash
./doctor_macos_runtime.sh
```

Strict mode (fails on warnings too):

```bash
./doctor_macos_runtime.sh --strict
```

Note: strict mode expects `PROJ_LIB` to be set in your current shell.

## 5) Unity plugin importer settings

In Unity, for all `.dylib` under:

- `WUInity/Assets/PREACT/Release/netstandard2.1/ThirdParty/GDAL/osx-arm64`
- `WUInity/Assets/PREACT/Release/netstandard2.1/ThirdParty/SUMO/osx-arm64`

Set plugin compatibility:

- Enable: `Editor`, `Standalone OSX`
- CPU: `ARM64`
- Disable Windows/Linux

For Windows `.dll` plugins in `x64` folders, disable OSX.

## Known scope

- This path targets minimal reproducible runtime (SUMO + GDAL).
- Behave/CityFlow/FOFEM/kPERIL macOS parity is out of scope for this minimal setup.
