"""Load real Roxborough scenario data from a WUI-NITY .wui configuration.

Parses:
  - [Destination] blocks: exit points with lat/lon
  - [Demographics]:       cars per household
  - [EvacuationGroup]:    group polygons + destination CDF priors
  - [Population] file:    household origin + access points + occupants
  - [SUMO] UTMoffset:     to project lat/lon into the SUMO network's local CRS

Reproject lat/lon -> UTM zone 13N (from the SUMO net's projParameter), then
subtract the SUMO netOffset to land in network-local coordinates.
"""

import csv
import math
import re
from collections import defaultdict
from pathlib import Path

import pyproj

try:
    import shapefile  # pyshp
except Exception:  # pragma: no cover - optional dependency in some envs
    shapefile = None


# ---------------------------------------------------------------------------
# .wui parser
# ---------------------------------------------------------------------------


def parse_wui(wui_path):
    """Parse a .wui INI-like file into a list of (section, dict).

    Returns a list rather than a dict because sections like [Destination] and
    [EvacuationGroup] repeat.
    """
    blocks = []
    current_name = None
    current_data = None

    with open(wui_path) as f:
        for raw in f:
            line = raw.strip()
            if not line or line.startswith(";") or line.startswith("#"):
                continue
            m = re.match(r"^\[(.+)\]$", line)
            if m:
                if current_name is not None:
                    blocks.append((current_name, current_data))
                current_name = m.group(1)
                current_data = {}
                continue
            if "=" in line and current_data is not None:
                key, _, val = line.partition("=")
                current_data[key.strip()] = val.strip()
            elif current_data is not None:
                # Free-form line (e.g., response curve data points) — keep as list
                current_data.setdefault("_lines", []).append(line)
    if current_name is not None:
        blocks.append((current_name, current_data))

    return blocks


def wui_destinations(blocks):
    """Return [(name, lat, lon)] for every [Destination] of Type=Exit."""
    out = []
    for name, data in blocks:
        if name != "Destination":
            continue
        if data.get("Type", "Exit") != "Exit":
            continue
        lat, lon = [float(x) for x in data["LatLon"].split(",")]
        out.append((data["Name"], lat, lon))
    return out


def wui_demographics(blocks):
    """Average cars per household from the default Demographics block."""
    for name, data in blocks:
        if name == "Demographics" and data.get("Default", "false").lower() == "true":
            allow_more = data.get("AllowMoreThanOneCar", "false").lower() == "true"
            max_cars = int(data.get("MaxCars", 1))
            p_max = float(data.get("MaxCarsProbability", 0.0))
            if not allow_more or max_cars <= 1:
                return 1.0
            # If more than one car is allowed: expected cars = 1 + (max_cars-1) * p_max
            return 1.0 + (max_cars - 1) * p_max
    return 1.0


def wui_sumo_offset(blocks):
    """Return (offset_x, offset_y) from [SUMO] UTMoffset, or None."""
    for name, data in blocks:
        if name == "SUMO" and "UTMoffset" in data:
            ox, oy = [float(x) for x in data["UTMoffset"].split(",")]
            return ox, oy
    return None


def _csv_list(text):
    return [x.strip() for x in text.split(",") if x.strip()]


def _cdf_to_probs(cdf_values):
    probs = []
    prev = 0.0
    for c in cdf_values:
        c = max(c, prev)
        probs.append(c - prev)
        prev = c

    total = sum(probs)
    if total <= 0:
        return []
    return [p / total for p in probs]


def wui_evacuation_groups(blocks, wui_dir):
    """Parse [EvacuationGroup] blocks.

    Returns list preserving file order:
        [
          {
            'name': 'groupA',
            'shape_file': '/abs/path/to/groupA.shp' or None,
            'dest_probs': {'goalE': 0.4, 'goalR': 0.3, 'goalF': 0.3}
          },
          ...
        ]
    """
    groups = []
    for section, data in blocks:
        if section != "EvacuationGroup":
            continue

        group_name = data.get("Name")

        shape_file = data.get("ShapeFile")
        if shape_file:
            shape_file = str((Path(wui_dir) / shape_file).resolve())

        destinations = _csv_list(data.get("Destinations", ""))
        cdf_text = data.get("DestinationsCDF", "")
        cdf = []
        if cdf_text:
            cdf = [float(x) for x in _csv_list(cdf_text)]

        n = min(len(destinations), len(cdf)) if cdf else len(destinations)
        destinations = destinations[:n]

        if cdf and n > 0:
            probs = _cdf_to_probs(cdf[:n])
        elif n > 0:
            probs = [1.0 / n] * n
        else:
            probs = []

        dest_probs = {d: p for d, p in zip(destinations, probs) if p > 0}
        groups.append(
            {
                "name": group_name,
                "shape_file": shape_file,
                "dest_probs": dest_probs,
            }
        )

    return groups


