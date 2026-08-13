using System.Collections.Generic;
using PREACT.Input;
using PREACT.Math;

namespace PREACT.Evacuation
{
    public class AcoStigmergicRoutingPolicy : IDroneRoutingPolicy
    {
        public struct EdgeTelemetrySnapshot
        {
            public int EdgeIndex;
            public double TauS;
            public double TauV;
            public double TauC;
            public int OccupancyCount;
            public double Gamma;
            public double LastUpdateTime;
        }

        private class EdgeState
        {
            public double TauS;
            public double TauV;
            public double TauC;
            public double LastUpdateTime;
            public int OccupancyCount;
            public double Gamma;
        }

        private DroneRoutingContext _context;
        private AcoStigmergicPolicyInput _input;
        private EdgeState[] _edgeState;
        private System.Random _random;
        private readonly Dictionary<int, int> _droneActiveEdge = new Dictionary<int, int>();

        // ---- Graph diffusion state (active iff _input.DiffusionV > 0) ----
        // _edgeNeighbours[i] is the set of edge indices sharing an endpoint with edge i.
        // Computed once at Initialize from the graph's NodeToEdgeIndices adjacency.
        // _diffusionBuffer holds the next-step values during a Laplacian update so we
        // get Jacobi iteration (read old, write new) rather than implicit Gauss-Seidel
        // drift that would over-smooth and quietly break mass conservation.
        private int[][] _edgeNeighbours;
        private double[] _diffusionBuffer;
        private double _lastDiffusionTime = 0.0;

        // Network reference density d_ref (eq. dtilde in the design doc). Defaults to 1.0
        // so that if the loader was not run, the deposit reduces to the legacy raw value.
        private double _dRef = 1.0;

        public double DRef { get => _dRef; }

        public void ConfigureDRef(double dRef)
        {
            _dRef = System.Math.Max(1e-9, dRef);
        }

        public bool TrySetGammaForEdge(int edgeIndex, double gamma)
        {
            if (!IsValidEdgeIndex(edgeIndex) || double.IsNaN(gamma) || double.IsInfinity(gamma))
            {
                return false;
            }
            _edgeState[edgeIndex].Gamma = System.Math.Max(1e-6, gamma);
            return true;
        }

        public string Name { get => nameof(DronePolicyInput.RoutingPolicies.AcoStigmergic); }

        public void Initialize(DroneRoutingContext context)
        {
            _context = context;
            _input = context.AcoInput ?? new AcoStigmergicPolicyInput();
            _random = _input.RandomSeed >= 0 ? new System.Random(_input.RandomSeed) : new System.Random();
            _droneActiveEdge.Clear();

            int edgeCount = _context?.Graph?.EdgeCount ?? 0;
            _edgeState = new EdgeState[edgeCount];
            for (int i = 0; i < edgeCount; ++i)
            {
                _edgeState[i] = new EdgeState
                {
                    TauS = 0.0,
                    TauV = 0.0,
                    TauC = 0.0,
                    LastUpdateTime = 0.0,
                    OccupancyCount = 0,
                    Gamma = System.Math.Max(1e-6, _input.DefaultGamma)
                };
            }

            BuildEdgeNeighbourTable();
            _diffusionBuffer = new double[edgeCount];
            _lastDiffusionTime = 0.0;
        }

        // Builds the per-edge adjacency table used by the Laplacian diffusion pass.
        // Two edges are neighbours iff they share at least one junction endpoint.
        // O(sum over nodes of deg^2) memory and time at init — negligible for typical
        // road graphs (a few hundred KB on a 2000-edge network).
        private void BuildEdgeNeighbourTable()
        {
            int edgeCount = _edgeState != null ? _edgeState.Length : 0;
            if (edgeCount == 0 || _context?.Graph == null)
            {
                _edgeNeighbours = new int[0][];
                return;
            }

            var sets = new HashSet<int>[edgeCount];
            for (int i = 0; i < edgeCount; ++i) sets[i] = new HashSet<int>();

            foreach (var kv in _context.Graph.NodeToEdgeIndices)
            {
                List<int> incident = kv.Value;
                int n = incident.Count;
                for (int a = 0; a < n; ++a)
                {
                    int ea = incident[a];
                    if (ea < 0 || ea >= edgeCount) continue;
                    for (int b = a + 1; b < n; ++b)
                    {
                        int eb = incident[b];
                        if (eb < 0 || eb >= edgeCount) continue;
                        sets[ea].Add(eb);
                        sets[eb].Add(ea);
                    }
                }
            }

            _edgeNeighbours = new int[edgeCount][];
            for (int i = 0; i < edgeCount; ++i)
            {
                _edgeNeighbours[i] = new int[sets[i].Count];
                sets[i].CopyTo(_edgeNeighbours[i]);
            }
        }

