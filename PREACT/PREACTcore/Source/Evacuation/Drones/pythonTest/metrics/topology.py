"""Evacuation-weighted betweenness centrality.

Standard betweenness counts shortest paths between every node pair. For
evacuation we only care about paths from residential origins to exit nodes,
so we use nx.edge_betweenness_centrality_subset which restricts the source
and target sets.
"""

import networkx as nx


def compute_evac_betweenness(G, origins, exits, weight="travel_time"):
    """Edge betweenness restricted to origin -> exit shortest paths.

    If `origins` is a dict {node: vehicles}, we use the node list as sources;
    vehicle weighting is already captured in f_demand, and betweenness here is
    deliberately topology-only (how many origin-exit paths cross the edge).

    Returns:
        dict {edge: betweenness score, not normalized}
    """
    source_list = list(origins.keys()) if isinstance(origins, dict) else list(origins)
    bc = nx.edge_betweenness_centrality_subset(
        G, sources=source_list, targets=list(exits),
        weight=weight, normalized=False,
    )
    return bc


def compute_f_topo(G, origins, exits, weight="travel_time"):
    """f_topo(e) = BC_evac(e) / max(BC_evac) in [0, 1]."""
    bc = compute_evac_betweenness(G, origins, exits, weight=weight)
    if not bc:
        return {}
    max_bc = max(bc.values()) or 1.0
    return {e: v / max_bc for e, v in bc.items()}
