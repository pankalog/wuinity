"""Analytical road capacity from the Daganzo reduction of the Krauss model.

Per Rohaert (2025), SUMO's Krauss car-following model reduces in steady-state
to the Daganzo triangular fundamental diagram:

    v(k) = min( v_f,  v_f * (1/k - 1/k_j) / (1/k_c - 1/k_j) )

with

    k_j = 1 / (l_veh + g_min)          # jam density (veh/m/lane)
    k_c = 1 / (v_f * tau + 1/k_j)      # critical density (veh/m/lane)
    q_c = k_c * v_f                    # capacity (veh/s/lane)

Every term is a static property of the edge. Capacity scales linearly with
the number of lanes.
"""

# Defaults chosen to match SUMO's default Krauss parameters
DEFAULT_VEH_LENGTH = 5.0     # m  — SUMO default passenger car
DEFAULT_MIN_GAP = 2.5        # m  — SUMO default standstill gap
DEFAULT_TAU = 1.0            # s  — SUMO default driver reaction time


def daganzo_capacity(v_f, num_lanes,
                     veh_length=DEFAULT_VEH_LENGTH,
                     min_gap=DEFAULT_MIN_GAP,
                     tau=DEFAULT_TAU):
    """Compute Daganzo capacity for a road edge.

    Args:
        v_f:        free-flow speed (m/s)
        num_lanes:  number of lanes (capacity scales linearly)
        veh_length: mean vehicle length (m)
        min_gap:    minimum standstill gap between vehicles (m)
        tau:        driver reaction time (s)

    Returns:
        dict with k_j, k_c, q_c (all per-lane) and q_c_edge (total, veh/s)
    """
    h_min = veh_length + min_gap       # minimum space headway at standstill
    k_j = 1.0 / h_min                   # jam density, veh/m/lane

    # critical density: where free-flow branch meets congested branch
    k_c = 1.0 / (v_f * tau + 1.0 / k_j)
    q_c = k_c * v_f                     # capacity, veh/s/lane

    return {
        "k_j": k_j,
        "k_c": k_c,
        "q_c_per_lane": q_c,
        "q_c_edge": q_c * num_lanes,
        "q_c_per_lane_per_hour": q_c * 3600,
        "q_c_edge_per_hour": q_c * num_lanes * 3600,
    }


def annotate_capacity(G, veh_length=DEFAULT_VEH_LENGTH,
                      min_gap=DEFAULT_MIN_GAP, tau=DEFAULT_TAU):
    """Add capacity attributes to every edge in G, in place.

    Adds: k_j, k_c, q_c_per_lane, q_c_edge (veh/s), q_c_edge_per_hour (veh/h)
    """
    for u, v, data in G.edges(data=True):
        cap = daganzo_capacity(
            v_f=data["speed"],
            num_lanes=data["num_lanes"],
            veh_length=veh_length, min_gap=min_gap, tau=tau,
        )
        data.update(cap)
    return G
