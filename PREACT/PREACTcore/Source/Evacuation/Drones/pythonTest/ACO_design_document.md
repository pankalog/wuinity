# Methodological Formulation of a Static-Network-Weighted Decentralized ACO for Persistent UAV Road Monitoring

## 1. Problem definition and scope

This work addresses persistent, decentralized road-network monitoring by a multi-UAV swarm under single-depot operational constraints.

The road network is represented as a static graph extracted from SUMO `net.xml`. UAVs repeatedly traverse graph edges to maintain up-to-date situational awareness. Edge priorities are non-uniform and are encoded by a static criticality weight, computed before simulation.

The formulation intentionally relies on infrastructure-only priors. Runtime policy does not depend on scenario-specific dynamic inputs (for example, evacuation waypoints or live route reassignment).

The control objective is to reduce information staleness on critical edges while preserving broad network coverage under local decision constraints.

---

## 2. Graph representation

Two graph representations are used:

- `G_D` (directed): used during static assignment for demand-related weight terms.
- `G_U` (undirected): used for patrol simulation and topological metrics.

The undirected patrol abstraction is chosen because aerial monitoring is edge-centric and direction-agnostic at the sensing level.

To avoid infeasible evaluation artifacts, only the largest connected component is retained for simulation and metrics. This prevents unreachable subgraphs from inflating stale-edge statistics.

---

## 3. Static edge-criticality weight construction

Each edge `e` receives a static weight `W_e in [0,1]`:

`W_e = w_d * f_demand(e) + w_t * f_topo(e) + w_r * f_red(e)`

with:

- `w_d = 0.5`
- `w_t = 0.3`
- `w_r = 0.2`

### 3.1 Demand pressure term `f_demand`

Static all-or-nothing assignment is performed on `G_D` from inferred origin nodes to inferred exit nodes along shortest travel-time routes. This yields edge volume `V_e` over an evacuation horizon `H` (hours).

`f_demand(e) = min(1, (V_e / H) / q_c(e))`

where `q_c(e)` is edge capacity.

Edge capacity is derived analytically from SUMO edge attributes (speed, lanes) through a Daganzo-style macroscopic reduction of car-following assumptions.

### 3.2 Topological criticality term `f_topo`

`f_topo` is computed as evacuation-subset edge betweenness:

`f_topo(e) = BC_evac(e) / max_k BC_evac(k)`

where `BC_evac` counts shortest paths between inferred source nodes and inferred exit nodes that cross edge `e`.

### 3.3 Redundancy term `f_red`

Irreplaceability is approximated by local edge connectivity class:

- `kappa(e) = 1` for bridges
- `kappa(e) = 2` otherwise

`f_red(e) = 1 / kappa(e)`

Thus bridges receive maximal redundancy criticality.

### 3.4 Why additive composition

An additive mixture is selected for interpretability and stability:

1. Each term contribution remains explicit.
2. Ablation and sensitivity analysis are straightforward.
3. One weak term does not collapse the full score (as in multiplicative forms).

---

## 4. Monitoring objective

For each edge `e`, define age (staleness):

`Delta_e(t) = t - t_last(e)`

Weighted age:

`A_e(t) = W_e * Delta_e(t)`

Primary evaluation criteria:

- mean weighted age
- p95 weighted age
- max weighted age
- freshness coverage: `P(Delta_e <= Delta_thr)` with `Delta_thr = 900 s`

This objective emphasizes refresh of critical infrastructure while preserving global observability.

---

## 5. ACO variant

The method is a decentralized, edge-based, local-transition ACO for persistent patrolling (not tour-construction ACO).

At UAV `i`, current node `u_i`, candidate feasible edges are adjacent edges that satisfy energy feasibility constraints.

For candidate edge `e`, transition score is:

`S_i,e(t) = tau_e(t)^alpha * eta_e(t)^beta / (1 + n_e(t))^gamma`

where:

- `tau_e(t)`: pheromone level
- `eta_e(t)`: heuristic utility
- `n_e(t)`: number of UAVs currently traversing edge `e` (anti-crowding)
- `alpha, beta, gamma`: sensitivity exponents

Sampling probability is roulette-normalized over feasible candidates.

---

## 6. Heuristic function and decision mechanics

Heuristic is defined as a weighted blend of static priority and staleness:

`eta_e(t) = eps + lambda_w * W_e + lambda_a * min(Delta_e(t) / Delta_s, 3)`

with:

- `eps = 1e-6`
- `lambda_w = 0.4`
- `lambda_a = 0.6`
- `Delta_s = 900 s`

