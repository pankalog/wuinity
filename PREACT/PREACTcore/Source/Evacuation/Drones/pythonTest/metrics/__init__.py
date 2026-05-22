"""Static edge-weight metrics for drone-ACO road network monitoring.

Three components, all precomputable from the SUMO network + GIS origins/exits:

- f_demand(e)     : estimated volume-to-capacity ratio under evacuation (Janfeshanaraghi LOS)
- f_topo(e)       : normalized evacuation betweenness centrality
- f_redundancy(e) : 1 / edge connectivity (bridges are the extreme case)

Capacity is derived analytically from the Daganzo macroscopic reduction of the
Krauss car-following model used in SUMO (Rohaert 2025). Demand is estimated by
static all-or-nothing assignment of residential trips to exit nodes
(Intini et al. 2019, four-step model, steps 1-2).
"""

from .network_loader import load_sumo_network, build_graphs
from .capacity import daganzo_capacity, annotate_capacity
from .demand import assign_static_demand, assign_static_demand_by_exit, compute_f_demand
from .topology import compute_evac_betweenness, compute_f_topo
from .redundancy import compute_edge_connectivity, compute_f_redundancy
from .weights import combine_weights
from .wui_loader import load_scenario, parse_wui

__all__ = [
    "load_sumo_network",
    "build_graphs",
    "daganzo_capacity",
    "annotate_capacity",
    "assign_static_demand",
    "assign_static_demand_by_exit",
    "compute_f_demand",
    "compute_evac_betweenness",
    "compute_f_topo",
    "compute_edge_connectivity",
    "compute_f_redundancy",
    "combine_weights",
    "load_scenario",
    "parse_wui",
]
