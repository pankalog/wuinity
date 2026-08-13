#!/usr/bin/env python3
"""
Descriptive statistics for the Roxborough Park SUMO road network, for the
journal paper's network-description section.

Directionality is collapsed: each physical road that SUMO stores as a pair of
opposing directed edges (`X` and `-X`) is merged into a single undirected road.
One-way roads remain a single road. All statistics below are therefore in terms
of PHYSICAL roads and undirected connectivity.

Usage:
    python3 roxborough_network_stats.py [path/to/rox_big.net.xml]
"""

from __future__ import annotations

import sys
from collections import Counter, defaultdict
from pathlib import Path

import numpy as np
import sumolib
import networkx as nx

ROOT = Path(__file__).resolve().parents[6]
NET = sys.argv[1] if len(sys.argv) > 1 else str(
    ROOT / "Examples/Development/sumo/rox_big.net.xml"
)


def fmt(x, n=1):
    return f"{x:,.{n}f}"


def pct(a, b):
    return f"{100.0 * a / b:.1f}%" if b else "n/a"


def base_id(edge_id: str) -> str:
    """OSM/SUMO convention: a two-way road is stored as 'X' (forward) and '-X'
    (backward). Stripping a single leading '-' groups the two directions of the
    same physical road; split segments keep their '#k' suffix so distinct
    physical segments stay separate."""
    return edge_id[1:] if edge_id.startswith("-") else edge_id


def build_physical_roads(edges):
    """Collapse opposing directed edges into physical roads.

    Returns a list of dicts, one per physical road:
      length      : metres (one direction; identical for both)
      lanes       : lanes per direction (max over the merged directions)
      speed_kmh   : free-flow speed
      rtype       : SUMO/OSM road class
      u, v        : the two junction IDs (unordered)
      twoway      : True if both directions were present
    """
    groups = defaultdict(list)
    for e in edges:
        groups[base_id(e.getID())].append(e)

    roads = []
    for bid, grp in groups.items():
        lengths = [e.getLength() for e in grp]
        lanes = max(e.getLaneNumber() for e in grp)
        speed = max(e.getSpeed() for e in grp) * 3.6  # km/h
        rtype = grp[0].getType() or "(unclassified)"
        u = grp[0].getFromNode().getID()
        v = grp[0].getToNode().getID()
        # Two-way iff at least two directed edges whose endpoints are reversed.
        twoway = False
        if len(grp) >= 2:
            ends = {(e.getFromNode().getID(), e.getToNode().getID()) for e in grp}
            twoway = any((b, a) in ends for (a, b) in ends if a != b)
        roads.append({
            "length": float(np.median(lengths)),
            "lanes": int(lanes),
            "speed_kmh": float(speed),
            "rtype": rtype,
            "u": u, "v": v,
            "twoway": twoway,
        })
    return roads