        public void OnSimulationStep(double simulationTime, double deltaTime)
        {
            if (_input == null || _input.DiffusionV <= 0.0 || _edgeState == null || _edgeState.Length == 0)
            {
                return;
            }

            double sinceLast = simulationTime - _lastDiffusionTime;
            double step = System.Math.Max(1e-6, _input.DiffusionStepSeconds);
            if (sinceLast + 1e-9 < step)
            {
                return;
            }

            // Bring every edge current with respect to lazy decay before reading tau_v.
            // Otherwise edges that haven't been touched recently would diffuse stale values.
            for (int i = 0; i < _edgeState.Length; ++i)
            {
                ApplyDecay(i, simulationTime);
            }

            // Explicit-Euler diffusion is stable iff Dv * dt < 0.5 with normalised
            // Laplacian. Clamp the per-substep dt to keep us safely below that, and
            // do however many substeps are needed to cover the full sinceLast interval.
            double maxSubstep = 0.4 / _input.DiffusionV;
            int subSteps = System.Math.Max(1, (int)System.Math.Ceiling(sinceLast / maxSubstep));
            double subDt = sinceLast / subSteps;

            for (int s = 0; s < subSteps; ++s)
            {
                // Pass 1: compute new tau_v into the buffer using current edge state.
                for (int e = 0; e < _edgeState.Length; ++e)
                {
                    int[] nbrs = _edgeNeighbours != null && e < _edgeNeighbours.Length ? _edgeNeighbours[e] : null;
                    if (nbrs == null || nbrs.Length == 0)
                    {
                        _diffusionBuffer[e] = _edgeState[e].TauV;
                        continue;
                    }
                    double sum = 0.0;
                    for (int k = 0; k < nbrs.Length; ++k)
                    {
                        sum += _edgeState[nbrs[k]].TauV;
                    }
                    double avgNeighbour = sum / nbrs.Length;
                    double delta = _input.DiffusionV * subDt * (avgNeighbour - _edgeState[e].TauV);
                    _diffusionBuffer[e] = _edgeState[e].TauV + delta;
                }

                // Pass 2: write back. Clamp to non-negative as a numerical safety net
                // (the Laplacian preserves non-negativity analytically but float noise
                // around zero deposits can otherwise drift slightly negative).
                for (int e = 0; e < _edgeState.Length; ++e)
                {
                    double v = _diffusionBuffer[e];
                    _edgeState[e].TauV = v > 0.0 ? v : 0.0;
                }
            }

            _lastDiffusionTime = simulationTime;
        }

