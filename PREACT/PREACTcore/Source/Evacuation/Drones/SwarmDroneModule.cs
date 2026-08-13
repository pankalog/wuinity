using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PREACT.Input;
using PREACT.Math;
using PREACT.Traffic;

namespace PREACT.Evacuation
{
    public class SwarmDroneModule : DroneModule
    {
        private const double ArrivalToleranceMeters = 0.5;

        private readonly DroneModuleInput _moduleInput;
        private readonly DroneRoadGraphRuntime _graph = new DroneRoadGraphRuntime();
        private readonly List<(Vector2d start, Vector2d end)> _roadSegments = new List<(Vector2d start, Vector2d end)>();
        private readonly List<DroneAgentRuntime> _drones = new List<DroneAgentRuntime>();
        private IDroneRoutingPolicy _policy;

        private double _diagnosticsTimer;
        private readonly double _diagnosticsIntervalSeconds;
        private readonly Vector2d _chargingStation;
        private volatile string _latestStatusText;
        private volatile Vector2d[] _dronePositionsSnapshot = Array.Empty<Vector2d>();
        private volatile DroneAgentState[] _droneStatesSnapshot = Array.Empty<DroneAgentState>();
        private volatile bool[] _rasterActiveGridMask = Array.Empty<bool>();
        private Vector2d _rasterGridMin;
        private Vector2d _rasterGridMax;
        private int _rasterGridRows;
        private int _rasterGridColumns;
        private int _rasterGridTotalCells;
        private StreamWriter _droneStateTelemetryWriter;
        private StreamWriter _scanTelemetryWriter;
        private StreamWriter _acoEdgeTelemetryWriter;
        private double _nextTelemetrySampleTime;
        private string _telemetryPrefix;
        private readonly List<AcoStigmergicRoutingPolicy.EdgeTelemetrySnapshot> _acoEdgeSnapshotBuffer = new List<AcoStigmergicRoutingPolicy.EdgeTelemetrySnapshot>();

        // Per-edge mean per-lane density d_bar_e from the reference SUMO run; parallel to _graph.Edges.
        // Network-mean d_ref = arithmetic mean of d_bar_e over edges in _dBarPerEdge that had data.
        // Both are 0 / NaN if no GammaSourceFile was loaded.
        private double[] _dBarPerEdge;
        private double _dRef;
        private double _gammaSourceTRef;

        // Spawn position resolved by BuildGraphFromSumo to lie on the LARGEST connected
        // component, regardless of where _chargingStation sits. Without this, drones snap
        // to whatever edge is geographically nearest to the charger — frequently a small
        // disconnected island where they get trapped because no graph path leaves it.
        // _chargingStation is still used for charging-return logic; the dispatch entry
        // only affects the initial drone Position.
        private Vector2d _dispatchEntryPosition;
        private bool _dispatchEntryResolved;

        // Indices into _graph.Edges that belong to the largest kept component. Used by
        // RandomSpawnAcrossNetwork so drones can't be placed on disconnected islands.
        private int[] _keeperEdgeIndices = System.Array.Empty<int>();

        public SwarmDroneModule(Simulation simulation, out bool success) : base(simulation)
        {
            _moduleInput = _simulation?.Input?.DroneModule ?? new DroneModuleInput();
            success = true;
            _diagnosticsIntervalSeconds = _moduleInput.Policy.DiagnosticsIntervalSeconds;
            _diagnosticsTimer = 0.0;
            _chargingStation = ResolveChargingStation(_moduleInput.Fleet, _simulation.Input.Simulation.DomainSize);

            BuildGraphFromSumo();

            BuildDroneAgents();
            BuildPolicy();
            ApplyEdgeImportanceWeights();
            UpdateDroneSnapshots();
            InitializeTelemetryOutput();

            _latestStatusText = $"Drone module active\nPolicy: {_policy.Name}\nDrones: {_drones.Count}\nCharging station: ({_chargingStation.x:0.0}, {_chargingStation.y:0.0})\nDiagnostics: every {_diagnosticsIntervalSeconds:0.0}s";
            Engine.Message(_simulation, Engine.LogType.Log, $"[DroneScaffold] Built graph edges={_graph.EdgeCount}, drones={_drones.Count}, policy={_policy.Name}.");

            TryGetRasterGrid(out _, out _, out _, out _);
            TryGetRasterActiveMask(out _);
        }

        public override bool IsSimulationDone()
        {
            return false;
        }

        public override void Step(double simulationTime, double deltaTime)
        {
            _policy.OnSimulationStep(simulationTime, deltaTime);
            UpdateBatteryDynamics(deltaTime);
            AccumulateScanMeasurements(deltaTime);
            AdvanceDroneStates(deltaTime);
            AssignTargetsIfNeeded(simulationTime);
            UpdateDroneSnapshots();
            RecordTelemetry(simulationTime);
            UpdateDiagnostics(deltaTime, simulationTime);
        }

        public override void Stop()
        {
            DisposeTelemetryOutput();
            Engine.Message(_simulation, Engine.LogType.Log, "[DroneScaffold] Stopping drone module.");
        }

        public bool TryGetStatusText(out string statusText)
        {
            statusText = _latestStatusText;
            return !string.IsNullOrWhiteSpace(statusText);
        }

        public bool TryGetDronePositions(out Vector2d[] dronePositions)
        {
            dronePositions = _dronePositionsSnapshot;
            return dronePositions != null && dronePositions.Length > 0;
        }

        public bool TryGetDroneStates(out DroneAgentState[] droneStates)
        {
            droneStates = _droneStatesSnapshot;
            return droneStates != null && droneStates.Length > 0;
        }

        public bool TryGetRasterGrid(out Vector2d min, out Vector2d max, out int rows, out int columns)
        {
            if (_policy != null && _policy.TryGetRasterGrid(out min, out max, out rows, out columns))
            {
                _rasterGridMin = min;
                _rasterGridMax = max;
                _rasterGridRows = rows;
                _rasterGridColumns = columns;
                _rasterGridTotalCells = rows * columns;
                return true;
            }

            min = Vector2d.zero;
            max = Vector2d.zero;
            rows = 0;
            columns = 0;
            return false;
        }

        public bool TryGetRasterActiveMask(out bool[] activeMask)
        {
            if (_policy != null && _policy.TryGetRasterActiveMask(out activeMask))
            {
                _rasterActiveGridMask = activeMask;
                return true;
            }

            activeMask = Array.Empty<bool>();
            return false;
        }

