# WUInity Linux Setup (Minimal Reproducible Path)

This setup targets Linux x86_64 with the minimal module set (SUMO + GDAL).

## 1) Install prerequisites

From repo root:

```bash
./install_prereqs_linux.sh
```

## 2) Set shell environment

Add to `~/.bashrc` or `~/.zshrc`:

```bash
export SUMO_HOME="/usr/share/sumo"
export PROJ_LIB="/usr/share/proj"
export PATH="$SUMO_HOME/bin:$PATH"
export LANG="C.UTF-8"
export LC_ALL="C.UTF-8"
```

Reload shell:

```bash
source ~/.bashrc
```

## 3) Build and sync Linux `libsumocs`

On a Linux host, run:

```bash
./build_sumo_official_libsumocs.sh --sync
```

This should create:

- `PREACT/PREACTcore/ThirdParty/Eclipse.Sumo.Libsumo/linux-x64/libsumocs.so`

## 4) Stage native plugins

```bash
./stage_linux_minimal_plugins.sh
```

This script:

- Copies Linux SUMO native bridge into Unity plugin folder
- Copies Linux GDAL SWIG wrappers into Unity plugin folder
- Vendors transitive SUMO dependencies next to `libsumocs.so`
- Sets local `$ORIGIN` rpath where `patchelf` is available

## 5) Run runtime doctor

```bash
./doctor_linux_runtime.sh
```

Strict mode (fails on warnings too):

```bash
./doctor_linux_runtime.sh --strict
```

## 6) Unity launch helper

```bash
./run_wuinity_linux.sh
```

This helper stages plugins and prints environment status. Unity launch remains manual via Unity Hub on Linux.

## Known scope

- This path targets minimal reproducible runtime (SUMO + GDAL).
- Behave/CityFlow/FOFEM/kPERIL Linux parity is out of scope for this minimal setup.
