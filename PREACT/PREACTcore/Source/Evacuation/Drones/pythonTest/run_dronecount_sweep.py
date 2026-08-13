#!/usr/bin/env python3
"""
Sweep harness: run the Roxborough ACO scenario once per DroneCount in [start, end],
extract a small validation summary per run, and write everything into
_output/dronecount_sweep/ for later plotting.

Per run, we write:
  - _output/dronecount_sweep/validation_n{N:02}.csv   (tiny: time, truth, swarm_estimate, coverage_pct)

We also keep a one-time ground-truth cache:
  - _output/dronecount_sweep/_ground_truth.csv

The bulky raw telemetry (aco_edge_samples.csv, edge_density_1s.xml) is deleted
after each run to save disk; per-run scan_events.csv and graph_edges.csv are kept
so you can re-derive anything later without re-simulating.

USAGE
    python3 run_dronecount_sweep.py                  # default: N=1..50
    python3 run_dronecount_sweep.py --start 1 --end 10
    python3 run_dronecount_sweep.py --no-skip        # re-run even if summary exists
    python3 run_dronecount_sweep.py --traffic-seed 424242
    python3 run_dronecount_sweep.py --dry-run        # just print what would happen
"""

from __future__ import annotations

import argparse
import csv
import re
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[6]
DEFAULT_WUI = ROOT / "Examples/NFDRS4_Behave/Roxborough/Roxborough_global_smoke_drones.wui"
# The native apphost (`.../net8.0/PREACT`) requires a system-installed .NET 8 runtime,
# which isn't present on this machine — only the SDK is. Invoking via `dotnet PREACT.dll`
# bypasses the apphost and uses the SDK's runtime, which works.
PREACT_DLL = ROOT / "PREACT/PREACTexecute/bin/Debug/net8.0/PREACT.dll"
DOTNET = shutil.which("dotnet") or "dotnet"

# Validation hyperparameters — match the notebook so results are directly comparable.
FRESHNESS_WINDOW_S = 120.0
SAMPLE_EVERY_S = 10.0

# Default deterministic seeds used by generated sweep .wui files. A negative value
# preserves legacy time-seeded behaviour in PREACT/SUMO/ACO.
SIMULATION_RANDOM_SEED = 424242
SUMO_RANDOM_SEED = 424242
ACO_RANDOM_SEED = 424243

# These are all derived from the chosen .wui and (re)assigned by configure_paths(),
# called once at import with DEFAULT_WUI and again in main() if --wui is passed.
BASE_WUI: Path
SCENARIO_DIR: Path
OUTPUT_DIR: Path
BASE_NAME: str
SUMO_LIVE_DUMP: Path
SWEEP_DIR: Path
GROUND_TRUTH_CACHE: Path


def _routing_policy(wui: Path) -> str:
    """Read RoutingPolicy from the .wui so sweeps of different policies don't clobber."""
    m = re.search(r"^\s*RoutingPolicy=(\w+)", wui.read_text(), flags=re.M)
    return m.group(1) if m else "Unknown"


def _simulation_name(wui: Path) -> str:
    """Read Name= from the [Simulation] block of the .wui.

    Two scenarios can share the same RoutingPolicy (e.g. the tuned stigmergic run and a
    zero-deposit uniform-walk run are both `AcoStigmergic`) yet must not clobber each
    other's sweep folder. The [Simulation] Name distinguishes them. We deliberately read
    only the Name inside [Simulation] — the .wui has other Name= fields under
    [Destination]/[EvacuationGroup]/etc. Returns "" if there is no [Simulation] Name.
    """
    text = wui.read_text()
    section = re.search(r"(?ms)^\[Simulation\]\s*\n(.*?)(?=^\[|\Z)", text)
    if not section:
        return ""
    key = re.search(r"(?m)^Name=(.*)$", section.group(1))
    return key.group(1).strip() if key else ""


