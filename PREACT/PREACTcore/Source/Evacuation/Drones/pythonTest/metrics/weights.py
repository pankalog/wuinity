"""Combine the three static weight components into a single edge weight."""


def combine_weights(edges, f_demand, f_topo, f_redundancy,
                    w_demand=0.5, w_topo=0.3, w_redundancy=0.2):
    """Combine components into a single static weight in [0, 1].

    Args:
        edges:         iterable of (u, v) edges to score
        f_demand:      dict {edge: value in [0, 1]}
        f_topo:        dict {edge: value in [0, 1]}
        f_redundancy:  dict {edge: value in [0, 1]}
        w_*:           component weights (should sum to 1)

    Missing components for an edge are treated as 0 — callers should supply
    components computed on the same edge set to avoid surprises.

    Returns:
        dict {edge: combined weight}
    """
    total_w = w_demand + w_topo + w_redundancy
    if abs(total_w - 1.0) > 1e-6:
        # Normalize silently — weights are relative importance, not a requirement.
        w_demand /= total_w
        w_topo /= total_w
        w_redundancy /= total_w

    out = {}
    for e in edges:
        fd = f_demand.get(e, 0.0)
        ft = f_topo.get(e, 0.0)
        fr = f_redundancy.get(e, 0.0)
        out[e] = w_demand * fd + w_topo * ft + w_redundancy * fr
    return out