        private void BuildGraphFromSumo()
        {
            try
            {
                _graph.Edges.Clear();
                _graph.NodeToEdgeIndices.Clear();
                _roadSegments.Clear();

                string configFile = Path.Combine(_simulation.Engine.WorkingFolder, _simulation.Input.TrafficModule.SumoInput.ConfigurationFile);
                SumoConfig sumoConfig = new SumoConfig(configFile, true);

                Vector2d sumoUTM = -sumoConfig.Network.UTMOffset;
                _originOffset = sumoUTM - _simulation.Spatial.UTMOrigin;

                // First pass: collect every candidate edge, but don't insert into the graph yet.
                // We need to keep road segments for the raster overlay regardless of connectivity,
                // but ACO routing should only see the largest connected component (junction adjacency)
                // so drones can't end up on isolated islands they can never leave.
                List<DroneEdgeRuntime> candidateEdges = new List<DroneEdgeRuntime>(sumoConfig.Network.Edges.Count);
                foreach (SumoEdge edge in sumoConfig.Network.Edges.Values)
                {
                    if (edge.Shape == null || edge.Shape.Count < 2)
                    {
                        continue;
                    }

                    for (int i = 0; i < edge.Shape.Count - 1; ++i)
                    {
                        Vector2d segmentStart = ToSimulationSpace(edge.Shape[i]);
                        Vector2d segmentEnd = ToSimulationSpace(edge.Shape[i + 1]);
                        if (Vector2d.Distance(segmentStart, segmentEnd) > 0.01)
                        {
                            _roadSegments.Add((segmentStart, segmentEnd));
                        }
                    }

                    Vector2d start = ToSimulationSpace(edge.Shape[0]);
                    Vector2d end = ToSimulationSpace(edge.Shape[edge.Shape.Count - 1]);
                    double length = ComputePolylineLengthInSimulation(edge.Shape);
                    if (length <= 0.0)
                    {
                        continue;
                    }

                    int laneCount = edge.Lanes != null ? edge.Lanes.Count : 1;
                    candidateEdges.Add(new DroneEdgeRuntime(edge.Id, edge.From, edge.To, start, end, length, laneCount));
                }

                // Second pass: union-find over junction IDs to identify connected components.
                // Treats edges as undirected — fine for ACO routing which only needs node adjacency.
                Dictionary<string, string> parent = new Dictionary<string, string>();
                string Find(string n)
                {
                    if (!parent.TryGetValue(n, out string p))
                    {
                        parent[n] = n;
                        return n;
                    }
                    if (p == n)
                    {
                        return n;
                    }
                    string root = Find(p);
                    parent[n] = root;
                    return root;
                }
                void Union(string a, string b)
                {
                    string ra = Find(a);
                    string rb = Find(b);
                    if (ra != rb)
                    {
                        parent[ra] = rb;
                    }
                }

                // ---- Optional: merge junctions that are within EdgeMergeToleranceMeters ----
                // OSM-imported SUMO networks frequently have two distinct junction IDs at
                // the same physical intersection (cleanup artefacts at complex
                // intersections, network-clip boundaries). Without merging, each becomes a
                // separate node in the adjacency map, splitting one real intersection into
                // multiple components. We collect representative positions for each node id
                // (using whichever edge endpoint we see first) and union any two ids whose
                // positions are within tolerance.
                double mergeTolerance = System.Math.Max(0.0, _moduleInput.Policy.EdgeMergeToleranceMeters);
                Dictionary<string, string> canonicalIdByNode = new Dictionary<string, string>();
                int nodeMergePairs = 0;
                if (mergeTolerance > 0.0)
                {
                    Dictionary<string, Vector2d> nodePosition = new Dictionary<string, Vector2d>();
                    foreach (DroneEdgeRuntime ce in candidateEdges)
                    {
                        if (!string.IsNullOrWhiteSpace(ce.FromNode) && !nodePosition.ContainsKey(ce.FromNode))
                        {
                            nodePosition[ce.FromNode] = ce.Start;
                        }
                        if (!string.IsNullOrWhiteSpace(ce.ToNode) && !nodePosition.ContainsKey(ce.ToNode))
                        {
                            nodePosition[ce.ToNode] = ce.End;
                        }
                    }

                    // Spatial bucketing by tolerance-sized cells: any nodes within the
                    // tolerance must share a cell with one of the (3 x 3) neighbour cells.
                    // Keeps the comparison O(n) on typical road networks rather than O(n^2).
                    double cell = mergeTolerance;
                    double tolSq = mergeTolerance * mergeTolerance;
                    Dictionary<long, List<string>> bucket = new Dictionary<long, List<string>>();
                    long CellKey(double x, double y)
                    {
                        long cx = (long)System.Math.Floor(x / cell);
                        long cy = (long)System.Math.Floor(y / cell);
                        return (cx * 73856093L) ^ (cy * 19349663L);
                    }
                    foreach (KeyValuePair<string, Vector2d> kv in nodePosition)
                    {
                        long key = CellKey(kv.Value.x, kv.Value.y);
                        if (!bucket.TryGetValue(key, out List<string> list))
                        {
                            list = new List<string>();
                            bucket[key] = list;
                        }
                        list.Add(kv.Key);
                    }

                    foreach (KeyValuePair<string, Vector2d> kv in nodePosition)
                    {
                        long cx0 = (long)System.Math.Floor(kv.Value.x / cell);
                        long cy0 = (long)System.Math.Floor(kv.Value.y / cell);
                        for (long dx = -1; dx <= 1; ++dx)
                        {
                            for (long dy = -1; dy <= 1; ++dy)
                            {
                                long key = ((cx0 + dx) * 73856093L) ^ ((cy0 + dy) * 19349663L);
                                if (!bucket.TryGetValue(key, out List<string> list)) continue;
                                foreach (string other in list)
                                {
                                    if (string.CompareOrdinal(other, kv.Key) <= 0) continue;  // process each pair once
                                    Vector2d po = nodePosition[other];
                                    double ddx = po.x - kv.Value.x;
                                    double ddy = po.y - kv.Value.y;
                                    if (ddx * ddx + ddy * ddy <= tolSq)
                                    {
                                        // Build a separate union-find solely over node-id partition.
                                        // We piggyback on the same Find/Union helpers used for the
                                        // edge-component pass; the parent map starts fresh per node id
                                        // and the canonical id below is the resolved root.
                                        Union(kv.Key, other);
                                        nodeMergePairs++;
                                    }
                                }
                            }
                        }
                    }

                    // Snapshot the canonical id (Find result) for every node so subsequent
                    // edge insertions use the merged identity.
                    foreach (string id in nodePosition.Keys)
                    {
                        canonicalIdByNode[id] = Find(id);
                    }
                    // Reset parent map so the *edge*-component pass below starts from a clean state.
                    parent.Clear();
                }

                string Canon(string id)
                {
                    if (string.IsNullOrWhiteSpace(id)) return id;
                    return canonicalIdByNode.TryGetValue(id, out string c) ? c : id;
                }

                // ---- Connected-component analysis over (canonical) node ids ----
                // Fold three signals into one union-find:
                //   1. Each candidate edge's own (from, to) endpoints — guarantees an edge's
                //      two endpoints share a component.
                //   2. SUMO <connection> data via EdgeOutgoing: for every (A,B) link, the
                //      four endpoint nodes (A.from, A.to, B.from, B.to) physically meet at
                //      the same junction even if SUMO labels them with different IDs. This
                //      is the ground truth — it captures turn restrictions, internal-lane
                //      hops, and edge-pairs that share no node id literally but are
                //      reachable across a junction.
                //   3. The optional EdgeMergeToleranceMeters merge applied above (already
                //      baked into the canonical id via Canon()).
                foreach (DroneEdgeRuntime e in candidateEdges)
                {
                    string fn = Canon(e.FromNode);
                    string tn = Canon(e.ToNode);
                    if (string.IsNullOrWhiteSpace(fn) || string.IsNullOrWhiteSpace(tn)) continue;
                    Union(fn, tn);
                }

                int connectionPairsUsed = 0;
                Dictionary<string, DroneEdgeRuntime> edgeById = new Dictionary<string, DroneEdgeRuntime>(candidateEdges.Count);
                foreach (DroneEdgeRuntime e in candidateEdges)
                {
                    if (!string.IsNullOrEmpty(e.EdgeId)) edgeById[e.EdgeId] = e;
                }
                if (sumoConfig.Network.EdgeOutgoing != null)
                {
                    foreach (KeyValuePair<string, List<string>> kv in sumoConfig.Network.EdgeOutgoing)
                    {
                        if (!edgeById.TryGetValue(kv.Key, out DroneEdgeRuntime from)) continue;
                        string fromFrom = Canon(from.FromNode);
                        string fromTo = Canon(from.ToNode);
                        foreach (string toId in kv.Value)
                        {
                            if (!edgeById.TryGetValue(toId, out DroneEdgeRuntime to)) continue;
                            string toFrom = Canon(to.FromNode);
                            string toTo = Canon(to.ToNode);
                            // Union all four endpoint ids — they all meet at the shared junction.
                            // Filtering empties at union-time, since Find on a missing key just adds it.
                            if (!string.IsNullOrWhiteSpace(fromTo) && !string.IsNullOrWhiteSpace(toFrom))
                            {
                                Union(fromTo, toFrom);
                            }
                            if (!string.IsNullOrWhiteSpace(fromFrom) && !string.IsNullOrWhiteSpace(toTo))
                            {
                                Union(fromFrom, toTo);
                            }
                            connectionPairsUsed++;
                        }
                    }
                }

                // Tally component sizes by edge count, pick the largest as the keeper.
                // Also collect bounding-box centroid + size class for diagnostics.
                Dictionary<string, int> componentEdgeCount = new Dictionary<string, int>();
                Dictionary<string, (double minX, double minY, double maxX, double maxY)> componentBbox =
                    new Dictionary<string, (double, double, double, double)>();
                foreach (DroneEdgeRuntime e in candidateEdges)
                {
                    string fn = Canon(e.FromNode);
                    if (string.IsNullOrWhiteSpace(fn)) continue;
                    string root = Find(fn);
                    componentEdgeCount.TryGetValue(root, out int count);
                    componentEdgeCount[root] = count + 1;

                    if (!componentBbox.TryGetValue(root, out var bb))
                    {
                        bb = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
                    }
                    bb.minX = System.Math.Min(bb.minX, System.Math.Min(e.Start.x, e.End.x));
                    bb.minY = System.Math.Min(bb.minY, System.Math.Min(e.Start.y, e.End.y));
                    bb.maxX = System.Math.Max(bb.maxX, System.Math.Max(e.Start.x, e.End.x));
                    bb.maxY = System.Math.Max(bb.maxY, System.Math.Max(e.Start.y, e.End.y));
                    componentBbox[root] = bb;
                }

                // Identify which components survive the MinComponentEdges filter.
                //   MinComponentEdges <= 0  => legacy behaviour: keep ONLY the largest component.
                //   MinComponentEdges == 1  => keep every component (no filtering).
                //   MinComponentEdges  > 1  => drop noise stubs smaller than the threshold.
                int minComponent = _moduleInput.Policy.MinComponentEdges;
                string keeperRoot = null;
                int keeperSize = 0;
                foreach (KeyValuePair<string, int> kv in componentEdgeCount)
                {
                    if (kv.Value > keeperSize)
                    {
                        keeperSize = kv.Value;
                        keeperRoot = kv.Key;
                    }
                }

                HashSet<string> keepRoots = new HashSet<string>();
                string filterDescription;
                if (minComponent <= 0)
                {
                    if (keeperRoot != null) keepRoots.Add(keeperRoot);
                    filterDescription = "largest-only (legacy)";
                }
                else
                {
                    foreach (KeyValuePair<string, int> kv in componentEdgeCount)
                    {
                        if (kv.Value >= minComponent) keepRoots.Add(kv.Key);
                    }
                    filterDescription = minComponent == 1 ? "all components" : $">= {minComponent} edges";
                }

                // ---- Add edges in surviving components, using canonical node ids ----
                int kept = 0;
                int dropped = 0;
                foreach (DroneEdgeRuntime e in candidateEdges)
                {
                    string fn = Canon(e.FromNode);
                    string tn = Canon(e.ToNode);
                    bool survives = !string.IsNullOrWhiteSpace(fn)
                        && !string.IsNullOrWhiteSpace(tn)
                        && keepRoots.Contains(Find(fn));
                    if (survives)
                    {
                        if (mergeTolerance > 0.0 && (fn != e.FromNode || tn != e.ToNode))
                        {
                            // Rewrite endpoint ids on the runtime edge so NodeToEdgeIndices
                            // adjacency keys on the merged id at lookup time.
                            e.FromNode = fn;
                            e.ToNode = tn;
                        }
                        _graph.AddEdge(e);
                        kept++;
                    }
                    else
                    {
                        dropped++;
                    }
                }

                Engine.Message(_simulation, Engine.LogType.Log,
                    string.Format(CultureInfo.InvariantCulture,
                        "[DroneScaffold] SUMO graph connectivity: candidates={0}, mergeTol={1:0.##}m, mergedPairs={2}, sumoConns={3}, components={4}, filter={5}, kept={6}, dropped={7}.",
                        candidateEdges.Count, mergeTolerance, nodeMergePairs, connectionPairsUsed, componentEdgeCount.Count, filterDescription, kept, dropped));

                // Per-component breakdown so the user can see whether dropped components are
                // tiny artefacts (1-3 edges) or genuine sub-networks worth recovering.
                int diagLimit = 12;
                int shown = 0;
                List<KeyValuePair<string, int>> sorted = new List<KeyValuePair<string, int>>(componentEdgeCount);
                sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
                foreach (KeyValuePair<string, int> kv in sorted)
                {
                    if (shown++ >= diagLimit) break;
                    var bb = componentBbox[kv.Key];
                    double cx = 0.5 * (bb.minX + bb.maxX);
                    double cy = 0.5 * (bb.minY + bb.maxY);
                    double extent = System.Math.Max(bb.maxX - bb.minX, bb.maxY - bb.minY);
                    string tag = keepRoots.Contains(kv.Key) ? "kept" : "drop";
                    Engine.Message(_simulation, Engine.LogType.Log,
                        string.Format(CultureInfo.InvariantCulture,
                            "[DroneScaffold]   [{0}] component edges={1,5}, centroid=({2:0.#}, {3:0.#}), extent={4:0.#}m",
                            tag, kv.Value, cx, cy, extent));
                }
                if (componentEdgeCount.Count > diagLimit)
                {
                    Engine.Message(_simulation, Engine.LogType.Log,
                        $"[DroneScaffold]   ... ({componentEdgeCount.Count - diagLimit} more components not listed)");
                }

                // Resolve the dispatch entry point: the position on the LARGEST kept
                // component that's geographically closest to _chargingStation. Without
                // this, drones get snapped to whichever edge endpoint is globally
                // nearest — frequently a small island where they can't escape.
                _dispatchEntryResolved = false;
                _keeperEdgeIndices = System.Array.Empty<int>();
                if (keeperRoot != null && _graph.EdgeCount > 0)
                {
                    double bestDist = double.MaxValue;
                    Vector2d bestPos = _chargingStation;
                    List<int> keeperIdx = new List<int>(_graph.Edges.Count);
                    for (int i = 0; i < _graph.Edges.Count; ++i)
                    {
                        DroneEdgeRuntime ge = _graph.Edges[i];
                        string fn = Canon(ge.FromNode);
                        if (string.IsNullOrWhiteSpace(fn)) continue;
                        if (Find(fn) != keeperRoot) continue;
                        keeperIdx.Add(i);
                        double dStart = Vector2d.Distance(_chargingStation, ge.Start);
                        double dEnd = Vector2d.Distance(_chargingStation, ge.End);
                        if (dStart < bestDist) { bestDist = dStart; bestPos = ge.Start; }
                        if (dEnd < bestDist) { bestDist = dEnd; bestPos = ge.End; }
                    }
                    _keeperEdgeIndices = keeperIdx.ToArray();
                    if (bestDist < double.MaxValue)
                    {
                        _dispatchEntryPosition = bestPos;
                        _dispatchEntryResolved = true;
                        Engine.Message(_simulation, Engine.LogType.Log,
                            string.Format(CultureInfo.InvariantCulture,
                                "[DroneScaffold] Dispatch entry resolved on largest component ({0} edges) at ({1:0.#}, {2:0.#}); {3:0.#}m from charging station ({4:0.#}, {5:0.#}).",
                                keeperSize, bestPos.x, bestPos.y, bestDist, _chargingStation.x, _chargingStation.y));
                    }
                }

                if (_graph.EdgeCount == 0)
                {
                    Engine.Message(_simulation, Engine.LogType.Warning, "[DroneScaffold] SUMO graph parsing returned zero valid edges. Raster sweep continues on simulation grid.");
                }
            }
            catch (Exception e)
            {
                Engine.Message(_simulation, Engine.LogType.Warning, "[DroneScaffold] Failed to build SUMO graph: " + e.Message + " Raster sweep continues on simulation grid.");
            }
        }