def configure_paths(wui: Path, tag: str = "") -> None:
    """Point the harness at a specific .wui and recompute every derived path.

    Sweep results live in a policy-specific subfolder (e.g. dronecount_sweep_Raster/
    vs dronecount_sweep_AcoStigmergic/) so the two policy sweeps coexist. BASE_NAME is
    the .wui stem, which becomes the per-run simulation Name and telemetry prefix.

    `tag` appends a suffix to the sweep folder (dronecount_sweep_<Policy>_<tag>/) so a
    fresh batch of experiments can be run without overwriting a previous batch.
    """
    global BASE_WUI, SCENARIO_DIR, OUTPUT_DIR, BASE_NAME, SUMO_LIVE_DUMP, SWEEP_DIR, GROUND_TRUTH_CACHE
    BASE_WUI = wui.resolve()
    SCENARIO_DIR = BASE_WUI.parent
    OUTPUT_DIR = SCENARIO_DIR / "_output"
    BASE_NAME = BASE_WUI.stem
    SUMO_LIVE_DUMP = OUTPUT_DIR / "edge_density_1s.xml"
    # Key the sweep folder on BOTH the routing policy AND the [Simulation] Name so that
    # two scenarios sharing a policy (e.g. tuned-stigmergic vs zero-deposit uniform-walk,
    # both `AcoStigmergic`) never write into the same folder. Fall back to the .wui stem
    # when [Simulation] Name is absent, so behaviour is well-defined for any .wui.
    scenario = _simulation_name(BASE_WUI) or BASE_NAME
    parts = [_routing_policy(BASE_WUI), scenario]
    if tag:
        parts.append(tag)
    SWEEP_DIR = OUTPUT_DIR / ("dronecount_sweep_" + "_".join(parts))
    GROUND_TRUTH_CACHE = SWEEP_DIR / "_ground_truth.csv"


configure_paths(DEFAULT_WUI)


def upsert_section_value(text: str, section: str, key: str, value: str) -> str:
    """Set key=value inside [section], appending it if missing."""
    section_re = re.compile(rf"(?ms)^(\[{re.escape(section)}\]\n)(.*?)(?=^\[|\Z)")

    def replace(match: re.Match[str]) -> str:
        header, body = match.group(1), match.group(2)
        key_re = re.compile(rf"(?m)^{re.escape(key)}=.*$")
        replacement = f"{key}={value}"
        if key_re.search(body):
            body = key_re.sub(replacement, body, count=1)
        else:
            if body and not body.endswith("\n"):
                body += "\n"
            body += replacement + "\n"
        return header + body

    updated, count = section_re.subn(replace, text, count=1)
    if count == 0:
        raise RuntimeError(f"Cannot set {key}: missing [{section}] section in {BASE_WUI}")
    return updated


def make_wui(n: int) -> Path:
    """Materialise a per-run .wui in the scenario folder with DroneCount=N and a unique Name.

    Replaces only the FIRST `Name=` line (the [Simulation] one at the top of the
    file). The .wui has additional `Name=` fields under [Destination],
    [Demographics], [EvacuationGroup], [ResponseCurve] — overwriting those
    collapses all destinations to the same key and crashes the parser with
    "An item with the same key has already been added".
    """
    text = BASE_WUI.read_text()
    # count=1 ensures we only rewrite the simulation Name= (which is the first one
    # in the file, in the [Simulation] block at the top).
    text = re.sub(r"^Name=.*$",       f"Name={BASE_NAME}_n{n:02}", text, count=1, flags=re.M)
    # DroneCount= appears only once in the file ([DroneFleet]), so no count limit needed.
    text = re.sub(r"^DroneCount=.*$", f"DroneCount={n}",            text, flags=re.M)
    # Force deterministic traffic generation for sweep runs even if the base .wui lacks
    # explicit seeds. Traffic uses the [Simulation] RNG for households/destinations and
    # [SUMO] for libsumo; ACO gets a separate stream so drone decisions cannot perturb
    # vehicle generation through the shared PREACT RNG.
    text = upsert_section_value(text, "Simulation", "RandomSeed", str(SIMULATION_RANDOM_SEED))
    text = upsert_section_value(text, "SUMO", "RandomSeed", str(SUMO_RANDOM_SEED))
    if _routing_policy(BASE_WUI) == "AcoStigmergic":
        text = upsert_section_value(text, "AcoStigmergicPolicy", "RandomSeed", str(ACO_RANDOM_SEED))
    target = SCENARIO_DIR / f"_sweep_n{n:02}.wui"
    target.write_text(text)
    return target


