using PREACT.Input;
using PREACT.Math;

namespace PREACT.Evacuation
{
    public class AcsRoutingPolicy : IDroneRoutingPolicy
    {
        private DroneRoadGraphRuntime _graph;
        private DronePolicyInput _policyInput;
        private AcsPolicyInput _acsInput;

        public string Name { get => nameof(DronePolicyInput.RoutingPolicies.ACS); }

        public void Initialize(DroneRoutingContext context)
        {
            _graph = context.Graph;
            _policyInput = context.PolicyInput;
            _acsInput = context.AcsInput;
        }

        public void OnSimulationStep(double simulationTime, double deltaTime)
        {
        }

        public bool TryAssignTask(DroneAgentRuntime drone, double simulationTime, out DroneTaskRuntime task)
        {
            task = null;
            if (_graph == null || _graph.EdgeCount == 0)
            {
                return false;
            }

            int pickedEdgeIndex = -1;
            double bestScore = double.MinValue;

            for (int i = 0; i < _graph.Edges.Count; ++i)
            {
                DroneEdgeRuntime edge = _graph.Edges[i];

                double stalenessTerm = System.Math.Max(0.0, edge.StalenessSeconds) * _policyInput.StalenessWeight + 1.0;
                double distance = DistanceToEdge(drone.Position, edge);
                double distanceTerm = 1.0 / (1.0 + distance);

                double score = System.Math.Pow(stalenessTerm, _acsInput.Alpha) * System.Math.Pow(distanceTerm, _acsInput.Beta);

                if (score > bestScore)
                {
                    bestScore = score;
                    pickedEdgeIndex = i;
                }
            }

            if (pickedEdgeIndex < 0)
            {
                return false;
            }

            DroneEdgeRuntime selectedEdge = _graph.Edges[pickedEdgeIndex];
            Vector2d scanStart = Vector2d.Distance(drone.Position, selectedEdge.Start) <= Vector2d.Distance(drone.Position, selectedEdge.End) ? selectedEdge.Start : selectedEdge.End;
            Vector2d scanEnd = Vector2d.Distance(scanStart, selectedEdge.Start) <= 0.001 ? selectedEdge.End : selectedEdge.Start;

            task = new DroneTaskRuntime
            {
                TaskType = DroneTaskType.EdgeScan,
                TaskIndex = pickedEdgeIndex,
                TransitTarget = scanStart,
                ScanStart = scanStart,
                ScanEnd = scanEnd
            };

            return true;
        }

        public void OnTaskCompleted(DroneAgentRuntime drone, DroneTaskRuntime task, double simulationTime) { }

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

        private static double DistanceToEdge(Vector2d point, DroneEdgeRuntime edge)
        {
            double distanceToStart = Vector2d.Distance(point, edge.Start);
            double distanceToEnd = Vector2d.Distance(point, edge.End);
            return System.Math.Min(distanceToStart, distanceToEnd);
        }
    }
}
