
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;

namespace PREACT.Traffic
{
    public class SumoEdge
    {
        public string Id;
        public string From;
        public string To;
        public List<(double x, double y)> Shape = new List<(double, double)>();
        public List<string> Lanes = new List<string>();
    }

    public class SumoLane
    {
        public string Id;
        public string EdgeId;
        public List<(double x, double y)> Shape = new List<(double, double)>();
    }

    // Represents a SUMO junction (intersection) parsed from the .net.xml <junction> element.
    // Position is in the same coordinate space as edge shapes (i.e. UTM-offset network coords).
    // Type values commonly seen: priority, traffic_light, dead_end, internal, allway_stop, etc.
    public class SumoJunction
    {
        public string Id;
        public string Type;
        public double X;
        public double Y;
        public List<string> IncomingLanes = new List<string>();
        public List<string> InternalLanes = new List<string>();
    }

    // Represents a SUMO <connection> element: a single allowed (fromLane -> toLane) link
    // across a junction. The Via field is the internal lane id (":junction_index_...") that
    // physically traverses the junction interior, if any; for direct junctions it's empty.
    // FromLane / ToLane are lane indices within the edge (-1 if not specified).
    public class SumoConnection
    {
        public string From;
        public string To;
        public int FromLane = -1;
        public int ToLane = -1;
        public string Via;
        public string Direction;
        public string State;
    }

    public class SumoConfig
    {
        private SumoNetwork _network;

        public SumoNetwork Network { get => _network; }

        public SumoConfig(string filePath, bool readNetwork)
        {

            XmlDocument doc = new XmlDocument();
            doc.Load(filePath);

            XmlElement root = doc.DocumentElement;
            if (root == null)
            {
                throw new Exception("Invalid .sumocg file");
            }


            // The net-file entry is inside <input> … <net-file value="..."/>
            XmlNode inputNode = root.SelectSingleNode("//input");
            if (inputNode == null)
            {
                throw new Exception("No <input> element found in .sumocfg");
            }

            XmlNode netFileNode = inputNode.SelectSingleNode("net-file");
            if (netFileNode == null)
            {
                throw new Exception("No <net-file> element found in .sumocfg");
            }

            XmlAttribute valueAttr = netFileNode.Attributes["value"];
            if (valueAttr == null)
            {
                throw new Exception("<net-file> exists but has no 'value' attribute");
            }

            string netEditFilePath = Path.Combine(Path.GetDirectoryName(filePath), valueAttr.Value);
            _network = new SumoNetwork(netEditFilePath, readNetwork);
        }
    }  

    public class SumoNetwork
    {
        public Math.Vector2d UTMOffset;
        public Dictionary<string, SumoEdge> Edges = new Dictionary<string, SumoEdge>();
        public Dictionary<string, SumoLane> Lanes = new Dictionary<string, SumoLane>();

        // Junctions and connections are parsed additively from the same .net.xml. They are
        // populated only when readNetwork=true. Consumers that don't need topology metadata
        // can ignore them — their presence is non-breaking for existing code paths.
        public Dictionary<string, SumoJunction> Junctions = new Dictionary<string, SumoJunction>();
        public List<SumoConnection> Connections = new List<SumoConnection>();

        // Fast forward-adjacency lookup keyed by edge id: edge A maps to the list of edge ids
        // reachable via any <connection from="A" to="..."/>. De-duplicated. Built once at
        // parse time so per-decision lookups in routing policies are O(1).
        public Dictionary<string, List<string>> EdgeOutgoing = new Dictionary<string, List<string>>();