        public bool TryAssignTask(DroneAgentRuntime drone, double simulationTime, out DroneTaskRuntime task)
        {
            task = null;
            if (_context?.Graph == null || _context.Graph.EdgeCount == 0)
            {
                return false;
            }

            string nodeId = ResolveCurrentNodeId(drone);
            if (string.IsNullOrWhiteSpace(nodeId) || !_context.Graph.NodeToEdgeIndices.TryGetValue(nodeId, out List<int> incidentEdges) || incidentEdges.Count == 0)
            {
                return false;
            }

            double weightSum = 0.0;
            double[] weights = new double[incidentEdges.Count];
            for (int i = 0; i < incidentEdges.Count; ++i)
            {
                int edgeIndex = incidentEdges[i];
                ApplyDecay(edgeIndex, simulationTime);
                EdgeState s = _edgeState[edgeIndex];
                double freshnessTerm = System.Math.Pow(1.0 + s.TauS, -_input.Alpha);
                double densityTerm = System.Math.Pow(1.0 + s.TauV, _input.Beta);
                double crowdedTerm = System.Math.Pow(1.0 + s.TauC, -_input.Delta);
                double weight = freshnessTerm * densityTerm * crowdedTerm;
                if (double.IsNaN(weight) || weight <= 0.0)
                {
                    weight = 1e-9;
                }

                weights[i] = weight;
                weightSum += weight;
            }

            if (weightSum <= 0.0)
            {
                return false;
            }

            double sample = _random.NextDouble() * weightSum;
            double running = 0.0;
            int pickedLocalIndex = incidentEdges.Count - 1;
            for (int i = 0; i < incidentEdges.Count; ++i)
            {
                running += weights[i];
                if (sample <= running)
                {
                    pickedLocalIndex = i;
                    break;
                }
            }

            int pickedEdgeIndex = incidentEdges[pickedLocalIndex];
            DroneEdgeRuntime edge = _context.Graph.Edges[pickedEdgeIndex];
            Vector2d scanStart = Vector2d.Distance(drone.Position, edge.Start) <= Vector2d.Distance(drone.Position, edge.End) ? edge.Start : edge.End;
            Vector2d scanEnd = Vector2d.Distance(scanStart, edge.Start) <= 0.001 ? edge.End : edge.Start;

            task = new DroneTaskRuntime
            {
                TaskType = DroneTaskType.EdgeScan,
                TaskIndex = pickedEdgeIndex,
                TransitTarget = scanStart,
                ScanStart = scanStart,
                ScanEnd = scanEnd,
                EdgeId = edge.EdgeId,
                EdgeLengthMeters = edge.LengthMeters,
                LaneCount = edge.LaneCount,
                IncludeBidiEdgeInCounts = _input.UseBidiAggregation,
                AccumulatedSeconds = 0.0,
                AccumulatedVehicleSeconds = 0.0,
                LastMeasuredDensityPerLane = 0.0
            };
            return true;
        }

        public void OnTaskStarted(DroneAgentRuntime drone, DroneTaskRuntime task, double simulationTime)
        {
            if (task == null || task.TaskType != DroneTaskType.EdgeScan)
            {
                return;
            }

            int edgeIndex = task.TaskIndex;
            if (!IsValidEdgeIndex(edgeIndex))
            {
                return;
            }

            ApplyDecay(edgeIndex, simulationTime);
            _edgeState[edgeIndex].OccupancyCount += 1;
            _droneActiveEdge[drone.AgentId] = edgeIndex;
        }

        public void OnTaskCompleted(DroneAgentRuntime drone, DroneTaskRuntime task, double simulationTime, double measuredDensityPerLane)
        {
            if (task == null || task.TaskType != DroneTaskType.EdgeScan)
            {
                return;
            }

            int edgeIndex = task.TaskIndex;
            if (!IsValidEdgeIndex(edgeIndex))
            {
                return;
            }

            ApplyDecay(edgeIndex, simulationTime);
            EdgeState state = _edgeState[edgeIndex];
            state.OccupancyCount = System.Math.Max(0, state.OccupancyCount - 1);
            state.TauS += _input.Qs;
            // Per design doc eq. (6): deposit is Q_v * tilde{d_scan}, where
            // tilde{d_scan} = d_scan / d_ref is the dimensionless normalised density.
            // When UseReferenceWeightsOnline=false we deposit raw veh/m/lane instead,
            // so the on-line dynamics aren't biased by the offline d_ref scalar.
            double dRefActive = _input.UseReferenceWeightsOnline ? _dRef : 1.0;
            double normalisedDensity = System.Math.Max(0.0, measuredDensityPerLane) / dRefActive;
            state.TauV += _input.Qv * normalisedDensity;

            _droneActiveEdge.Remove(drone.AgentId);
        }