Interpretation:

- `W_e` provides strategic, infrastructure-level importance.
- Age term provides urgency for revisit.
- Overlap denominator discourages local clustering and redundant traversals.

---

## 7. Pheromone dynamics

### 7.1 Initialization

`tau_e(0) = tau_0`, with `tau_0 = 1.0`.

### 7.2 Local update on edge selection

When an edge is selected for patrol:

`tau_e <- (1 - rho_local) * tau_e + rho_local * tau_0`

This keeps local pheromone bounded near baseline and reduces runaway self-reinforcement.

### 7.3 Global evaporation

At each simulation step:

`tau_e <- (1 - rho_global) * tau_e`

This prevents stale reinforcement from dominating long horizons.

### 7.4 Deposit on completed patrol traversal

After successful patrol scan:

`tau_e <- tau_e + delta * (0.5 * W_e + 0.5 * min(Delta_before / Delta_s, 1))`

Deposit is tied to both strategic edge importance and scan information value.

---

## 8. UAV operational state machine

Each UAV follows:

`patrol -> return -> charge -> patrol`

### 8.1 Battery safety logic

Return trigger:

`B_i(t) <= T_to_station(u_i) + B_reserve`

Candidate move `(u,v)` is feasible only if:

`B_i(t) - T_uv - T_to_station(v) >= B_reserve`

### 8.2 Return policy

Return route is shortest-path to station (length-weighted).

### 8.3 Charging model

Battery is replenished linearly over `T_charge`, then UAV re-enters patrol.

### 8.4 Spawn policy

Two modes are retained:

- `station`: all UAVs co-located at depot (operationally realistic)
- `random`: diagnostic mode to isolate spawn bias effects

---

## 9. Simulation loop ordering

At each discrete step:

1. Apply global pheromone evaporation.
2. Update each UAV by state (charge, traversal, dispatch).
3. On patrol-edge completion, update:
   - `t_last(e)`
   - visit counters
   - deposit rule
4. Sample KPIs at fixed intervals.

This ordering was selected to maximize auditability and reproducibility.

---

## 10. Evaluation outputs and diagnostics

The framework reports:

- weighted-age trajectories (mean, p95, max)
- freshness coverage time series
- battery envelope and state occupancy
- top scanned-edge table
- scan-count and age histograms
- weight-vs-scan scatter
- Pareto scan concentration curve
- spatial maps (scan rate and final age)
- expected-vs-observed alignment:
  - `corr(W, scan_rate)`
  - top-k overlap
  - over-served and under-served edge lists
  - alignment delta map (`observed - expected`)

Robust p95 normalization is used in maps to avoid outlier-dominated colormap collapse.

---

## 11. Design rationale summary

1. Static network-only weighting enables deployment without scenario-specific priors.
2. Connected-component filtering ensures feasible, unbiased evaluation space.
3. Local decentralized policy reflects realistic communication/control constraints.
4. Overlap penalty mitigates redundant swarming on single edges.
5. Weight + age heuristic balances strategic priority and revisit urgency.
6. Explicit energy constraints preserve operational realism.
7. Rich diagnostics make lock-in and depot bias measurable.

---

## 12. Known limitations

- Single-depot initialization can induce persistent spatial bias.
- Local-only movement may delay access to distant high-priority edges.
- Redundancy approximation (`kappa in {1,2}`) is efficient but coarse.
- Weight construction is static; dynamic demand assimilation is not included.

---

## 13. Current reference parameterization

Fleet:

- `N = 20`
- speed `= 75 km/h (20.83 m/s)`
- battery endurance `= 270000 s` (diagnostic high-endurance mode)
- reserve fraction `= 0.10`
- recharge duration `= 1200 s`

ACO:

- `alpha = 0.5`
- `beta = 2.0`
- `gamma = 1.5`
- `tau_0 = 1.0`
- `rho_local = 0.02`
- `delta = 0.15`
- `rho_global = 0.0005`
- `Delta_s = 900 s`

Weight mix:

- `w_demand = 0.5`
- `w_topo = 0.3`
- `w_redundancy = 0.2`

---

## 14. Reproducibility references

Primary notebook:

- `sumo_only_local_aco_step_by_step.ipynb`

Weight construction notebook:

- `sumo_only_static_weights.ipynb`

Core modules:

- `metrics/network_loader.py`
- `metrics/capacity.py`
- `metrics/demand.py`
- `metrics/topology.py`
- `metrics/redundancy.py`
- `metrics/weights.py`