def _shape_to_polygons(shape):
    """Convert a pyshp polygon shape to list-of-polygons(list-of-rings)."""
    points = shape.points
    parts = list(shape.parts) + [len(points)]

    rings = []
    for i in range(len(parts) - 1):
        ring = points[parts[i] : parts[i + 1]]
        if len(ring) >= 2 and ring[0] == ring[-1]:
            ring = ring[:-1]
        if len(ring) >= 3:
            rings.append(ring)

    # For simple group polygons one shape typically represents one polygon,
    # potentially with holes (additional rings). Keep as a single polygon.
    return [rings] if rings else []


def _point_on_segment(x, y, x1, y1, x2, y2, eps=1e-12):
    cross = (x - x1) * (y2 - y1) - (y - y1) * (x2 - x1)
    if abs(cross) > eps:
        return False
    return (
        min(x1, x2) - eps <= x <= max(x1, x2) + eps
        and min(y1, y2) - eps <= y <= max(y1, y2) + eps
    )


def _point_in_ring(x, y, ring):
    inside = False
    n = len(ring)
    for i in range(n):
        x1, y1 = ring[i]
        x2, y2 = ring[(i + 1) % n]

        if _point_on_segment(x, y, x1, y1, x2, y2):
            return True

        if (y1 > y) != (y2 > y):
            xinters = (x2 - x1) * (y - y1) / (y2 - y1) + x1
            if x < xinters:
                inside = not inside

    return inside


def _point_in_polygon(x, y, polygon):
    """Even-odd rule across polygon rings (supports holes/multi-rings)."""
    inside = False
    for ring in polygon:
        if _point_in_ring(x, y, ring):
            inside = not inside
    return inside


def _load_group_geometries(groups):
    """Load evacuation-group polygons from shapefiles."""
    if not groups:
        return {}

    if shapefile is None:
        raise ImportError(
            "pyshp is required to load EvacuationGroup ShapeFile entries. "
            "Install with: pip install pyshp"
        )

    out = {}
    for g in groups:
        shp = g.get("shape_file")
        if not shp:
            continue

        reader = shapefile.Reader(shp)
        polygons = []
        for s in reader.shapes():
            polygons.extend(_shape_to_polygons(s))
        out[g["name"]] = polygons

    return out


def _household_group(origin_lat, origin_lon, groups, group_geoms):
    """Return the first EvacuationGroup that contains the origin point."""
    # Shapefiles are lon/lat in WGS84.
    x = origin_lon
    y = origin_lat

    for g in groups:
        name = g["name"]
        polygons = group_geoms.get(name, [])
        for poly in polygons:
            if _point_in_polygon(x, y, poly):
                return name
    return None


# ---------------------------------------------------------------------------
# Coordinate transformations
# ---------------------------------------------------------------------------


def latlon_to_sumo(lat, lon, sumo_offset, utm_zone=13, north=True):
    """Project WGS84 lat/lon to SUMO-network-local coordinates.

    SUMO coords = UTM coords + netOffset, where netOffset is usually negative.
    We accept the offset as given in the .wui (which matches net.xml netOffset).
    """
    proj = pyproj.Transformer.from_crs(
        "EPSG:4326",
        f"+proj=utm +zone={utm_zone} +{'north' if north else 'south'} +ellps=WGS84 +datum=WGS84 +units=m +no_defs",
        always_xy=True,
    )
    x, y = proj.transform(lon, lat)
    return x + sumo_offset[0], y + sumo_offset[1]


# ---------------------------------------------------------------------------
# Population CSV loader
# ---------------------------------------------------------------------------


def load_population(csv_path):
    """Read a WUI-NITY population CSV.

    Returns list of dicts with origin_lat, origin_lon, access_lat, access_lon, people.
    Each row represents one household.
    """
    households = []
    with open(csv_path) as f:
        reader = csv.DictReader(f)
        for row in reader:
            households.append(
                {
                    "origin_lat": float(row["OriginLat"]),
                    "origin_lon": float(row["OriginLon"]),
                    "access_lat": float(row["AccessLat"]),
                    "access_lon": float(row["AccessLon"]),
                    "people": int(row["People"]),
                }
            )
    return households


# ---------------------------------------------------------------------------
# Spatial join: snap lat/lon points to nearest SUMO node
# ---------------------------------------------------------------------------


def build_node_index(G):
    """Build a simple spatial index (list of (x, y, node_id)) for nearest-node lookup.

    For ~1000 nodes, brute-force O(N) per query is fine. Swap in scipy KDTree if
    this ever becomes a bottleneck.
    """
    return [(G.nodes[n]["x"], G.nodes[n]["y"], n) for n in G.nodes()]


