"""Compare static road weights against Roxborough traffic output.

This script computes static edge weights with the same metrics used in the
notebooks, but with WUI-aligned demand assignment:

- applies EvacuationGroup polygon culling (if enabled)
- uses group destination CDF priors for expected origin->exit demand split

Then it compares that split against observed traffic outputs in _output.
"""

from __future__ import annotations

import argparse
import csv
from pathlib import Path

import numpy as np

from metrics import (
    annotate_capacity,
    assign_static_demand,
    assign_static_demand_by_exit,
    build_graphs,
    combine_weights,
    compute_f_demand,
    compute_f_redundancy,
    compute_f_topo,
    load_scenario,
    load_sumo_network,
)


def parse_traffic_output(path: Path):
    with path.open() as f:
        rows = list(csv.DictReader(f, skipinitialspace=True))
    last = rows[-1]

    exits = ["goal_highway", "goalE", "goalF", "goalR"]
    by_exit = {k: int(float(last[f"{k} cars arrived"])) for k in exits}
    peak_flows = {k: max(float(r[f"{k} flow [veh./h]"]) for r in rows) for k in exits}

    return {
        "cars_injected": int(float(last["Total cars injected"])),
        "cars_arrived": int(float(last["Total cars arrived"])),
        "time_end": float(last["Time(s)"]),
        "peak_cars_in_system": max(float(r["Current cars in system"]) for r in rows),
        "by_exit": by_exit,
        "peak_flows": peak_flows,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--net",
        default="../../../../../../Examples/NFDRS4_Behave/Roxborough/sumo/rox_big.net.xml",
    )
    parser.add_argument(
        "--wui",
        default="../../../../../../Examples/NFDRS4_Behave/Roxborough/Roxborough_global_smoke_drones.wui",
    )
    parser.add_argument(
        "--traffic-output",
        default="../../../../../../Examples/NFDRS4_Behave/Roxborough/_output/Roxborough_global_smoke_drones_traffic_output_0.csv",
    )
    parser.add_argument("--evac-hours", type=float, default=1.0)
    args = parser.parse_args()

    net_file = Path(args.net)
    wui_file = Path(args.wui)
    traffic_output_file = Path(args.traffic_output)

    sumo_net = load_sumo_network(str(net_file))
    DG, UG = build_graphs(sumo_net)
    annotate_capacity(DG)
    annotate_capacity(UG)

    scen = load_scenario(str(wui_file), DG)
    exit_nodes = [e[1] for e in scen["exits"]]

    if scen["origin_exit_vehicles"]:
        volumes, unassigned = assign_static_demand_by_exit(
            DG, scen["origin_exit_vehicles"]
        )
        demand_model = "EvacGroup destination CDF"
    else:
        volumes, unassigned = assign_static_demand(DG, scen["origins"], exit_nodes)
        demand_model = "nearest-exit all-or-nothing"

    f_demand = compute_f_demand(DG, volumes, evacuation_duration_hours=args.evac_hours)
    f_topo = compute_f_topo(UG, scen["origins"], exit_nodes, weight="travel_time")
    f_redundancy = compute_f_redundancy(UG)
    f_demand_ug = {
        (u, v): max(f_demand.get((u, v), 0.0), f_demand.get((v, u), 0.0))
        for u, v in UG.edges()
    }
    W = combine_weights(
        list(UG.edges()),
        f_demand_ug,
        f_topo,
        f_redundancy,
        w_demand=0.5,
        w_topo=0.3,
        w_redundancy=0.2,
    )

    observed = parse_traffic_output(traffic_output_file)
    observed_exit = observed["by_exit"]
    observed_total = sum(observed_exit.values())

    # Expected exit split from scenario priors
    expected_exit = {k: 0.0 for k in observed_exit.keys()}
    node_to_name = {node: name for name, node, _ in scen["exits"]}
    for split in scen["origin_exit_vehicles"].values():
        for exit_node, veh in split.items():
            name = node_to_name.get(exit_node)
            if name in expected_exit:
                expected_exit[name] += veh

    expected_total = sum(expected_exit.values())

    print("Static weighting summary")
    print("-----------------------")
    print(f"Demand model:                 {demand_model}")
    print(
        f"Households raw/post-cull:     {scen['total_households_raw']} / {scen['total_households']}"
    )
    print(f"Culled households:            {scen['culled_households']}")
    print(f"Group counts:                 {scen['group_counts']}")
    print(f"Unassigned OD tuples:         {len(unassigned)}")

    v = np.array(list(f_demand.values()))
    w = np.array(list(W.values()))
    print(f"f_demand mean / max:          {v.mean():.4f} / {v.max():.4f}")
    print(
        f"W mean / p95 / max:           {w.mean():.4f} / {np.percentile(w, 95):.4f} / {w.max():.4f}"
    )

    print("\nExit split comparison (expected static vs observed)")
    print("-----------------------------------------------")
    print(f"{'Exit':<14} {'Expected%':>10} {'Observed%':>10} {'Delta pp':>10}")
    for ex in ["goal_highway", "goalE", "goalF", "goalR"]:
        e_pct = (
            (expected_exit[ex] / expected_total * 100.0) if expected_total > 0 else 0.0
        )
        o_pct = (
            (observed_exit[ex] / observed_total * 100.0) if observed_total > 0 else 0.0
        )
        print(f"{ex:<14} {e_pct:>10.2f} {o_pct:>10.2f} {o_pct - e_pct:>10.2f}")

    print("\nObserved run summary")
    print("--------------------")
    print(
        f"Cars injected/arrived:        {observed['cars_injected']} / {observed['cars_arrived']}"
    )
    print(f"Peak cars in system:          {observed['peak_cars_in_system']:.0f}")
    print(f"Simulation end time [s]:      {observed['time_end']:.2f}")
    print("Peak exit flows [veh/h]:")
    for ex in ["goal_highway", "goalE", "goalF", "goalR"]:
        print(f"  {ex:<12} {observed['peak_flows'][ex]:.1f}")


if __name__ == "__main__":
    main()