        private static Vector2d ResolveChargingStation(DroneFleetInput fleetInput, Vector2d domainSize)
        {
            if (fleetInput.AutoPlaceChargingStationBottomLeft)
            {
                return Vector2d.zero;
            }

            double x = Mathd.Clamp(fleetInput.ChargingStationSimulationPos.x, 0.0, domainSize.x);
            double y = Mathd.Clamp(fleetInput.ChargingStationSimulationPos.y, 0.0, domainSize.y);
            return new Vector2d(x, y);
        }

        private Vector2d ToSimulationSpace((double x, double y) sumoPoint)
        {
            return new Vector2d(sumoPoint.x, sumoPoint.y) + _originOffset;
        }

        private double ComputePolylineLengthInSimulation(List<(double x, double y)> points)
        {
            double length = 0.0;
            for (int i = 0; i < points.Count - 1; ++i)
            {
                Vector2d a = ToSimulationSpace(points[i]);
                Vector2d b = ToSimulationSpace(points[i + 1]);
                length += Vector2d.Distance(a, b);
            }

            return length;
        }

        private void BuildDroneAgents()
        {
            _drones.Clear();
            // Spawn at the dispatch entry on the largest component rather than the
            // raw charging station position. Falls back to _chargingStation if the
            // graph builder couldn't resolve one (no edges, no keeper component).
            Vector2d defaultSpawn = _dispatchEntryResolved ? _dispatchEntryPosition : _chargingStation;

            bool randomSpawn = _moduleInput.Fleet.RandomSpawnAcrossNetwork
                               && _keeperEdgeIndices != null
                               && _keeperEdgeIndices.Length > 0;
            System.Random rng = randomSpawn
                ? (_moduleInput.Fleet.RandomSpawnSeed >= 0
                    ? new System.Random(_moduleInput.Fleet.RandomSpawnSeed)
                    : new System.Random())
                : null;

            if (randomSpawn)
            {
                Engine.Message(_simulation, Engine.LogType.Log,
                    string.Format(CultureInfo.InvariantCulture,
                        "[DroneScaffold] Random spawn enabled: {0} drones across {1} keeper-component edges (seed={2}).",
                        _moduleInput.Fleet.DroneCount, _keeperEdgeIndices.Length, _moduleInput.Fleet.RandomSpawnSeed));
            }

            for (int i = 0; i < _moduleInput.Fleet.DroneCount; ++i)
            {
                Vector2d spawn = defaultSpawn;
                if (randomSpawn)
                {
                    int pickIndex = _keeperEdgeIndices[rng.Next(_keeperEdgeIndices.Length)];
                    DroneEdgeRuntime pickedEdge = _graph.Edges[pickIndex];
                    // Sample a random point ALONG the edge so we don't bunch up at junctions.
                    double t = rng.NextDouble();
                    spawn = new Vector2d(
                        pickedEdge.Start.x + t * (pickedEdge.End.x - pickedEdge.Start.x),
                        pickedEdge.Start.y + t * (pickedEdge.End.y - pickedEdge.Start.y));
                }
                DroneAgentRuntime drone = new DroneAgentRuntime(i, _moduleInput.Fleet.UniformTypeId, spawn, _moduleInput.Fleet.CruiseSpeedMps);
                _drones.Add(drone);
            }
        }

