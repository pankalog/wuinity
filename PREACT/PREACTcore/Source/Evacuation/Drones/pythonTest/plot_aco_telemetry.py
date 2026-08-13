#!/usr/bin/env python3
"""
Quick telemetry plotting utility for PREACT drone ACO dumps.

Expected files in _output (same prefix):
  <prefix>_state_samples.csv
  <prefix>_scan_events.csv
  <prefix>_graph_edges.csv
  <prefix>_aco_edge_samples.csv   (ACO only)

Usage examples:
  python plot_aco_telemetry.py --output-dir /path/to/_output
  python plot_aco_telemetry.py --output-dir /path/to/_output --prefix Roxborough_global_smoke_drones_aco_0_drone
"""

from __future__ import annotations

import argparse
from pathlib import Path

import matplotlib.pyplot as plt
import pandas as pd


STATE_SUFFIX = "_state_samples.csv"
SCAN_SUFFIX = "_scan_events.csv"
GRAPH_SUFFIX = "_graph_edges.csv"
EDGE_SUFFIX = "_aco_edge_samples.csv"


def resolve_prefix(output_dir: Path, prefix: str | None) -> str:
    if prefix:
        return prefix

    candidates = sorted(output_dir.glob(f"*{STATE_SUFFIX}"), key=lambda p: p.stat().st_mtime)
    if not candidates:
        raise FileNotFoundError(f"No telemetry files matching *{STATE_SUFFIX} found in {output_dir}")

    latest = candidates[-1]
    return latest.name[: -len(STATE_SUFFIX)]


def load_csv(path: Path) -> pd.DataFrame | None:
    if not path.exists():
        return None
    return pd.read_csv(path)


def plot_drone_trajectories(drone_df: pd.DataFrame, out_path: Path) -> None:
    fig, ax = plt.subplots(figsize=(8, 8))
    for drone_id, group in drone_df.groupby("drone_id"):
        group = group.sort_values("sim_time_s")
        ax.plot(group["x"], group["y"], linewidth=0.9, alpha=0.8)
        ax.scatter(group["x"].iloc[0], group["y"].iloc[0], s=12)

    ax.set_title("Drone Trajectories")
    ax.set_xlabel("x")
    ax.set_ylabel("y")
    ax.set_aspect("equal", adjustable="box")
    ax.grid(True, alpha=0.2)
    fig.tight_layout()
    fig.savefig(out_path, dpi=200)
    plt.close(fig)


def plot_pheromone_stats(edge_df: pd.DataFrame, out_path: Path) -> None:
    stats = edge_df.groupby("sim_time_s", as_index=False).agg(
        tau_s_mean=("tau_s", "mean"),
        tau_v_mean=("tau_v", "mean"),
        tau_c_mean=("tau_c", "mean"),
        occupancy_sum=("occupancy_count", "sum"),
    )

    fig, axes = plt.subplots(2, 1, figsize=(10, 8), sharex=True)
    axes[0].plot(stats["sim_time_s"], stats["tau_s_mean"], label="tau_s mean")
    axes[0].plot(stats["sim_time_s"], stats["tau_v_mean"], label="tau_v mean")
    axes[0].plot(stats["sim_time_s"], stats["tau_c_mean"], label="tau_c mean")
    axes[0].set_ylabel("mean pheromone")
    axes[0].grid(True, alpha=0.2)
    axes[0].legend()

    axes[1].plot(stats["sim_time_s"], stats["occupancy_sum"], color="black", label="occupancy sum")
    axes[1].set_xlabel("simulation time [s]")
    axes[1].set_ylabel("sum occupancy")
    axes[1].grid(True, alpha=0.2)
    axes[1].legend()

    fig.tight_layout()
    fig.savefig(out_path, dpi=200)
    plt.close(fig)


def plot_last_tau_heatmap(edge_df: pd.DataFrame, graph_df: pd.DataFrame, out_path: Path, field: str) -> None:
    latest_t = edge_df["sim_time_s"].max()
    latest = edge_df[edge_df["sim_time_s"] == latest_t].copy()
    merged = latest.merge(graph_df, on="edge_index", how="left")
    merged["mid_x"] = 0.5 * (merged["start_x"] + merged["end_x"])
    merged["mid_y"] = 0.5 * (merged["start_y"] + merged["end_y"])

    fig, ax = plt.subplots(figsize=(8, 8))
    sc = ax.scatter(merged["mid_x"], merged["mid_y"], c=merged[field], s=8, cmap="viridis")
    cbar = fig.colorbar(sc, ax=ax)
    cbar.set_label(field)
    ax.set_title(f"{field} at t={latest_t:0.1f}s")
    ax.set_xlabel("x")
    ax.set_ylabel("y")
    ax.set_aspect("equal", adjustable="box")
    ax.grid(True, alpha=0.15)
    fig.tight_layout()
    fig.savefig(out_path, dpi=200)
    plt.close(fig)


def plot_scan_density(scan_df: pd.DataFrame, out_path: Path) -> None:
    fig, ax = plt.subplots(figsize=(10, 4))
    ax.plot(scan_df["sim_time_s"], scan_df["measured_density_per_lane"], linewidth=0.9)
    ax.set_title("Scan Density Events")
    ax.set_xlabel("simulation time [s]")
    ax.set_ylabel("density [veh/m/lane]")
    ax.grid(True, alpha=0.2)
    fig.tight_layout()
    fig.savefig(out_path, dpi=200)
    plt.close(fig)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", required=True, help="Path to PREACT _output folder")
    parser.add_argument("--prefix", default=None, help="Telemetry prefix (without suffix)")
    args = parser.parse_args()

    output_dir = Path(args.output_dir)
    if not output_dir.exists():
        raise FileNotFoundError(f"Output dir does not exist: {output_dir}")

    prefix = resolve_prefix(output_dir, args.prefix)
    print(f"Using telemetry prefix: {prefix}")

    state_path = output_dir / f"{prefix}{STATE_SUFFIX}"
    scan_path = output_dir / f"{prefix}{SCAN_SUFFIX}"
    graph_path = output_dir / f"{prefix}{GRAPH_SUFFIX}"
    edge_path = output_dir / f"{prefix}{EDGE_SUFFIX}"

    state_df = load_csv(state_path)
    if state_df is None:
        raise FileNotFoundError(f"Missing required telemetry file: {state_path}")

    scan_df = load_csv(scan_path)
    graph_df = load_csv(graph_path)
    edge_df = load_csv(edge_path)

    plot_dir = output_dir / f"{prefix}_plots"
    plot_dir.mkdir(parents=True, exist_ok=True)

    print(f"Writing plots to: {plot_dir}")
    plot_drone_trajectories(state_df, plot_dir / "drone_trajectories.png")

    if scan_df is not None and len(scan_df) > 0:
        plot_scan_density(scan_df.sort_values("sim_time_s"), plot_dir / "scan_density_events.png")

    if edge_df is not None and graph_df is not None and len(edge_df) > 0 and len(graph_df) > 0:
        plot_pheromone_stats(edge_df, plot_dir / "pheromone_global_stats.png")
        for field in ("tau_s", "tau_v", "tau_c"):
            plot_last_tau_heatmap(edge_df, graph_df, plot_dir / f"{field}_last_heatmap.png", field)

    print("Done.")


if __name__ == "__main__":
    main()