def run_sim(wui: Path) -> int:
    """Invoke PREACTexecute on the given .wui synchronously. Returns exit code."""
    cmd = [DOTNET, str(PREACT_DLL), str(wui)]
    t0 = time.time()
    # cwd = scenario dir so relative paths inside the .wui (population files, SUMO config, etc.) resolve.
    rc = subprocess.run(cmd, cwd=str(SCENARIO_DIR)).returncode
    print(f"  [run_sim] rc={rc}, elapsed={time.time() - t0:.0f}s")
    return rc


def compute_ground_truth(xml_path: Path, cache_csv: Path) -> None:
    """Stream-parse the SUMO meandata XML; write (sim_time_s, total_vehicles) per 1-second interval."""
    print(f"  [ground truth] streaming {xml_path.name} ({xml_path.stat().st_size / 1e9:.2f} GB)...")
    t0 = time.time()
    rows: list[tuple[float, float]] = []
    cur_t: float | None = None
    cur_total = 0.0
    for _, elem in ET.iterparse(str(xml_path), events=("end",)):
        if elem.tag == "interval":
            if cur_t is not None:
                rows.append((cur_t, cur_total))
            try:
                cur_t = float(elem.get("begin", "nan"))
            except (TypeError, ValueError):
                cur_t = None
            cur_total = 0.0
            elem.clear()
        elif elem.tag == "edge":
            try:
                cur_total += float(elem.get("sampledSeconds", "0"))
            except (TypeError, ValueError):
                pass
            elem.clear()
    if cur_t is not None:
        rows.append((cur_t, cur_total))
    cache_csv.parent.mkdir(parents=True, exist_ok=True)
    with cache_csv.open("w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["sim_time_s", "ground_truth_vehicles"])
        w.writerows(rows)
    print(f"  [ground truth] {len(rows)} rows, parsed in {time.time() - t0:.1f}s -> {cache_csv.name}")


def compute_validation(scan_csv: Path, graph_csv: Path, truth_csv: Path, out_csv: Path,
                       cells_csv: Path | None = None, policy: str = "") -> None:
    """Per-run validation summary: time series of (truth, swarm_estimate, coverage_pct).

    Mirrors the notebook's `validation` cell exactly so harness output and interactive
    output are 1:1 comparable.

    Coverage denominator: number of scannable units, chosen by the active routing policy
    (passed in by the caller, who knows it from the .wui). The Raster policy is keyed by
    cell index so its denominator is the number of active cells from `cells_csv`; every
    other (edge-based) policy uses the number of edges from `graph_csv`.

    Earlier versions chose the denominator by file presence (`cells_csv.exists()`), which
    silently picked up STALE raster-cells files left over from prior sweeps that shared
    OUTPUT_DIR and produced a discontinuity in the ACO coverage curve at the boundary
    between sweeps. The policy is the authoritative source, not the filesystem.
    """
    import numpy as np
    import pandas as pd

    truth = pd.read_csv(truth_csv)
    truth_lookup = dict(zip(truth.sim_time_s.astype(int), truth.ground_truth_vehicles))

    is_raster = policy.lower() == "raster"
    if is_raster:
        if cells_csv is None or not cells_csv.exists():
            raise RuntimeError(
                f"Raster policy expects a *_raster_cells.csv but it is missing: {cells_csv}. "
                f"The simulator should emit it from SwarmDroneModule.cs; check that the run "
                f"actually executed under the Raster policy."
            )
        n_units = len(pd.read_csv(cells_csv))
    else:
        if not graph_csv.exists():
            raise RuntimeError(
                f"Edge-based policy '{policy}' expects a *_graph_edges.csv but it is missing: "
                f"{graph_csv}."
            )
        n_units = len(pd.read_csv(graph_csv))
    scans = (
        pd.read_csv(scan_csv)
        if scan_csv.exists() and scan_csv.stat().st_size > 0
        else None
    )

    t_min = float(truth.sim_time_s.min())
    t_max = float(truth.sim_time_s.max())
    qt = np.arange(t_min, t_max + 1e-6, SAMPLE_EVERY_S)
    swarm = np.zeros_like(qt)
    coverage = np.zeros_like(qt, dtype=np.int64)

    if scans is not None and len(scans):
        for _, g in scans.sort_values("sim_time_s").groupby("edge_index"):
            st = g["sim_time_s"].to_numpy()
            sc = g["estimated_vehicle_count"].to_numpy()
            idx = np.searchsorted(st, qt, side="right") - 1
            valid = idx >= 0
            safe_idx = np.where(valid, idx, 0)
            fresh = valid & ((qt - st[safe_idx]) <= FRESHNESS_WINDOW_S)
            swarm += np.where(fresh, sc[safe_idx], 0.0)
            coverage += fresh.astype(np.int64)

    truth_arr = np.array([truth_lookup.get(int(t), 0.0) for t in qt])
    coverage_pct = 100.0 * coverage / max(1, n_units)

    pd.DataFrame({
        "sim_time_s": qt,
        "truth": truth_arr,
        "swarm_estimate": swarm,
        "coverage_pct": coverage_pct,
    }).to_csv(out_csv, index=False)


def cleanup_bulky(prefix: str) -> None:
    """Remove the huge per-run dumps. Keep small CSVs for re-derivation if needed."""
    for name in (
        f"{prefix}_aco_edge_samples.csv",
        f"{prefix}_state_samples.csv",   # large too; not needed for validation curves
    ):
        p = OUTPUT_DIR / name
        if p.exists():
            sz = p.stat().st_size
            p.unlink()
            print(f"  [cleanup] removed {name} ({sz / 1e6:.0f} MB)")


def purge_stale_telemetry(prefix: str, policy: str) -> None:
    """Delete any pre-existing telemetry files for THIS prefix before invoking the sim.

    OUTPUT_DIR is shared across every sweep and every policy. If a prior run wrote files
    with the same prefix under a different policy, those files would still be on disk
    when this run starts. compute_validation() then risks picking up a stale file as the
    coverage denominator. We pre-emptively delete any per-prefix telemetry so the run's
    own writes are the only thing on disk by the time validation reads them back. Files
    legitimately produced by this run (under its current policy) are re-created by the
    simulator on its own.
    """
    candidates = [
        f"{prefix}_scan_events.csv",
        f"{prefix}_graph_edges.csv",
        f"{prefix}_raster_cells.csv",
        f"{prefix}_aco_edge_samples.csv",
        f"{prefix}_state_samples.csv",
        f"{prefix}_ground_truth_total.csv",
    ]
    removed = []
    for name in candidates:
        p = OUTPUT_DIR / name
        if p.exists():
            p.unlink()
            removed.append(name)
    if removed:
        print(f"  [purge stale, policy={policy}] removed {len(removed)} pre-existing files: "
              f"{', '.join(removed)}")


def main() -> int:
    global SIMULATION_RANDOM_SEED, SUMO_RANDOM_SEED, ACO_RANDOM_SEED

    ap = argparse.ArgumentParser()
    ap.add_argument("--wui", type=Path, default=None,
                    help="path to the base .wui to sweep (default: the Raster scenario). "
                         "Results route to dronecount_sweep_<RoutingPolicy>/ automatically.")
    ap.add_argument("--tag", type=str, default="",
                    help="suffix for the sweep folder (dronecount_sweep_<Policy>_<tag>/), "
                         "so a fresh batch doesn't overwrite a previous one. "
                         "e.g. --tag 20260601 or --tag rerun2")
    ap.add_argument("--start", type=int, default=1)
    ap.add_argument("--end",   type=int, default=50)
    ap.add_argument("--no-skip", action="store_true",
                    help="rerun even if a per-N validation CSV already exists")
    ap.add_argument("--traffic-seed", type=int, default=SIMULATION_RANDOM_SEED,
                    help="fixed PREACT seed for household response times, destinations, "
                         "and other traffic-affecting random draws; use -1 for legacy "
                         "time-seeded behaviour")
    ap.add_argument("--sumo-seed", type=int, default=None,
                    help="fixed SUMO/libsumo seed; defaults to --traffic-seed")
    ap.add_argument("--aco-seed", type=int, default=None,
                    help="fixed ACO drone-routing seed; defaults to --traffic-seed + 1 "
                         "so drone randomness cannot perturb traffic randomness")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    SIMULATION_RANDOM_SEED = args.traffic_seed
    SUMO_RANDOM_SEED = args.sumo_seed if args.sumo_seed is not None else args.traffic_seed
    ACO_RANDOM_SEED = (
        args.aco_seed
        if args.aco_seed is not None
        else (args.traffic_seed + 1 if args.traffic_seed >= 0 else -1)
    )

    # Resolve the .wui (explicit --wui or the default) and apply the tag in one place.
    wui = args.wui if args.wui is not None else DEFAULT_WUI
    if not wui.exists():
        print(f"ERROR: .wui not found: {wui}")
        return 2
    configure_paths(wui, tag=args.tag)

    print(f"Base .wui:   {BASE_WUI}")
    print(f"Policy:      {_routing_policy(BASE_WUI)}")
    print(f"Sweep dir:   {SWEEP_DIR}")
    print(f"Seeds:       PREACT={SIMULATION_RANDOM_SEED}, SUMO={SUMO_RANDOM_SEED}, ACO={ACO_RANDOM_SEED}")

    if not BASE_WUI.exists():
        print(f"ERROR: base .wui not found: {BASE_WUI}")
        return 2
    if not PREACT_DLL.exists():
        print(f"ERROR: PREACT.dll not found: {PREACT_DLL}\n"
              f"Build it with: cd PREACT/PREACTexecute && dotnet build -c Debug")
        return 2
    if shutil.which("dotnet") is None:
        print("ERROR: `dotnet` not on PATH; install .NET 8 SDK or add it to PATH.")
        return 2

    SWEEP_DIR.mkdir(parents=True, exist_ok=True)

    # Ground truth is scenario-dependent: it must be recomputed whenever the .wui /
    # SUMO scenario changes, otherwise a stale _ground_truth.csv from a previous
    # scenario gets reused (the cause of the "wrong ground truth for raster" bug).
    # We therefore refresh it ONCE per invocation, from the first run that actually
    # executes in this call, overwriting any cached copy. Within the invocation all
    # runs share the same scenario, so a single recompute suffices.
    ground_truth_refreshed = False

    for n in range(args.start, args.end + 1):
        print(f"\n=== Drone count {n}/{args.end} ===")
        out_csv = SWEEP_DIR / f"validation_n{n:02}.csv"
        if out_csv.exists() and not args.no_skip:
            print(f"  skip (already have {out_csv.name})")
            continue
        if args.dry_run:
            print(f"  [dry-run] would generate sweep wui and invoke PREACT for N={n}")
            continue

        wui = make_wui(n)
        # Scrub any stale per-prefix telemetry from prior sweeps before launching the sim,
        # so compute_validation() can't accidentally read another run's files. See the
        # docstring of purge_stale_telemetry() for the full rationale.
        prefix_for_run = f"{BASE_NAME}_n{n:02}_0_drone"
        purge_stale_telemetry(prefix_for_run, _routing_policy(BASE_WUI))
        rc = run_sim(wui)
        if rc != 0:
            print(f"  FAILED rc={rc}, skipping post-processing for N={n}")
            continue

        # Recompute ground truth from THIS invocation's first fresh SUMO dump, overwriting
        # any stale cache. Guarded so it happens at most once per sweep invocation.
        if not ground_truth_refreshed and SUMO_LIVE_DUMP.exists():
            if GROUND_TRUTH_CACHE.exists():
                print(f"  [ground truth] refreshing stale cache {GROUND_TRUTH_CACHE.name} from current scenario")
            compute_ground_truth(SUMO_LIVE_DUMP, GROUND_TRUTH_CACHE)
            ground_truth_refreshed = True
        if not GROUND_TRUTH_CACHE.exists():
            print("  WARNING: no ground truth cache and no live XML; skipping validation.")
            continue

        prefix = f"{BASE_NAME}_n{n:02}_0_drone"
        scan_csv  = OUTPUT_DIR / f"{prefix}_scan_events.csv"
        graph_csv = OUTPUT_DIR / f"{prefix}_graph_edges.csv"
        # Only present for raster-policy runs (SwarmDroneModule.cs writes it conditionally
        # on `_policy is RasterRoutingPolicy`). The coverage denominator is now picked from
        # the policy itself, not from file presence, so a stale leftover here is harmless.
        cells_csv = OUTPUT_DIR / f"{prefix}_raster_cells.csv"
        compute_validation(scan_csv, graph_csv, GROUND_TRUTH_CACHE, out_csv, cells_csv,
                           policy=_routing_policy(BASE_WUI))
        print(f"  wrote {out_csv.name}")

        cleanup_bulky(prefix)
        # Drop the generated .wui too — keeping 50 copies in the scenario folder is noise.
        wui.unlink(missing_ok=True)

    print("\nSweep complete.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
