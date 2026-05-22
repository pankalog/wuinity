"""Edge-level redundancy: how many alternative paths exist around each edge.

We use local edge connectivity kappa(u, v) for the endpoints of each edge,
which equals the minimum number of edges that must be removed to disconnect
u from v. A bridge has kappa = 1; a 2-edge-connected edge has kappa >= 2.

We then set f_redundancy(e) = 1 / kappa, so bridges score highest (most
critical / least redundant — biggest drone-monitoring payoff).

Computing exact kappa for every edge via max-flow would be O(|E| * max-flow).
For road networks a good approximation is:

  - kappa = 1 for bridges (O(V+E), Tarjan)
  - kappa = 2 for edges in biconnected components (these CAN be disconnected
    by removing 2 edges; rarely more in sparse road networks)
  - kappa >= 3 only in grid-dense urban cores

We compute this cheaply via biconnected component decomposition, which is
sufficient for the drone-monitoring decision.
"""

import networkx as nx


def compute_edge_connectivity(UG):
    """Approximate edge-connectivity per edge via biconnected component analysis.

    Returns:
        dict {edge: kappa}, where kappa is 1 for bridges and 2 otherwise.
        (Exact kappa >= 3 is rare in road networks and not worth the cost;
         callers wanting it can fall back to nx.edge_connectivity per edge.)
    """
    bridges = set()
    for u, v in nx.bridges(UG):
        bridges.add(frozenset((u, v)))

    kappa = {}
    for u, v in UG.edges():
        if frozenset((u, v)) in bridges:
            kappa[(u, v)] = 1
        else:
            kappa[(u, v)] = 2
    return kappa


def compute_f_redundancy(UG):
    """f_redundancy(e) = 1 / kappa(e). Bridges score 1.0, redundant edges 0.5."""
    kappa = compute_edge_connectivity(UG)
    return {e: 1.0 / k for e, k in kappa.items()}
