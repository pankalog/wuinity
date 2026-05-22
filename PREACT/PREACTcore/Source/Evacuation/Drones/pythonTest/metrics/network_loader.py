"""SUMO network loading and NetworkX graph construction.

Builds two graph representations from a SUMO .net.xml:
- DiGraph: for static demand assignment (respects edge direction)
- Graph:   for topology/redundancy metrics (drones fly above roads, direction-agnostic)
"""

import sumolib
import networkx as nx


def load_sumo_network(net_file):
    """Load a SUMO .net.xml file via sumolib."""
    return sumolib.net.readNet(net_file)


def _edge_attrs(sumo_edge):
    """Extract the static attributes we care about from a SUMO edge."""
    return {
        "length": sumo_edge.getLength(),
        "speed": sumo_edge.getSpeed(),        # m/s (free-flow speed v_f)
        "num_lanes": sumo_edge.getLaneNumber(),
        "edge_type": sumo_edge.getType(),
        "sumo_id": sumo_edge.getID(),
        "travel_time": sumo_edge.getLength() / max(sumo_edge.getSpeed(), 0.1),
    }


def build_graphs(sumo_net):
    """Build directed and undirected NetworkX graphs from a SUMO network.

    Returns:
        DG: directed graph, one edge per SUMO edge (demand assignment)
        UG: undirected graph, opposite-direction pairs merged (topology)
    """
    DG = nx.DiGraph()
    UG = nx.Graph()

    for node in sumo_net.getNodes():
        x, y = node.getCoord()
        DG.add_node(node.getID(), x=x, y=y)
        UG.add_node(node.getID(), x=x, y=y)

    for edge in sumo_net.getEdges():
        u = edge.getFromNode().getID()
        v = edge.getToNode().getID()
        attrs = _edge_attrs(edge)

        DG.add_edge(u, v, **attrs)

        if UG.has_edge(u, v):
            # Opposite-direction pair already added; keep min travel time,
            # sum lanes (total cross-section), append sumo_id.
            d = UG[u][v]
            d["num_lanes"] = d["num_lanes"] + attrs["num_lanes"]
            d["sumo_ids"].append(attrs["sumo_id"])
            d["travel_time"] = min(d["travel_time"], attrs["travel_time"])
        else:
            d = dict(attrs)
            d["sumo_ids"] = [attrs.pop("sumo_id")]
            d.pop("sumo_id", None)
            UG.add_edge(u, v, **d)

    return DG, UG