        private void BuildPolicy()
        {
            if (_moduleInput.Policy.RoutingPolicy == DronePolicyInput.RoutingPolicies.ACS)
            {
                _policy = new AcsRoutingPolicy();
            }
            else if (_moduleInput.Policy.RoutingPolicy == DronePolicyInput.RoutingPolicies.AcoStigmergic)
            {
                _policy = new AcoStigmergicRoutingPolicy();
            }
            else
            {
                _policy = new RasterRoutingPolicy();
            }

            _policy.Initialize(new DroneRoutingContext
            {
                Graph = _graph,
                PolicyInput = _moduleInput.Policy,
                AcsInput = _moduleInput.AcsPolicy,
                RasterInput = _moduleInput.RasterPolicy,
                AcoInput = _moduleInput.AcoStigmergicPolicy,
                DomainSize = _simulation.Input.Simulation.DomainSize,
                RoadSegments = _roadSegments,
                DroneCount = _drones.Count
            });
        }

        // Loads per-edge time-averaged density from the SUMO meandata XML referenced in
        // AcoStigmergicPolicy.GammaSourceFile, computes d_bar_e and d_ref per the design
        // doc (Definition 2 and eq. dtilde), and pushes per-edge gamma + d_ref into the
        // policy. Results are also stashed on the module for telemetry output.
        // Caches results in a sidecar CSV next to the XML so repeat runs skip the parse.
        private void ApplyEdgeImportanceWeights()
        {
            _dBarPerEdge = new double[_graph.EdgeCount];
            _dRef = 0.0;
            _gammaSourceTRef = 0.0;

            if (!(_policy is AcoStigmergicRoutingPolicy acoPolicy))
            {
                return;
            }
            AcoStigmergicPolicyInput acoInput = _moduleInput.AcoStigmergicPolicy ?? new AcoStigmergicPolicyInput();
            string sourceFile = acoInput.GammaSourceFile;
            if (string.IsNullOrWhiteSpace(sourceFile))
            {
                Engine.Message(_simulation, Engine.LogType.Log, "[DroneScaffold] No GammaSourceFile configured; using DefaultGamma=" + acoInput.DefaultGamma + " on all edges.");
                return;
            }

            string resolved = Path.IsPathRooted(sourceFile)
                ? sourceFile
                : Path.Combine(_simulation.Engine.WorkingFolder, sourceFile);
            if (!File.Exists(resolved))
            {
                Engine.Message(_simulation, Engine.LogType.Warning, "[DroneScaffold] GammaSourceFile not found: " + resolved + ". Using DefaultGamma.");
                return;
            }

            // Reject in-progress files. SUMO writes _output/edge_density_1s.xml as the
            // current run progresses, so at sim t=0 it only contains an unclosed comment
            // and an empty <meandata> root. We need a finalised reference run instead.
            // A complete file ends with "</meandata>" on a line of its own; if we can't
            // see that token within the last 256 bytes, treat the file as in-progress.
            if (!IsFinalisedMeandataXml(resolved))
            {
                Engine.Message(_simulation, Engine.LogType.Warning,
                    "[DroneScaffold] GammaSourceFile appears in-progress (no </meandata> close tag): " + resolved
                    + ". This file is the live output of the current SUMO run, not a reference. "
                    + "Use a finalised XML from a prior run, set GammaSourceFile to its path, then re-run. Using DefaultGamma.");
                return;
            }

            try
            {
                Dictionary<string, double> sumSampledSecondsByEdgeId;
                double tRef;
                LoadOrParseEdgeReferenceDensity(resolved, out sumSampledSecondsByEdgeId, out tRef);
                _gammaSourceTRef = tRef;

                if (sumSampledSecondsByEdgeId.Count == 0 || tRef <= 0.0)
                {
                    Engine.Message(_simulation, Engine.LogType.Warning, "[DroneScaffold] GammaSourceFile yielded no usable data. Using DefaultGamma.");
                    return;
                }

                // Compute d_bar_e per drone-graph edge (veh/m/lane). Per design doc Definition 2,
                // d_bar_ref is the arithmetic mean over ALL edges of G — silent edges contribute
                // 0 to the numerator but still count in the denominator. This is what makes
                // gamma_e < 1 for quiet roads and gamma_e >> 1 for busy ones, as the spec intends.
                int matched = 0;
                double sumDBar = 0.0;
                for (int i = 0; i < _graph.EdgeCount; ++i)
                {
                    DroneEdgeRuntime edge = _graph.Edges[i];
                    if (string.IsNullOrEmpty(edge.EdgeId)) continue;
                    if (!sumSampledSecondsByEdgeId.TryGetValue(edge.EdgeId, out double vehSeconds)) continue;

                    double dBar = vehSeconds / (tRef * System.Math.Max(1e-6, edge.LengthMeters) * System.Math.Max(1, edge.LaneCount));
                    _dBarPerEdge[i] = dBar;
                    sumDBar += dBar;
                    matched++;
                }
                // Spec-faithful: d_ref averaged over the full kept-edge set (1985 silent edges
                // contribute zeros), not just over matched edges.
                _dRef = _graph.EdgeCount > 0 ? sumDBar / _graph.EdgeCount : 0.0;

                if (_dRef <= 0.0)
                {
                    Engine.Message(_simulation, Engine.LogType.Warning, "[DroneScaffold] d_ref computed as 0 (no traffic in reference run?). Falling back to DefaultGamma and dRef=1.");
                    return;
                }

                // Push gamma = d_bar_e / d_ref into the policy. Spec-faithful: edges with
                // d_bar_e == 0 (silent in the reference run) get gamma -> 0 (clamped to 1e-6
                // by TrySetGammaForEdge), giving them essentially-infinite memory in the decay
                // equations — they're flagged as low-importance so drones don't waste time
                // re-scanning quiet streets. DefaultGamma is only used as a numerical safety
                // floor inside TrySetGammaForEdge, not as a behavioural fallback here.
                acoPolicy.ConfigureDRef(_dRef);
                int gammaApplied = 0;
                int gammaSilent = 0;
                double gammaMin = double.MaxValue, gammaMax = 0.0, gammaSum = 0.0;
                for (int i = 0; i < _graph.EdgeCount; ++i)
                {
                    double gamma = _dBarPerEdge[i] / _dRef;   // 0 for silent edges; clamped >= 1e-6 on assignment
                    if (_dBarPerEdge[i] <= 0.0) gammaSilent++;
                    if (acoPolicy.TrySetGammaForEdge(i, gamma))
                    {
                        gammaApplied++;
                        if (gamma < gammaMin) gammaMin = gamma;
                        if (gamma > gammaMax) gammaMax = gamma;
                        gammaSum += gamma;
                    }
                }

                Engine.Message(_simulation, Engine.LogType.Log,
                    string.Format(CultureInfo.InvariantCulture,
                        "[DroneScaffold] Edge importance loaded: T_ref={0:0.0}s, matched={1}/{2} edges ({3} silent->gamma~0), d_ref={4:0.########} veh/m/lane, gamma min/mean/max = {5:0.####}/{6:0.###}/{7:0.###}.",
                        tRef, matched, _graph.EdgeCount, gammaSilent, _dRef, gammaMin, gammaSum / System.Math.Max(1, gammaApplied), gammaMax));
            }
            catch (Exception e)
            {
                Engine.Message(_simulation, Engine.LogType.Warning, "[DroneScaffold] Failed to load GammaSourceFile: " + e.Message + ". Using DefaultGamma.");
            }
        }