def main():
    print(f"Loading {NET} ...")
    net = sumolib.net.readNet(NET, withInternal=False)
    directed_edges = net.getEdges()
    nodes = net.getNodes()
    n_directed = len(directed_edges)

    roads = build_physical_roads(directed_edges)
    n_roads = len(roads)
    n_twoway = sum(r["twoway"] for r in roads)
    n_oneway = n_roads - n_twoway

    lengths = np.array([r["length"] for r in roads])             # m, physical
    lanes = np.array([r["lanes"] for r in roads])                # per direction
    speeds = np.array([r["speed_kmh"] for r in roads])
    total_len_m = float(lengths.sum())
    # lane-length: a two-way road carries `lanes` per direction in BOTH directions
    lane_len_m = float(sum(r["length"] * r["lanes"] * (2 if r["twoway"] else 1) for r in roads))

    # ---- Geographic extent (from edge shapes) ----
    xs, ys = [], []
    for e in directed_edges:
        for x, y in e.getShape():
            xs.append(x); ys.append(y)
    xs, ys = np.array(xs), np.array(ys)
    width_m, height_m = xs.max() - xs.min(), ys.max() - ys.min()
    bbox_km2 = (width_m / 1000.0) * (height_m / 1000.0)

    # ---- Road-class composition (by physical length) ----
    type_len = defaultdict(float)
    type_cnt = Counter()
    for r in roads:
        type_len[r["rtype"]] += r["length"]
        type_cnt[r["rtype"]] += 1

    # ---- Speed-limit breakdown (by physical length) ----
    speed_len = defaultdict(float)
    for r in roads:
        speed_len[round(r["speed_kmh"])] += r["length"]

    # ---- Undirected connectivity on physical roads ----
    UG = nx.Graph()
    for r in roads:
        UG.add_edge(r["u"], r["v"])
    components = sorted(nx.connected_components(UG), key=len, reverse=True)
    giant = components[0] if components else set()
    n_bridges = sum(1 for _ in nx.bridges(UG)) if UG.number_of_edges() else 0
    n_nodes = UG.number_of_nodes()

    # ---- Physical junction degree (physical roads meeting per junction) ----
    inc = defaultdict(int)
    for r in roads:
        inc[r["u"]] += 1
        inc[r["v"]] += 1
    degrees = np.array(list(inc.values()))
    deg_hist = Counter(degrees.tolist())
    n_intersections = int((degrees >= 3).sum())
    n_deadends = int((degrees == 1).sum())

    line = "=" * 70
    print(f"\n{line}\nROXBOROUGH PARK ROAD NETWORK — PHYSICAL (UNDIRECTED) STATISTICS\n{line}")
    print(f"(collapsed {n_directed:,} directed SUMO edges -> {n_roads:,} physical roads)")

    print("\n[ Size ]")
    print(f"  Physical roads:               {n_roads:,}")
    print(f"    two-way:                    {n_twoway:,} ({pct(n_twoway, n_roads)})")
    print(f"    one-way:                    {n_oneway:,} ({pct(n_oneway, n_roads)})")
    print(f"  Junctions (nodes):            {len(nodes):,}")
    print(f"  Total road length:            {fmt(total_len_m/1000)} km")
    print(f"  Total lane length:            {fmt(lane_len_m/1000)} km")
    print(f"  Mean junction degree (2E/V):  {fmt(2*n_roads/len(nodes), 2)}")

    print("\n[ Geographic extent ]")
    print(f"  Bounding box:                 {fmt(width_m/1000,2)} km (E-W) x {fmt(height_m/1000,2)} km (N-S)")
    print(f"  Bounding-box area:            {fmt(bbox_km2,2)} km^2")
    print(f"  Road density:                 {fmt(total_len_m/1000/bbox_km2)} km of road / km^2")

    print("\n[ Road length distribution (m) ]")
    print(f"  min / median / mean / max:    {fmt(lengths.min())} / {fmt(np.median(lengths))} / "
          f"{fmt(lengths.mean())} / {fmt(lengths.max())}")
    print(f"  p10 / p90 / p99:              {fmt(np.percentile(lengths,10))} / "
          f"{fmt(np.percentile(lengths,90))} / {fmt(np.percentile(lengths,99))}")

    print("\n[ Lanes per direction ]")
    for lc in sorted(set(lanes.tolist())):
        c = int((lanes == lc).sum())
        print(f"  {lc}-lane roads:               {c:,} ({pct(c, n_roads)})")

    print("\n[ Speed limits (free-flow) ]")
    print(f"  min / median / mean / max:    {fmt(speeds.min())} / {fmt(np.median(speeds))} / "
          f"{fmt(speeds.mean())} / {fmt(speeds.max())} km/h")
    for s in sorted(speed_len, key=lambda k: -speed_len[k])[:6]:
        print(f"  ~{s:>3} km/h:                   {fmt(speed_len[s]/1000)} km ({pct(speed_len[s], total_len_m)})")

    print("\n[ Road-class composition (by length) ]")
    for t in sorted(type_len, key=lambda k: -type_len[k])[:10]:
        print(f"  {t:<28s} {fmt(type_len[t]/1000):>8} km ({pct(type_len[t], total_len_m):>6})  "
              f"[{type_cnt[t]:,} roads]")

    print("\n[ Connectivity (undirected) ]")
    print(f"  Connected components:         {len(components)}")
    print(f"  Giant component:              {len(giant):,} nodes ({pct(len(giant), n_nodes)} of junctions)")
    if len(components) > 1:
        print(f"  Next-largest components:      {[len(c) for c in components[1:6]]}")
    print(f"  Bridges (cut roads):          {n_bridges:,} ({pct(n_bridges, n_roads)} of roads)")

    print("\n[ Physical junction degree (roads meeting per junction) ]")
    print(f"  Real intersections (deg>=3):  {n_intersections:,}")
    print(f"  Dead-ends (deg==1):           {n_deadends:,}")
    print(f"  Mean / max junction degree:   {fmt(degrees.mean(),2)} / {int(degrees.max())}")
    for d in sorted(deg_hist):
        print(f"    degree {d}:                   {deg_hist[d]:,} junctions ({pct(deg_hist[d], n_nodes)})")

    print(f"\n{line}")


if __name__ == "__main__":
    main()