        public SumoNetwork(string filePath, bool readNetwork)
        {
            XmlDocument doc;

            bool isGzip = filePath.ToLower().EndsWith("gz");
            if (isGzip)
            {
                //string xmlPath = filePath.Remove(filePath.Length - 3, 3);
                using var input = File.OpenRead(filePath);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                doc = new XmlDocument();
                doc.Load(gzip);
            }
            else
            {
                doc = new XmlDocument();
                doc.Load(filePath);
            }            

            XmlElement root = doc.DocumentElement;
            if (root == null)
            {
                throw new Exception("Invalid net.xml file");
            }

            //save UTM offset
            XmlNode location = root.SelectSingleNode("//location");
            if (location == null)
            {
                throw new Exception("Could not find location in file.");
            }
            XmlAttribute netOffset = location.Attributes["netOffset"];
            if(netOffset != null)
            {
                string[] offset = netOffset.Value.Split(',');
                double.TryParse(offset[0], out double offsetX);
                double.TryParse(offset[1], out double offsetY);
                UTMOffset = new Math.Vector2d(offsetX, offsetY);  
            }

            if(!readNetwork)
            {
                return;
            }

            //do edges
            XmlNodeList edgeNodes = root.SelectNodes("//edge");
            foreach (XmlNode edgeNode in edgeNodes)
            {
                XmlAttribute idAttr = edgeNode.Attributes["id"];
                if (idAttr == null)
                {
                    continue;
                }

                string id = idAttr.Value;

                // Skip internal edges
                if (id.StartsWith(":"))
                {
                    continue;
                }

                SumoEdge edge = new SumoEdge();
                edge.Id = id;

                XmlAttribute fromAttr = edgeNode.Attributes["from"];
                XmlAttribute toAttr = edgeNode.Attributes["to"];

                edge.From = fromAttr != null ? fromAttr.Value : null;
                edge.To = toAttr != null ? toAttr.Value : null;

                XmlAttribute shapeAttr = edgeNode.Attributes["shape"];
                if (shapeAttr != null)
                {
                    edge.Shape = ParseShape(shapeAttr.Value);
                }

                Edges[id] = edge;

                // Parse lane children
                XmlNodeList laneNodes = edgeNode.SelectNodes("lane");
                foreach (XmlNode laneNode in laneNodes)
                {
                    XmlAttribute laneIdAttr = laneNode.Attributes["id"];
                    if (laneIdAttr == null)
                    {
                        continue;
                    }

                    string laneId = laneIdAttr.Value;

                    SumoLane lane = new SumoLane();
                    lane.Id = laneId;
                    lane.EdgeId = id;

                    XmlAttribute laneShapeAttr = laneNode.Attributes["shape"];
                    if (laneShapeAttr != null)
                    {
                        lane.Shape = ParseShape(laneShapeAttr.Value);
                    }

                    Lanes[laneId] = lane;
                    edge.Lanes.Add(laneId);
                }

                // SUMO writes <edge shape="..."> only when the geometry deviates from a
                // straight line between junction centres. Most straight edges have no edge-
                // level shape attribute — their geometry lives entirely on the <lane>
                // children. Without this fallback, ~18% of edges in a typical OSM-imported
                // net end up with an empty Shape and get dropped by downstream consumers,
                // shattering the connectivity graph into spurious components.
                if (edge.Shape.Count < 2 && edge.Lanes.Count > 0)
                {
                    SumoLane firstLane = Lanes[edge.Lanes[0]];
                    if (firstLane.Shape != null && firstLane.Shape.Count >= 2)
                    {
                        edge.Shape = new List<(double x, double y)>(firstLane.Shape);
                    }
                }
            }

            //do junctions (additive; non-breaking — existing consumers don't touch Junctions)
            XmlNodeList junctionNodes = root.SelectNodes("//junction");
            foreach (XmlNode junctionNode in junctionNodes)
            {
                XmlAttribute idAttr = junctionNode.Attributes["id"];
                if (idAttr == null) continue;

                SumoJunction j = new SumoJunction
                {
                    Id = idAttr.Value,
                    Type = junctionNode.Attributes["type"]?.Value,
                };

                XmlAttribute xAttr = junctionNode.Attributes["x"];
                XmlAttribute yAttr = junctionNode.Attributes["y"];
                if (xAttr != null) double.TryParse(xAttr.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out j.X);
                if (yAttr != null) double.TryParse(yAttr.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out j.Y);

                XmlAttribute incLanesAttr = junctionNode.Attributes["incLanes"];
                if (incLanesAttr != null && !string.IsNullOrWhiteSpace(incLanesAttr.Value))
                {
                    foreach (string laneId in incLanesAttr.Value.Split(' '))
                    {
                        if (!string.IsNullOrWhiteSpace(laneId)) j.IncomingLanes.Add(laneId);
                    }
                }

                XmlAttribute intLanesAttr = junctionNode.Attributes["intLanes"];
                if (intLanesAttr != null && !string.IsNullOrWhiteSpace(intLanesAttr.Value))
                {
                    foreach (string laneId in intLanesAttr.Value.Split(' '))
                    {
                        if (!string.IsNullOrWhiteSpace(laneId)) j.InternalLanes.Add(laneId);
                    }
                }

                Junctions[j.Id] = j;
            }

            //do connections (additive). Skipped if neither from nor to is set, or if either
            //refers to an internal edge — we want road-to-road links, not lane-internal hops.
            XmlNodeList connectionNodes = root.SelectNodes("//connection");
            foreach (XmlNode connNode in connectionNodes)
            {
                XmlAttribute fromAttr = connNode.Attributes["from"];
                XmlAttribute toAttr = connNode.Attributes["to"];
                if (fromAttr == null || toAttr == null) continue;
                string from = fromAttr.Value;
                string to = toAttr.Value;
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) continue;
                if (from.StartsWith(":") || to.StartsWith(":")) continue;

                SumoConnection c = new SumoConnection
                {
                    From = from,
                    To = to,
                    Via = connNode.Attributes["via"]?.Value,
                    Direction = connNode.Attributes["dir"]?.Value,
                    State = connNode.Attributes["state"]?.Value,
                };
                XmlAttribute fromLaneAttr = connNode.Attributes["fromLane"];
                XmlAttribute toLaneAttr = connNode.Attributes["toLane"];
                if (fromLaneAttr != null) int.TryParse(fromLaneAttr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out c.FromLane);
                if (toLaneAttr != null) int.TryParse(toLaneAttr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out c.ToLane);
                Connections.Add(c);

                if (!EdgeOutgoing.TryGetValue(from, out List<string> outs))
                {
                    outs = new List<string>();
                    EdgeOutgoing[from] = outs;
                }
                // Deduplicate: many connection rows can share the same (from, to) pair across lanes.
                if (!outs.Contains(to))
                {
                    outs.Add(to);
                }
            }
        }

        private static List<(double x, double y)> ParseShape(string shape)
        {
            List<(double x, double y)> list = new List<(double, double)>();

            if (string.IsNullOrEmpty(shape))
            {
                return list;
            }

            string[] pairs = shape.Split(' ');
            foreach (string p in pairs)
            {
                string[] xy = p.Split(',');
                if (xy.Length == 2)
                {
                    double x = double.Parse(xy[0], CultureInfo.InvariantCulture);
                    double y = double.Parse(xy[1], CultureInfo.InvariantCulture);
                    list.Add((x, y));
                }
            }

            return list;
        }
    }
}