        // Confirms a SUMO meandata file is finalised — i.e. the root element is closed —
        // so we don't try to parse it while SUMO is still writing it.
        private static bool IsFinalisedMeandataXml(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long len = fs.Length;
                    if (len < 16) return false;
                    long offset = System.Math.Max(0, len - 512);
                    fs.Seek(offset, SeekOrigin.Begin);
                    byte[] buf = new byte[len - offset];
                    int read = fs.Read(buf, 0, buf.Length);
                    string tail = System.Text.Encoding.UTF8.GetString(buf, 0, read);
                    return tail.IndexOf("</meandata>", StringComparison.Ordinal) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        // Parses (or restores from cache) a SUMO meandata XML and returns the total
        // sampled-vehicle-seconds per edge id over the reference run. The cache is a
        // small CSV alongside the source XML; invalidated if the XML is newer.
        private void LoadOrParseEdgeReferenceDensity(string xmlPath, out Dictionary<string, double> sumByEdgeId, out double tRef)
        {
            string cachePath = xmlPath + ".gamma_cache.csv";
            DateTime xmlTime = File.GetLastWriteTimeUtc(xmlPath);
            if (File.Exists(cachePath) && File.GetLastWriteTimeUtc(cachePath) >= xmlTime)
            {
                sumByEdgeId = new Dictionary<string, double>();
                tRef = 0.0;
                using (StreamReader sr = new StreamReader(cachePath))
                {
                    string header = sr.ReadLine(); // edge_id,sum_sampled_seconds  (first comment line carries T_ref)
                    if (header != null && header.StartsWith("# t_ref=", StringComparison.Ordinal))
                    {
                        double.TryParse(header.Substring(8), NumberStyles.Float, CultureInfo.InvariantCulture, out tRef);
                        sr.ReadLine(); // skip column header
                    }
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        int comma = line.IndexOf(',');
                        if (comma <= 0) continue;
                        string id = line.Substring(0, comma);
                        if (double.TryParse(line.Substring(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                        {
                            sumByEdgeId[id] = v;
                        }
                    }
                }
                Engine.Message(_simulation, Engine.LogType.Log,
                    $"[DroneScaffold] Loaded edge-importance cache: {sumByEdgeId.Count} edges, T_ref={tRef:0.0}s ({cachePath}).");
                return;
            }

            sumByEdgeId = new Dictionary<string, double>(capacity: 4096);
            double tStart = double.NaN;
            double tEnd = 0.0;
            long edgeRecords = 0;
            DateTime t0 = DateTime.UtcNow;
            using (System.Xml.XmlReader reader = System.Xml.XmlReader.Create(xmlPath, new System.Xml.XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true }))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;
                    if (reader.Name == "interval")
                    {
                        if (double.TryParse(reader.GetAttribute("begin"), NumberStyles.Float, CultureInfo.InvariantCulture, out double begin))
                        {
                            if (double.IsNaN(tStart) || begin < tStart) tStart = begin;
                        }
                        if (double.TryParse(reader.GetAttribute("end"), NumberStyles.Float, CultureInfo.InvariantCulture, out double end))
                        {
                            if (end > tEnd) tEnd = end;
                        }
                    }
                    else if (reader.Name == "edge")
                    {
                        string id = reader.GetAttribute("id");
                        if (string.IsNullOrEmpty(id)) continue;
                        if (!double.TryParse(reader.GetAttribute("sampledSeconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) || sec <= 0.0) continue;
                        sumByEdgeId.TryGetValue(id, out double prev);
                        sumByEdgeId[id] = prev + sec;
                        edgeRecords++;
                    }
                }
            }
            tRef = double.IsNaN(tStart) ? tEnd : System.Math.Max(0.0, tEnd - tStart);
            double parseSeconds = (DateTime.UtcNow - t0).TotalSeconds;
            Engine.Message(_simulation, Engine.LogType.Log,
                string.Format(CultureInfo.InvariantCulture,
                    "[DroneScaffold] Parsed GammaSourceFile: {0} edges with traffic, {1} <edge> records, T_ref={2:0.0}s, parse={3:0.0}s.",
                    sumByEdgeId.Count, edgeRecords, tRef, parseSeconds));

            // Write cache so subsequent runs skip the multi-second parse.
            try
            {
                using (StreamWriter sw = new StreamWriter(cachePath))
                {
                    sw.WriteLine("# t_ref=" + tRef.ToString("0.######", CultureInfo.InvariantCulture));
                    sw.WriteLine("edge_id,sum_sampled_seconds");
                    foreach (KeyValuePair<string, double> kv in sumByEdgeId)
                    {
                        sw.Write(kv.Key);
                        sw.Write(',');
                        sw.WriteLine(kv.Value.ToString("0.######", CultureInfo.InvariantCulture));
                    }
                }
            }
            catch (Exception e)
            {
                Engine.Message(_simulation, Engine.LogType.Warning, "[DroneScaffold] Failed to write gamma cache: " + e.Message);
            }
        }

        private void AssignTargetsIfNeeded(double simulationTime)
        {
            for (int i = 0; i < _drones.Count; ++i)
            {
                DroneAgentRuntime drone = _drones[i];

                if (drone.State != DroneAgentState.Idle)
                {
                    continue;
                }

                if (drone.CurrentTask != null)
                {
                    continue;
                }

                if (ShouldReturnToCharge(drone))
                {
                    BeginReturnToCharge(drone);
                    continue;
                }

                if (_policy.TryAssignTask(drone, simulationTime, out DroneTaskRuntime task))
                {
                    drone.CurrentTask = task;
                    _policy.OnTaskStarted(drone, task, simulationTime);
                    ConfigureTaskTraversal(drone, task);
                }
            }
        }

        private void UpdateBatteryDynamics(double deltaTime)
        {
            for (int i = 0; i < _drones.Count; ++i)
            {
                DroneAgentRuntime drone = _drones[i];

                if (drone.State == DroneAgentState.Charging)
                {
                    ChargeDrone(drone, deltaTime);
                    continue;
                }

                if (IsAirborne(drone.State))
                {
                    DrainBattery(drone, deltaTime);
                }

                if (drone.State != DroneAgentState.ReturnToCharge && ShouldReturnToCharge(drone))
                {
                    BeginReturnToCharge(drone);
                }
            }
        }

        private void AdvanceDroneStates(double deltaTime)
        {
            for (int i = 0; i < _drones.Count; ++i)
            {
                DroneAgentRuntime drone = _drones[i];

                switch (drone.State)
                {
                    case DroneAgentState.Transit:
                        if (MoveDroneTowards(drone, drone.TransitTarget, deltaTime))
                        {
                            drone.State = DroneAgentState.ScanEdge;
                        }
                        break;

                    case DroneAgentState.ScanEdge:
                        if (MoveDroneTowards(drone, drone.ScanEnd, deltaTime))
                        {
                            HandleCompletedScan(drone);
                        }
                        break;

                    case DroneAgentState.ReturnToCharge:
                        if (MoveDroneTowards(drone, _chargingStation, deltaTime))
                        {
                            drone.State = DroneAgentState.Charging;
                        }
                        break;
                }
            }
        }

        private bool MoveDroneTowards(DroneAgentRuntime drone, Vector2d target, double deltaTime)
        {
            double maxDistance = System.Math.Max(0.0, drone.CruiseSpeedMps * deltaTime);
            drone.Position = Vector2d.MoveTowards(drone.Position, target, maxDistance);
            return Vector2d.Distance(drone.Position, target) <= ArrivalToleranceMeters;
        }

        private void ConfigureTaskTraversal(DroneAgentRuntime drone, DroneTaskRuntime task)
        {
            drone.ScanStart = task.ScanStart;
            drone.ScanEnd = task.ScanEnd;

            drone.TransitTarget = drone.ScanStart;
            if (Vector2d.Distance(drone.Position, drone.ScanStart) <= ArrivalToleranceMeters)
            {
                drone.State = DroneAgentState.ScanEdge;
            }
            else
            {
                drone.State = DroneAgentState.Transit;
            }
        }

        private void HandleCompletedScan(DroneAgentRuntime drone)
        {
            DroneTaskRuntime completedTask = drone.CurrentTask;
            if (completedTask != null)
            {
                if (completedTask.TaskType == DroneTaskType.RasterScan)
                {
                    double measuredVehicleCount = ComputeMeasuredCellVehicleCount(completedTask);
                    _policy.OnTaskCompleted(drone, completedTask, _simulation.Time.SimulationTime, measuredVehicleCount);
                    WriteRasterScanTelemetry(_simulation.Time.SimulationTime, drone, completedTask, measuredVehicleCount);
                }
                else
                {
                    double measuredDensityPerLane = ComputeMeasuredDensityPerLane(completedTask);
                    _policy.OnTaskCompleted(drone, completedTask, _simulation.Time.SimulationTime, measuredDensityPerLane);
                    WriteScanTelemetry(_simulation.Time.SimulationTime, drone, completedTask, measuredDensityPerLane);
                }
            }

            drone.CurrentTask = null;
            drone.State = DroneAgentState.Idle;
        }

        // Time-averaged number of vehicles inside the scanned cell over the scan window,
        // i.e. ( integral of n_cell(t) dt ) / T_scan. Falls back to an instantaneous box
        // count if the scan logged no ticks (e.g. arrived already at the scan end).
        private double ComputeMeasuredCellVehicleCount(DroneTaskRuntime task)
        {
            if (task == null || task.TaskType != DroneTaskType.RasterScan)
            {
                return 0.0;
            }

            if (task.AccumulatedSeconds > 1e-6)
            {
                return System.Math.Max(0.0, task.AccumulatedVehicleSeconds / task.AccumulatedSeconds);
            }

            TrafficModule trafficModule = _simulation.Evacuation?.TrafficModule;
            if (trafficModule != null)
            {
                return trafficModule.GetVehiclesInBoundingBox(task.ScanAreaMin, task.ScanAreaMax).Count;
            }
            return 0.0;
        }

        private void InitializeTelemetryOutput()
        {
            if (!_moduleInput.EnableTelemetryOutput)
            {
                return;
            }

            try
            {
                string outputFolder = _simulation.Engine.OutputFolder;
                _telemetryPrefix = _simulation.Input.Simulation.Name + "_" + _simulation.SimulationIndex + "_drone";

                string droneStatePath = Path.Combine(outputFolder, _telemetryPrefix + "_state_samples.csv");
                string scanPath = Path.Combine(outputFolder, _telemetryPrefix + "_scan_events.csv");
                string edgeGraphPath = Path.Combine(outputFolder, _telemetryPrefix + "_graph_edges.csv");

                _droneStateTelemetryWriter = new StreamWriter(droneStatePath);
                _droneStateTelemetryWriter.WriteLine("sim_time_s,utc_time,drone_id,x,y,state,battery_fraction,current_task_type,current_task_edge_id,current_task_edge_index,current_task_target_x,current_task_target_y");

                _scanTelemetryWriter = new StreamWriter(scanPath);
                _scanTelemetryWriter.WriteLine("sim_time_s,utc_time,drone_id,edge_id,edge_index,edge_length_m,lane_count,measured_density_per_lane,estimated_vehicle_count,scan_seconds,accumulated_vehicle_seconds,normalised_density,tau_v_deposit,d_ref");

                // Pull current per-edge gamma from the policy so the graph dump matches what's used at runtime.
                double[] gammaPerEdge = new double[_graph.Edges.Count];
                if (_policy is AcoStigmergicRoutingPolicy acoPolicy)
                {
                    int n = acoPolicy.CopyEdgeStateSnapshots(0.0, _acoEdgeSnapshotBuffer);
                    for (int i = 0; i < n; ++i)
                    {
                        AcoStigmergicRoutingPolicy.EdgeTelemetrySnapshot snap = _acoEdgeSnapshotBuffer[i];
                        if (snap.EdgeIndex >= 0 && snap.EdgeIndex < gammaPerEdge.Length)
                        {
                            gammaPerEdge[snap.EdgeIndex] = snap.Gamma;
                        }
                    }
                }

                using (StreamWriter edgeGraphWriter = new StreamWriter(edgeGraphPath))
                {
                    edgeGraphWriter.WriteLine("edge_index,edge_id,from_node,to_node,start_x,start_y,end_x,end_y,length_m,lane_count,d_bar_e,gamma,d_ref,t_ref_seconds");
                    string dRefStr = _dRef.ToString("0.########", CultureInfo.InvariantCulture);
                    string tRefStr = _gammaSourceTRef.ToString("0.######", CultureInfo.InvariantCulture);
                    for (int edgeIndex = 0; edgeIndex < _graph.Edges.Count; ++edgeIndex)
                    {
                        DroneEdgeRuntime edge = _graph.Edges[edgeIndex];
                        double dBar = _dBarPerEdge != null && edgeIndex < _dBarPerEdge.Length ? _dBarPerEdge[edgeIndex] : 0.0;
                        edgeGraphWriter.WriteLine(string.Join(",",
                            edgeIndex.ToString(CultureInfo.InvariantCulture),
                            CsvEscape(edge.EdgeId),
                            CsvEscape(edge.FromNode),
                            CsvEscape(edge.ToNode),
                            edge.Start.x.ToString("0.######", CultureInfo.InvariantCulture),
                            edge.Start.y.ToString("0.######", CultureInfo.InvariantCulture),
                            edge.End.x.ToString("0.######", CultureInfo.InvariantCulture),
                            edge.End.y.ToString("0.######", CultureInfo.InvariantCulture),
                            edge.LengthMeters.ToString("0.######", CultureInfo.InvariantCulture),
                            edge.LaneCount.ToString(CultureInfo.InvariantCulture),
                            dBar.ToString("0.########", CultureInfo.InvariantCulture),
                            gammaPerEdge[edgeIndex].ToString("0.######", CultureInfo.InvariantCulture),
                            dRefStr,
                            tRefStr));
                    }
                }

                if (_moduleInput.TelemetryIncludeEdgePheromones && _policy is AcoStigmergicRoutingPolicy)
                {
                    string edgeTelemetryPath = Path.Combine(outputFolder, _telemetryPrefix + "_aco_edge_samples.csv");
                    _acoEdgeTelemetryWriter = new StreamWriter(edgeTelemetryPath);
                    _acoEdgeTelemetryWriter.WriteLine("sim_time_s,utc_time,edge_index,edge_id,tau_s,tau_v,tau_c,occupancy_count,gamma,last_update_time_s");
                }

                // For the raster policy, dump the active cell geometry. cell_index matches the
                // edge_index column of raster scan events, and the row count is the coverage
                // denominator (analogous to _graph_edges.csv for the edge-based policy).
                if (_policy is RasterRoutingPolicy rasterPolicy)
                {
                    string rasterCellsPath = Path.Combine(outputFolder, _telemetryPrefix + "_raster_cells.csv");
                    using (StreamWriter cellWriter = new StreamWriter(rasterCellsPath))
                    {
                        cellWriter.WriteLine("cell_index,row,col,owner_drone_id,min_x,min_y,max_x,max_y,center_x,center_y");
                        IReadOnlyList<RasterCellRuntime> cells = rasterPolicy.GetRasterCells();
                        for (int c = 0; c < cells.Count; ++c)
                        {
                            RasterCellRuntime cell = cells[c];
                            cellWriter.WriteLine(string.Join(",",
                                cell.CellIndex.ToString(CultureInfo.InvariantCulture),
                                cell.Row.ToString(CultureInfo.InvariantCulture),
                                cell.Column.ToString(CultureInfo.InvariantCulture),
                                cell.OwnerDroneId.ToString(CultureInfo.InvariantCulture),
                                cell.Min.x.ToString("0.######", CultureInfo.InvariantCulture),
                                cell.Min.y.ToString("0.######", CultureInfo.InvariantCulture),
                                cell.Max.x.ToString("0.######", CultureInfo.InvariantCulture),
                                cell.Max.y.ToString("0.######", CultureInfo.InvariantCulture),
                                cell.Center.x.ToString("0.######", CultureInfo.InvariantCulture),
                                cell.Center.y.ToString("0.######", CultureInfo.InvariantCulture)));
                        }
                    }
                }

                _nextTelemetrySampleTime = 0.0;
                Engine.Message(_simulation, Engine.LogType.Log,
                    "[DroneScaffold] Telemetry output enabled: prefix=" + _telemetryPrefix + " folder=" + outputFolder);
            }
            catch (Exception e)
            {
                Engine.Message(_simulation, Engine.LogType.Warning,
                    "[DroneScaffold] Failed to initialize telemetry output: " + e.Message);
                DisposeTelemetryOutput();
            }
        }

        private void RecordTelemetry(double simulationTime)
        {
            if (!_moduleInput.EnableTelemetryOutput)
            {
                return;
            }

            if (simulationTime + 1e-9 < _nextTelemetrySampleTime)
            {
                return;
            }

            double interval = System.Math.Max(1e-3, _moduleInput.TelemetryIntervalSeconds);
            _nextTelemetrySampleTime = simulationTime + interval;

            try
            {
                string utcTime = _simulation.Time.CurrentUTCDateTime.ToString("o", CultureInfo.InvariantCulture);

                if (_droneStateTelemetryWriter != null)
                {
                    for (int droneIndex = 0; droneIndex < _drones.Count; ++droneIndex)
                    {
                        DroneAgentRuntime drone = _drones[droneIndex];
                        DroneTaskRuntime task = drone.CurrentTask;
                        string taskType = task != null ? task.TaskType.ToString() : DroneTaskType.None.ToString();
                        string edgeId = task != null ? CsvEscape(task.EdgeId) : string.Empty;
                        string edgeIndex = task != null ? task.TaskIndex.ToString(CultureInfo.InvariantCulture) : "-1";
                        Vector2d target = task != null ? task.TransitTarget : drone.TransitTarget;

                        _droneStateTelemetryWriter.WriteLine(string.Join(",",
                            simulationTime.ToString("0.###", CultureInfo.InvariantCulture),
                            utcTime,
                            drone.AgentId.ToString(CultureInfo.InvariantCulture),
                            drone.Position.x.ToString("0.######", CultureInfo.InvariantCulture),
                            drone.Position.y.ToString("0.######", CultureInfo.InvariantCulture),
                            drone.State.ToString(),
                            drone.BatteryFraction.ToString("0.######", CultureInfo.InvariantCulture),
                            taskType,
                            edgeId,
                            edgeIndex,
                            target.x.ToString("0.######", CultureInfo.InvariantCulture),
                            target.y.ToString("0.######", CultureInfo.InvariantCulture)));
                    }
                    _droneStateTelemetryWriter.Flush();
                }

                if (_acoEdgeTelemetryWriter != null && _policy is AcoStigmergicRoutingPolicy acoPolicy)
                {
                    int edgeCount = acoPolicy.CopyEdgeStateSnapshots(simulationTime, _acoEdgeSnapshotBuffer);
                    for (int i = 0; i < edgeCount; ++i)
                    {
                        AcoStigmergicRoutingPolicy.EdgeTelemetrySnapshot snapshot = _acoEdgeSnapshotBuffer[i];
                        if (snapshot.EdgeIndex < 0 || snapshot.EdgeIndex >= _graph.Edges.Count)
                        {
                            continue;
                        }

                        DroneEdgeRuntime edge = _graph.Edges[snapshot.EdgeIndex];
                        _acoEdgeTelemetryWriter.WriteLine(string.Join(",",
                            simulationTime.ToString("0.###", CultureInfo.InvariantCulture),
                            utcTime,
                            snapshot.EdgeIndex.ToString(CultureInfo.InvariantCulture),
                            CsvEscape(edge.EdgeId),
                            snapshot.TauS.ToString("0.######", CultureInfo.InvariantCulture),
                            snapshot.TauV.ToString("0.######", CultureInfo.InvariantCulture),
                            snapshot.TauC.ToString("0.######", CultureInfo.InvariantCulture),
                            snapshot.OccupancyCount.ToString(CultureInfo.InvariantCulture),
                            snapshot.Gamma.ToString("0.######", CultureInfo.InvariantCulture),
                            snapshot.LastUpdateTime.ToString("0.###", CultureInfo.InvariantCulture)));
                    }
                    _acoEdgeTelemetryWriter.Flush();
                }
            }
            catch (Exception e)
            {
                Engine.Message(_simulation, Engine.LogType.Warning,
                    "[DroneScaffold] Failed to record telemetry sample: " + e.Message);
                DisposeTelemetryOutput();
            }
        }

        private void WriteScanTelemetry(double simulationTime, DroneAgentRuntime drone, DroneTaskRuntime task, double measuredDensityPerLane)
        {
            if (_scanTelemetryWriter == null || task == null)
            {
                return;
            }

            try
            {
                double estimatedVehicles = measuredDensityPerLane * System.Math.Max(1e-6, task.EdgeLengthMeters) * System.Math.Max(1, task.LaneCount);
                string utcTime = _simulation.Time.CurrentUTCDateTime.ToString("o", CultureInfo.InvariantCulture);
                double dRefForScan = _dRef > 0.0 ? _dRef : 1.0;
                double normalisedDensity = measuredDensityPerLane / dRefForScan;
                double tauVDeposit = (_moduleInput.AcoStigmergicPolicy != null ? _moduleInput.AcoStigmergicPolicy.Qv : 1.0) * normalisedDensity;
                _scanTelemetryWriter.WriteLine(string.Join(",",
                    simulationTime.ToString("0.###", CultureInfo.InvariantCulture),
                    utcTime,
                    drone.AgentId.ToString(CultureInfo.InvariantCulture),
                    CsvEscape(task.EdgeId),
                    task.TaskIndex.ToString(CultureInfo.InvariantCulture),
                    task.EdgeLengthMeters.ToString("0.######", CultureInfo.InvariantCulture),
                    task.LaneCount.ToString(CultureInfo.InvariantCulture),
                    measuredDensityPerLane.ToString("0.######", CultureInfo.InvariantCulture),
                    estimatedVehicles.ToString("0.######", CultureInfo.InvariantCulture),
                    task.AccumulatedSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    task.AccumulatedVehicleSeconds.ToString("0.######", CultureInfo.InvariantCulture),
                    normalisedDensity.ToString("0.######", CultureInfo.InvariantCulture),
                    tauVDeposit.ToString("0.######", CultureInfo.InvariantCulture),
                    dRefForScan.ToString("0.########", CultureInfo.InvariantCulture)));
                _scanTelemetryWriter.Flush();
            }
            catch (Exception e)
            {
                Engine.Message(_simulation, Engine.LogType.Warning,
                    "[DroneScaffold] Failed to write scan telemetry: " + e.Message);
            }
        }

        // Writes a raster-cell scan into the SAME _scan_events.csv schema as edge scans, so
        // the downstream validation (group by edge_index, sum estimated_vehicle_count, count
        // fresh units for coverage) works identically for both policies. For raster:
        //   edge_index             = cell index (matches DroneTaskRuntime.TaskIndex)
        //   edge_id                = "cell_<index>"
        //   estimated_vehicle_count = time-averaged vehicles inside the cell during the scan
        //   measured_density_per_lane = vehicles per m^2 of cell area (informational)
        //   ACO-only columns (normalised_density, tau_v_deposit, d_ref) are 0.
        private void WriteRasterScanTelemetry(double simulationTime, DroneAgentRuntime drone, DroneTaskRuntime task, double measuredVehicleCount)
        {
            if (_scanTelemetryWriter == null || task == null)
            {
                return;
            }

            try
            {
                double cellWidth = System.Math.Max(0.0, task.ScanAreaMax.x - task.ScanAreaMin.x);
                double cellHeight = System.Math.Max(0.0, task.ScanAreaMax.y - task.ScanAreaMin.y);
                double cellArea = System.Math.Max(1e-6, cellWidth * cellHeight);
                double densityPerSquareMetre = measuredVehicleCount / cellArea;
                string utcTime = _simulation.Time.CurrentUTCDateTime.ToString("o", CultureInfo.InvariantCulture);
                _scanTelemetryWriter.WriteLine(string.Join(",",
                    simulationTime.ToString("0.###", CultureInfo.InvariantCulture),
                    utcTime,
                    drone.AgentId.ToString(CultureInfo.InvariantCulture),
                    CsvEscape("cell_" + task.TaskIndex.ToString(CultureInfo.InvariantCulture)),
                    task.TaskIndex.ToString(CultureInfo.InvariantCulture),
                    cellWidth.ToString("0.######", CultureInfo.InvariantCulture),
                    "1",
                    densityPerSquareMetre.ToString("0.######", CultureInfo.InvariantCulture),
                    measuredVehicleCount.ToString("0.######", CultureInfo.InvariantCulture),
                    task.AccumulatedSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    task.AccumulatedVehicleSeconds.ToString("0.######", CultureInfo.InvariantCulture),
                    "0",
                    "0",
                    "0"));
                _scanTelemetryWriter.Flush();
            }
            catch (Exception e)
            {
                Engine.Message(_simulation, Engine.LogType.Warning,
                    "[DroneScaffold] Failed to write raster scan telemetry: " + e.Message);
            }
        }

        private void DisposeTelemetryOutput()
        {
            _droneStateTelemetryWriter?.Dispose();
            _droneStateTelemetryWriter = null;

            _scanTelemetryWriter?.Dispose();
            _scanTelemetryWriter = null;

            _acoEdgeTelemetryWriter?.Dispose();
            _acoEdgeTelemetryWriter = null;
        }

        private static string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0)
            {
                return '"' + value.Replace("\"", "\"\"") + '"';
            }

            return value;
        }

        private void AccumulateScanMeasurements(double deltaTime)
        {
            if (deltaTime <= 0.0)
            {
                return;
            }

            TrafficModule trafficModule = _simulation.Evacuation?.TrafficModule;
            if (trafficModule == null)
            {
                return;
            }

            for (int i = 0; i < _drones.Count; ++i)
            {
                DroneAgentRuntime drone = _drones[i];
                if (drone.State != DroneAgentState.ScanEdge || drone.CurrentTask == null)
                {
                    continue;
                }

                DroneTaskRuntime task = drone.CurrentTask;

                if (task.TaskType == DroneTaskType.EdgeScan)
                {
                    if (string.IsNullOrWhiteSpace(task.EdgeId))
                    {
                        continue;
                    }
                    if (trafficModule.TryGetEdgeVehicleCount(task.EdgeId, task.IncludeBidiEdgeInCounts, out int vehicleCount))
                    {
                        task.AccumulatedVehicleSeconds += System.Math.Max(0, vehicleCount) * deltaTime;
                        task.AccumulatedSeconds += deltaTime;
                    }
                }
                else if (task.TaskType == DroneTaskType.RasterScan)
                {
                    // The drone "tracks" every vehicle inside the cell rectangle while scanning.
                    // Time-integrate the in-cell count so the completed-scan value is the mean
                    // number of vehicles present over the scan window (mirrors the edge path).
                    int vehicleCount = trafficModule.GetVehiclesInBoundingBox(task.ScanAreaMin, task.ScanAreaMax).Count;
                    task.AccumulatedVehicleSeconds += vehicleCount * deltaTime;
                    task.AccumulatedSeconds += deltaTime;
                }
            }
        }

        private double ComputeMeasuredDensityPerLane(DroneTaskRuntime task)
        {
            if (task == null || task.TaskType != DroneTaskType.EdgeScan)
            {
                return 0.0;
            }

            double edgeLength = System.Math.Max(1e-6, task.EdgeLengthMeters);
            int laneCount = System.Math.Max(1, task.LaneCount);

            // Per design doc eq. (10): d_scan = ( int n_e(t) dt ) / ( T_e * ell_e * k_e ).
            // The numerator is task.AccumulatedVehicleSeconds, which is filled in every
            // tick during the scan by AccumulateScanData(); T_e is task.AccumulatedSeconds.
            // Falls back to an instantaneous sample only if no ticks were ever counted.
            double scanSeconds = task.AccumulatedSeconds;
            double density;
            if (scanSeconds > 1e-6)
            {
                double meanVehicleCount = task.AccumulatedVehicleSeconds / scanSeconds;
                density = System.Math.Max(0.0, meanVehicleCount) / (edgeLength * laneCount);
            }
            else
            {
                int vehicleCount = 0;
                TrafficModule trafficModule = _simulation.Evacuation?.TrafficModule;
                if (trafficModule != null && !string.IsNullOrWhiteSpace(task.EdgeId))
                {
                    trafficModule.TryGetEdgeVehicleCount(task.EdgeId, task.IncludeBidiEdgeInCounts, out vehicleCount);
                }
                density = System.Math.Max(0, vehicleCount) / (edgeLength * laneCount);
            }

            task.LastMeasuredDensityPerLane = density;
            return density;
        }

        private void DrainBattery(DroneAgentRuntime drone, double deltaTime)
        {
            double enduranceSeconds = System.Math.Max(1.0, _moduleInput.Fleet.BatteryEnduranceSeconds);
            double drain = deltaTime / enduranceSeconds;
            drone.BatteryFraction = Mathd.Clamp(drone.BatteryFraction - drain, 0.0, 1.0);
        }

        private void ChargeDrone(DroneAgentRuntime drone, double deltaTime)
        {
            double rechargeSeconds = System.Math.Max(1.0, _moduleInput.Fleet.RechargeDurationSeconds);
            double charge = deltaTime / rechargeSeconds;
            drone.BatteryFraction = Mathd.Clamp(drone.BatteryFraction + charge, 0.0, 1.0);

            if (drone.BatteryFraction >= 1.0)
            {
                drone.BatteryFraction = 1.0;
                drone.State = DroneAgentState.Idle;
            }
        }

        private bool ShouldReturnToCharge(DroneAgentRuntime drone)
        {
            if (drone.State == DroneAgentState.Charging)
            {
                return false;
            }

            double requiredFractionToHome = EstimateRequiredBatteryFractionToHome(drone);
            double reserve = Mathd.Clamp(_moduleInput.Fleet.ReturnReserveFraction, 0.0, 1.0);
            double triggerThreshold = requiredFractionToHome + reserve;
            return drone.BatteryFraction <= triggerThreshold;
        }

        private void BeginReturnToCharge(DroneAgentRuntime drone)
        {
            drone.CurrentEdgeIndex = -1;
            if (drone.CurrentTask != null)
            {
                _policy.OnTaskAborted(drone, drone.CurrentTask, _simulation.Time.SimulationTime);
            }
            drone.CurrentTask = null;
            drone.TransitTarget = _chargingStation;

            if (Vector2d.Distance(drone.Position, _chargingStation) <= ArrivalToleranceMeters)
            {
                drone.State = DroneAgentState.Charging;
            }
            else
            {
                drone.State = DroneAgentState.ReturnToCharge;
            }
        }

        private double EstimateRequiredBatteryFractionToHome(DroneAgentRuntime drone)
        {
            double enduranceSeconds = System.Math.Max(1.0, _moduleInput.Fleet.BatteryEnduranceSeconds);
            double speed = System.Math.Max(0.1, drone.CruiseSpeedMps);
            double distanceToStation = Vector2d.Distance(drone.Position, _chargingStation);
            double travelSecondsToStation = distanceToStation / speed;
            return travelSecondsToStation / enduranceSeconds;
        }

        private static bool IsAirborne(DroneAgentState state)
        {
            return state == DroneAgentState.Transit || state == DroneAgentState.ScanEdge || state == DroneAgentState.ReturnToCharge;
        }

        private void UpdateDroneSnapshots()
        {
            Vector2d[] positionSnapshot = new Vector2d[_drones.Count];
            DroneAgentState[] stateSnapshot = new DroneAgentState[_drones.Count];
            for (int i = 0; i < _drones.Count; ++i)
            {
                positionSnapshot[i] = _drones[i].Position;
                stateSnapshot[i] = _drones[i].State;
            }

            _dronePositionsSnapshot = positionSnapshot;
            _droneStatesSnapshot = stateSnapshot;
        }


        private void UpdateDiagnostics(double deltaTime, double simulationTime)
        {
            _diagnosticsTimer += deltaTime;
            if (_diagnosticsTimer < _diagnosticsIntervalSeconds)
            {
                return;
            }

            _diagnosticsTimer = 0.0;
            LogDiagnostics(simulationTime);
        }

        private void LogDiagnostics(double simulationTime)
        {
            double minBattery = double.MaxValue;
            double maxBattery = double.MinValue;
            double sumBattery = 0.0;
            int idleCount = 0;
            int transitCount = 0;
            int scanCount = 0;
            int returnCount = 0;
            int chargingCount = 0;

            TryGetRasterGrid(out _, out _, out _, out _);
            TryGetRasterActiveMask(out _);

            for (int i = 0; i < _drones.Count; ++i)
            {
                DroneAgentRuntime drone = _drones[i];
                if (drone.BatteryFraction < minBattery)
                {
                    minBattery = drone.BatteryFraction;
                }
                if (drone.BatteryFraction > maxBattery)
                {
                    maxBattery = drone.BatteryFraction;
                }
                sumBattery += drone.BatteryFraction;

                switch (drone.State)
                {
                    case DroneAgentState.Idle:
                        ++idleCount;
                        break;
                    case DroneAgentState.Transit:
                        ++transitCount;
                        break;
                    case DroneAgentState.ScanEdge:
                        ++scanCount;
                        break;
                    case DroneAgentState.ReturnToCharge:
                        ++returnCount;
                        break;
                    case DroneAgentState.Charging:
                        ++chargingCount;
                        break;
                }
            }

            double meanBattery = _drones.Count > 0 ? sumBattery / _drones.Count : 0.0;
            int activeRasterCells = 0;
            for (int i = 0; i < _rasterActiveGridMask.Length; ++i)
            {
                if (_rasterActiveGridMask[i])
                {
                    ++activeRasterCells;
                }
            }

            _latestStatusText =
                $"Policy: {_policy.Name}\n" +
                $"t: {simulationTime:0.0}s\n" +
                $"Raster cells active/total: {activeRasterCells}/{_rasterGridTotalCells}\n" +
                $"Grid rows/cols: {_rasterGridRows}/{_rasterGridColumns}\n" +
                $"Drones: {_drones.Count}\n" +
                $"Battery min/mean/max: {100.0 * minBattery:0.0}%/{100.0 * meanBattery:0.0}%/{100.0 * maxBattery:0.0}%\n" +
                $"States idle/transit/scan/return/charge: {idleCount}/{transitCount}/{scanCount}/{returnCount}/{chargingCount}";

            Engine.Message(_simulation, Engine.LogType.Log,
                $"[DroneScaffold] t={simulationTime:0.0}s policy={_policy.Name} cells={activeRasterCells}/{_rasterGridTotalCells} drones={_drones.Count} battery[min/mean/max]={100.0 * minBattery:0.0}%/{100.0 * meanBattery:0.0}%/{100.0 * maxBattery:0.0}% states[idle/transit/scan/return/charge]={idleCount}/{transitCount}/{scanCount}/{returnCount}/{chargingCount}");
        }
    }
}