def nearest_node(x, y, node_index):
    """Return the node ID nearest to (x, y) in SUMO-local coordinates."""
    best = None
    best_d2 = float("inf")
    for nx_, ny_, nid in node_index:
        d2 = (nx_ - x) ** 2 + (ny_ - y) ** 2
        if d2 < best_d2:
            best_d2 = d2
            best = nid
    return best, math.sqrt(best_d2)


# ---------------------------------------------------------------------------
# Top-level convenience: load a scenario and produce weighted origins + exits
# ---------------------------------------------------------------------------


def load_scenario(wui_path, G):
    """Load a full scenario ready for demand assignment.

    Returns:
        {
          'origins':                dict {node_id: vehicles},
          'origin_exit_vehicles':   dict {origin_node: {exit_node: vehicles}},
          'exits':                  list [(name, node_id, dist)],
          'cars_per_household':     float,
          'total_households':       int,    # post-cull, used by simulation
          'total_vehicles':         float,  # post-cull
          'total_households_raw':   int,
          'total_vehicles_raw':     float,
          'group_counts':           dict {group_name: households},
          'culled_households':      int,
        }
    """
    wui_path = Path(wui_path)
    blocks = parse_wui(wui_path)

    # Exits
    dests = wui_destinations(blocks)
    # Demographics
    cars_per_hh = wui_demographics(blocks)
    # SUMO netOffset
    offset = wui_sumo_offset(blocks)
    if offset is None:
        raise ValueError("No [SUMO] UTMoffset in the .wui file")

    # Population settings, resolved relative to the .wui
    pop_rel = None
    cull_outside_groups = False
    for name, data in blocks:
        if name == "Population":
            pop_rel = data["PopulationFile"]
            cull_outside_groups = (
                data.get("CullOutsideGroups", "false").lower() == "true"
            )
            break
    if pop_rel is None:
        raise ValueError("No [Population] PopulationFile in .wui")
    pop_path = (wui_path.parent / pop_rel).resolve()

    # Evacuation groups (optional but critical for scenario-aligned static demand)
    evac_groups = wui_evacuation_groups(blocks, wui_path.parent)
    group_geoms = _load_group_geometries(evac_groups) if evac_groups else {}

    households = load_population(pop_path)

    # Spatial index
    node_index = build_node_index(G)

    # Snap exits
    exits = []
    for name, lat, lon in dests:
        x, y = latlon_to_sumo(lat, lon, offset)
        node_id, dist = nearest_node(x, y, node_index)
        exits.append((name, node_id, dist))

    exit_node_by_name = {name: node_id for name, node_id, _ in exits}

    group_cfg_by_name = {g["name"]: g for g in evac_groups}

    # Snap each household access point to nearest node, aggregate vehicles and
    # build optional origin->exit split from EvacGroup destination priors.
    origins = {}
    origin_exit_vehicles = {}
    group_counts = defaultdict(int)
    culled_households = 0

    for hh in households:
        group_name = (
            _household_group(
                hh["origin_lat"],
                hh["origin_lon"],
                evac_groups,
                group_geoms,
            )
            if evac_groups
            else None
        )

        if group_name is None and cull_outside_groups:
            culled_households += 1
            continue

        x, y = latlon_to_sumo(hh["access_lat"], hh["access_lon"], offset)
        node_id, _ = nearest_node(x, y, node_index)
        origins[node_id] = origins.get(node_id, 0.0) + cars_per_hh

        if group_name is not None:
            group_counts[group_name] += 1
            group_cfg = group_cfg_by_name.get(group_name, {})
            dest_probs = group_cfg.get("dest_probs", {})

            if dest_probs:
                split = origin_exit_vehicles.setdefault(node_id, {})
                for dest_name, p in dest_probs.items():
                    exit_node = exit_node_by_name.get(dest_name)
                    if exit_node is None:
                        continue
                    split[exit_node] = split.get(exit_node, 0.0) + cars_per_hh * p
        else:
            group_counts["_ungrouped"] += 1

    total_households_raw = len(households)
    total_vehicles_raw = total_households_raw * cars_per_hh
    total_households = total_households_raw - culled_households
    total_vehicles = total_households * cars_per_hh

    return {
        "origins": origins,
        "origin_exit_vehicles": origin_exit_vehicles,
        "exits": exits,
        "cars_per_household": cars_per_hh,
        "total_households": total_households,
        "total_vehicles": total_vehicles,
        "total_households_raw": total_households_raw,
        "total_vehicles_raw": total_vehicles_raw,
        "group_counts": dict(group_counts),
        "culled_households": culled_households,
        "cull_outside_groups": cull_outside_groups,
        "sumo_offset": offset,
    }
