"""Static evacuation demand estimation.

Implements steps 1-2 of the Intini et al. (2019) four-step model:

  1. Trip generation:   one trip per vehicle per household at each origin.
  2. Trip distribution: assign each origin to its nearest exit by travel time,
                        then route all its vehicles along the shortest path
                        (all-or-nothing assignment).

This produces a per-edge estimated evacuation volume V_e (vehicles), which is
then divided by edge capacity to give f_demand = min(1, V_e / q_c).

User equilibrium assignment (Frank-Wolfe) would spread demand across competing
paths but requires iteration; all-or-nothing is the standard planning baseline
and is what the cited LOS analyses use.
"""

import networkx as nx
from collections import defaultdict


def assign_static_demand(
    DG, origins, exits, vehicles_per_origin=1.5, weight="travel_time"
):
    """All-or-nothing static assignment of evacuation trips to a directed graph.

    Args:
        DG:                 directed NetworkX graph with 'travel_time' on edges
        origins:            either
                              - iterable of origin node IDs (uniform weighting), or
                              - dict {node_id: vehicles} for weighted origins
                                (e.g., from the WUI population CSV)
        exits:              iterable of exit node IDs
        vehicles_per_origin: used only when origins is a plain iterable
        weight:             edge attribute to minimize

    Returns:
        volumes: dict {(u, v): V_e}, directed edge -> assigned vehicles
        unassigned: list of (origin, vehicles) with no path to any exit
    """
    # Normalize origins to a {node: vehicles} dict
    if isinstance(origins, dict):
        origin_weights = dict(origins)
    else:
        origin_weights = {o: vehicles_per_origin for o in origins}

    exits = set(exits)
    volumes = defaultdict(float)
    unassigned = []

    RG = DG.reverse(copy=False)
    _, path_to_exit = nx.multi_source_dijkstra(RG, sources=exits, weight=weight)

    for o, veh in origin_weights.items():
        if o not in path_to_exit:
            unassigned.append((o, veh))
            continue
        # Reversed-graph path is exit -> origin; flip to origin -> exit
        forward_path = list(reversed(path_to_exit[o]))
        for u, v in zip(forward_path[:-1], forward_path[1:]):
            volumes[(u, v)] += veh

    return dict(volumes), unassigned


def assign_static_demand_by_exit(DG, origin_exit_vehicles, weight="travel_time"):
    """Static assignment when each origin has an explicit exit split.

    This is the static counterpart of WUI-NITY's EvacGroupCDF destination model:
    each origin may send fractional demand to multiple exits according to fixed
    probabilities (expected-flow assignment).

    Args:
        DG: directed NetworkX graph with edge cost attribute ``weight``
        origin_exit_vehicles: dict {origin_node: {exit_node: vehicles}}
                             Vehicles may be fractional (expected demand).
        weight: edge attribute to minimize

    Returns:
        volumes: dict {(u, v): V_e}, directed edge -> assigned vehicles
        unassigned: list of (origin, exit, vehicles) with no path
    """
    volumes = defaultdict(float)
    unassigned = []

    # Compute shortest-path trees once per exit on the reversed graph so that
    # each query origin -> specific exit is O(path length).
    exits = sorted({e for split in origin_exit_vehicles.values() for e in split.keys()})
    RG = DG.reverse(copy=False)
    path_to_exit_by_exit = {}
    for e in exits:
        _, paths = nx.single_source_dijkstra(RG, source=e, weight=weight)
        path_to_exit_by_exit[e] = paths

    for origin, split in origin_exit_vehicles.items():
        for exit_node, veh in split.items():
            if veh <= 0:
                continue
            paths = path_to_exit_by_exit.get(exit_node, {})
            if origin not in paths:
                unassigned.append((origin, exit_node, veh))
                continue

            # Reversed-graph path is exit -> origin; flip to origin -> exit
            forward_path = list(reversed(paths[origin]))
            for u, v in zip(forward_path[:-1], forward_path[1:]):
                volumes[(u, v)] += veh

    return dict(volumes), unassigned


def compute_f_demand(DG, volumes, evacuation_duration_hours=2.0):
    """Compute the volume-to-capacity ratio f_demand = min(1, V_e / q_c) per edge.

    Volumes are total vehicles over the evacuation; q_c is veh/hour. We convert
    volume to an hourly flow by dividing by the assumed evacuation duration.

    Args:
        DG:                          directed graph with capacity annotated
                                     (q_c_edge_per_hour attribute)
        volumes:                     output of assign_static_demand
        evacuation_duration_hours:   over what horizon the trips flow

    Returns:
        f_demand: dict {(u, v): ratio in [0, 1]}, one entry per edge in DG
    """
    f_demand = {}
    for u, v, data in DG.edges(data=True):
        V_e = volumes.get((u, v), 0.0)
        hourly_flow = V_e / evacuation_duration_hours
        q_c = data["q_c_edge_per_hour"]
        ratio = min(1.0, hourly_flow / q_c) if q_c > 0 else 0.0
        f_demand[(u, v)] = ratio
    return f_demand


# ---------------------------------------------------------------------------
# Heuristics for picking origins / exits when you don't have GIS parcel data
# ---------------------------------------------------------------------------

RESIDENTIAL_TYPES = {
    "highway.residential",
    "highway.unclassified",
    "highway.living_street",
}
MAJOR_TYPES = {
    "highway.trunk",
    "highway.trunk_link",
    "highway.primary",
    "highway.primary_link",
    "highway.secondary",
    "highway.secondary_link",
}


def default_origins(DG):
    """Nodes incident to at least one residential edge — proxy for household origins."""
    origins = set()
    for u, v, data in DG.edges(data=True):
        if data.get("edge_type") in RESIDENTIAL_TYPES:
            origins.add(u)
            origins.add(v)
    return list(origins)


def default_exits(DG, top_k=None):
    """Pick exit nodes: degree-1 nodes incident to a major road (network boundary).

    If fewer than 2 such nodes exist, fall back to the top-k peripheral nodes
    incident to any road, ranked by distance from the network centroid.
    """
    UG_view = DG.to_undirected(as_view=False)
    candidates = []
    for n in UG_view.nodes():
        if UG_view.degree(n) != 1:
            continue
        # Is the single incident edge a major road?
        for _, _, data in UG_view.edges(n, data=True):
            if data.get("edge_type") in MAJOR_TYPES:
                candidates.append(n)
                break

    if len(candidates) >= 2:
        return candidates if top_k is None else candidates[:top_k]

    # Fallback: farthest-from-centroid degree-1 nodes
    import math

    xs = [DG.nodes[n]["x"] for n in DG.nodes()]
    ys = [DG.nodes[n]["y"] for n in DG.nodes()]
    cx, cy = sum(xs) / len(xs), sum(ys) / len(ys)
    leaves = [n for n in UG_view.nodes() if UG_view.degree(n) == 1]
    leaves.sort(key=lambda n: -math.hypot(DG.nodes[n]["x"] - cx, DG.nodes[n]["y"] - cy))
    k = top_k if top_k is not None else max(4, len(leaves) // 20)
    return leaves[:k]