        public void OnTaskAborted(DroneAgentRuntime drone, DroneTaskRuntime task, double simulationTime)
        {
            int edgeIndex;
            if (!_droneActiveEdge.TryGetValue(drone.AgentId, out edgeIndex))
            {
                return;
            }

            if (!IsValidEdgeIndex(edgeIndex))
            {
                _droneActiveEdge.Remove(drone.AgentId);
                return;
            }

            ApplyDecay(edgeIndex, simulationTime);
            _edgeState[edgeIndex].OccupancyCount = System.Math.Max(0, _edgeState[edgeIndex].OccupancyCount - 1);
            _droneActiveEdge.Remove(drone.AgentId);
        }

        public bool TryGetRasterGrid(out Vector2d min, out Vector2d max, out int rows, out int columns)
        {
            min = Vector2d.zero;
            max = Vector2d.zero;
            rows = 0;
            columns = 0;
            return false;
        }

        public bool TryGetRasterActiveMask(out bool[] activeMask)
        {
            activeMask = null;
            return false;
        }

        public int CopyEdgeStateSnapshots(double simulationTime, List<EdgeTelemetrySnapshot> snapshots)
        {
            if (snapshots == null || _edgeState == null)
            {
                return 0;
            }

            snapshots.Clear();
            snapshots.Capacity = System.Math.Max(snapshots.Capacity, _edgeState.Length);

            for (int edgeIndex = 0; edgeIndex < _edgeState.Length; ++edgeIndex)
            {
                ApplyDecay(edgeIndex, simulationTime);
                EdgeState state = _edgeState[edgeIndex];
                snapshots.Add(new EdgeTelemetrySnapshot
                {
                    EdgeIndex = edgeIndex,
                    TauS = state.TauS,
                    TauV = state.TauV,
                    TauC = state.TauC,
                    OccupancyCount = state.OccupancyCount,
                    Gamma = state.Gamma,
                    LastUpdateTime = state.LastUpdateTime
                });
            }

            return snapshots.Count;
        }

        private void ApplyDecay(int edgeIndex, double simulationTime)
        {
            EdgeState s = _edgeState[edgeIndex];
            double dt = simulationTime - s.LastUpdateTime;
            if (dt <= 0.0)
            {
                return;
            }

            // When UseReferenceWeightsOnline=false (default), bypass gamma amplification:
            // the decay rate becomes simply RhoX * dt (gamma = 1). gamma is still stored
            // on the edge state and emitted to telemetry — it's just not used here.
            double gammaActive = _input.UseReferenceWeightsOnline ? s.Gamma : 1.0;
            double decayS = System.Math.Exp(-_input.RhoS * gammaActive * dt);
            double decayV = System.Math.Exp(-_input.RhoV * gammaActive * dt);
            double decayC = System.Math.Exp(-_input.RhoC * dt);

            s.TauS *= decayS;
            s.TauV *= decayV;
            s.TauC = s.TauC * decayC + (_input.Qc * s.OccupancyCount / _input.RhoC) * (1.0 - decayC);
            s.LastUpdateTime = simulationTime;
        }

        private bool IsValidEdgeIndex(int edgeIndex)
        {
            return _edgeState != null && edgeIndex >= 0 && edgeIndex < _edgeState.Length;
        }

        private string ResolveCurrentNodeId(DroneAgentRuntime drone)
        {
            if (_context?.Graph == null || _context.Graph.EdgeCount == 0)
            {
                return null;
            }

            double bestDistance = double.MaxValue;
            string bestNodeId = null;
            for (int i = 0; i < _context.Graph.Edges.Count; ++i)
            {
                DroneEdgeRuntime edge = _context.Graph.Edges[i];
                double dStart = Vector2d.Distance(drone.Position, edge.Start);
                if (dStart < bestDistance)
                {
                    bestDistance = dStart;
                    bestNodeId = edge.FromNode;
                }

                double dEnd = Vector2d.Distance(drone.Position, edge.End);
                if (dEnd < bestDistance)
                {
                    bestDistance = dEnd;
                    bestNodeId = edge.ToNode;
                }
            }

            return bestNodeId;
        }
    }
}
