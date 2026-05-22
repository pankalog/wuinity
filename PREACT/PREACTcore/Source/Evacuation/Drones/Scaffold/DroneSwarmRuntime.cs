using System.Collections.Generic;
using PREACT.Math;

namespace PREACT.Evacuation
{
    public enum DroneAgentState { Idle, Transit, ScanEdge, ReturnToCharge, Charging }

    public class DroneAgentRuntime
    {
        public int AgentId;
        public string TypeId;
        public DroneAgentState State;
        public Vector2d Position;
        public double BatteryFraction;
        public double CruiseSpeedMps;
        public int CurrentEdgeIndex;
        public int CurrentRasterCellIndex;
        public Vector2d TransitTarget;
        public Vector2d ScanStart;
        public Vector2d ScanEnd;
        public DroneTaskRuntime CurrentTask;

        public DroneAgentRuntime(int agentId, string typeId, Vector2d initialPosition, double cruiseSpeedMps)
        {
            AgentId = agentId;
            TypeId = typeId;
            State = DroneAgentState.Idle;
            Position = initialPosition;
            BatteryFraction = 1.0;
            CruiseSpeedMps = cruiseSpeedMps;
            CurrentEdgeIndex = -1;
            CurrentRasterCellIndex = -1;
            TransitTarget = initialPosition;
            ScanStart = initialPosition;
            ScanEnd = initialPosition;
            CurrentTask = null;
        }
    }

    public class RasterCellRuntime
    {
        public int CellIndex;
        public int Row;
        public int Column;
        public int OwnerDroneId;
        public Vector2d Min;
        public Vector2d Max;
        public Vector2d Center;
        public double StalenessSeconds;

        public RasterCellRuntime(int cellIndex, int row, int column, int ownerDroneId, Vector2d min, Vector2d max)
        {
            CellIndex = cellIndex;
            Row = row;
            Column = column;
            OwnerDroneId = ownerDroneId;
            Min = min;
            Max = max;
            Center = 0.5 * (min + max);
            StalenessSeconds = 0.0;
        }
    }

    public class DroneEdgeRuntime
    {
        public string EdgeId;
        public string FromNode;
        public string ToNode;
        public Vector2d Start;
        public Vector2d End;
        public double LengthMeters;
        public double StalenessSeconds;
        public double Pheromone;

        public DroneEdgeRuntime(string edgeId, string fromNode, string toNode, Vector2d start, Vector2d end, double lengthMeters)
        {
            EdgeId = edgeId;
            FromNode = fromNode;
            ToNode = toNode;
            Start = start;
            End = end;
            LengthMeters = lengthMeters;
            StalenessSeconds = 0.0;
            Pheromone = 0.0;
        }

        public Vector2d Midpoint()
        {
            return 0.5 * (Start + End);
        }
    }

    public class DroneRoadGraphRuntime
    {
        public List<DroneEdgeRuntime> Edges = new List<DroneEdgeRuntime>();
        public Dictionary<string, List<int>> NodeToEdgeIndices = new Dictionary<string, List<int>>();

        public int EdgeCount { get => Edges.Count; }

        public void AddEdge(DroneEdgeRuntime edge)
        {
            int edgeIndex = Edges.Count;
            Edges.Add(edge);
            RegisterNodeEdge(edge.FromNode, edgeIndex);
            RegisterNodeEdge(edge.ToNode, edgeIndex);
        }

        private void RegisterNodeEdge(string nodeId, int edgeIndex)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                return;
            }

            if (!NodeToEdgeIndices.TryGetValue(nodeId, out List<int> edges))
            {
                edges = new List<int>();
                NodeToEdgeIndices.Add(nodeId, edges);
            }

            edges.Add(edgeIndex);
        }
    }
}
