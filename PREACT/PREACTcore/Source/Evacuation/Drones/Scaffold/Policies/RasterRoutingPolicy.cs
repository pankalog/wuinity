using System;
using System.Collections.Generic;
using PREACT.Input;
using PREACT.Math;

namespace PREACT.Evacuation
{
    public class RasterRoutingPolicy : IDroneRoutingPolicy
    {
        private const double MinCellSizeMeters = 25.0;

        private readonly List<RasterCellRuntime> _rasterCells = new List<RasterCellRuntime>();
        private readonly Dictionary<int, List<int>> _droneOwnedRasterCells = new Dictionary<int, List<int>>();
        private readonly Dictionary<int, int> _droneRasterCursor = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _activeRasterCellByGridIndex = new Dictionary<int, int>();
        private bool[] _rasterActiveGridMask = Array.Empty<bool>();

        private Vector2d _rasterGridMin;
        private Vector2d _rasterGridMax;
        private int _rasterGridRows;
        private int _rasterGridColumns;
        private int _rasterGridTotalCells;
        private double _rasterCellSizeMeters;
        private DroneRoutingContext _context;

        public string Name { get => nameof(DronePolicyInput.RoutingPolicies.Raster); }

        public void Initialize(DroneRoutingContext context)
        {
            _context = context;
            _rasterCellSizeMeters = Mathd.Max(MinCellSizeMeters, context.RasterInput.CellSizeMeters);
            BuildRasterCoverageGrid();
        }

        public void OnSimulationStep(double simulationTime, double deltaTime)
        {
            for (int i = 0; i < _rasterCells.Count; ++i)
            {
                _rasterCells[i].StalenessSeconds += deltaTime;
            }
        }

        public bool TryAssignTask(DroneAgentRuntime drone, double simulationTime, out DroneTaskRuntime task)
        {
            task = null;
            if (!_droneOwnedRasterCells.TryGetValue(drone.AgentId, out List<int> ownedCells) || ownedCells.Count == 0)
            {
                return false;
            }

            if (!_droneRasterCursor.TryGetValue(drone.AgentId, out int cursor) || cursor < 0 || cursor >= ownedCells.Count)
            {
                cursor = 0;
            }

            int selectedCell = ownedCells[cursor];
            _droneRasterCursor[drone.AgentId] = (cursor + 1) % ownedCells.Count;

            RasterCellRuntime cell = _rasterCells[selectedCell];
            bool leftToRight = (cell.Row % 2) == 0;
            double scanY = cell.Center.y;
            Vector2d left = new Vector2d(cell.Min.x, scanY);
            Vector2d right = new Vector2d(cell.Max.x, scanY);
            Vector2d scanStart = leftToRight ? left : right;
            Vector2d scanEnd = leftToRight ? right : left;

            task = new DroneTaskRuntime
            {
                TaskType = DroneTaskType.RasterScan,
                TaskIndex = selectedCell,
                TransitTarget = scanStart,
                ScanStart = scanStart,
                ScanEnd = scanEnd
            };
            return true;
        }

        public void OnTaskCompleted(DroneAgentRuntime drone, DroneTaskRuntime task, double simulationTime)
        {
            if (task == null || task.TaskType != DroneTaskType.RasterScan)
            {
                return;
            }

            int cellIndex = task.TaskIndex;
            if (cellIndex >= 0 && cellIndex < _rasterCells.Count)
            {
                _rasterCells[cellIndex].StalenessSeconds = 0.0;
            }
        }

        public bool TryGetRasterGrid(out Vector2d min, out Vector2d max, out int rows, out int columns)
        {
            min = _rasterGridMin;
            max = _rasterGridMax;
            rows = _rasterGridRows;
            columns = _rasterGridColumns;
            return rows > 0 && columns > 0;
        }

        public bool TryGetRasterActiveMask(out bool[] activeMask)
        {
            activeMask = _rasterActiveGridMask;
            return activeMask != null && activeMask.Length == _rasterGridTotalCells && _rasterGridTotalCells > 0;
        }

        private void BuildRasterCoverageGrid()
        {
            _rasterCells.Clear();
            _droneOwnedRasterCells.Clear();
            _droneRasterCursor.Clear();
            _activeRasterCellByGridIndex.Clear();
            _rasterActiveGridMask = Array.Empty<bool>();

            if (_context.DroneCount <= 0)
            {
                _rasterGridMin = Vector2d.zero;
                _rasterGridMax = Vector2d.zero;
                _rasterGridRows = 0;
                _rasterGridColumns = 0;
                return;
            }

            _rasterGridMin = Vector2d.zero;
            _rasterGridMax = _context.DomainSize;
            _rasterGridColumns = System.Math.Max(1, (int)System.Math.Ceiling(_context.DomainSize.x / _rasterCellSizeMeters));
            _rasterGridRows = System.Math.Max(1, (int)System.Math.Ceiling(_context.DomainSize.y / _rasterCellSizeMeters));
            _rasterGridTotalCells = _rasterGridRows * _rasterGridColumns;
            bool[] activeMask = new bool[_rasterGridTotalCells];

            bool filterByRoad = _context.RasterInput.PreferRoadIntersectingCells && _context.RoadSegments != null && _context.RoadSegments.Count > 0;

            for (int droneId = 0; droneId < _context.DroneCount; ++droneId)
            {
                _droneOwnedRasterCells[droneId] = new List<int>();
                _droneRasterCursor[droneId] = 0;
            }

            int cellIndex = 0;
            for (int row = 0; row < _rasterGridRows; ++row)
            {
                for (int col = 0; col < _rasterGridColumns; ++col)
                {
                    double x0 = col * _rasterCellSizeMeters;
                    double y0 = row * _rasterCellSizeMeters;
                    double x1 = System.Math.Min(_context.DomainSize.x, x0 + _rasterCellSizeMeters);
                    double y1 = System.Math.Min(_context.DomainSize.y, y0 + _rasterCellSizeMeters);

                    Vector2d cellMin = new Vector2d(x0, y0);
                    Vector2d cellMax = new Vector2d(x1, y1);
                    if (filterByRoad && !CellContainsRoadSegment(cellMin, cellMax))
                    {
                        continue;
                    }

                    int ownerDroneId = ResolveDroneOwnerForColumn(col, _rasterGridColumns, _context.DroneCount);
                    RasterCellRuntime cell = new RasterCellRuntime(cellIndex, row, col, ownerDroneId, cellMin, cellMax);
                    _rasterCells.Add(cell);
                    _droneOwnedRasterCells[ownerDroneId].Add(cellIndex);
                    int gridLinearIndex = row * _rasterGridColumns + col;
                    _activeRasterCellByGridIndex[gridLinearIndex] = cellIndex;
                    activeMask[gridLinearIndex] = true;
                    ++cellIndex;
                }
            }

            _rasterActiveGridMask = activeMask;

            for (int droneId = 0; droneId < _context.DroneCount; ++droneId)
            {
                List<int> ownedCells = _droneOwnedRasterCells[droneId];
                ownedCells.Sort((leftIndex, rightIndex) =>
                {
                    RasterCellRuntime left = _rasterCells[leftIndex];
                    RasterCellRuntime right = _rasterCells[rightIndex];
                    int rowComparison = left.Row.CompareTo(right.Row);
                    if (rowComparison != 0)
                    {
                        return rowComparison;
                    }

                    bool evenRow = (left.Row % 2) == 0;
                    return evenRow ? left.Column.CompareTo(right.Column) : right.Column.CompareTo(left.Column);
                });
            }
        }

        private bool CellContainsRoadSegment(Vector2d cellMin, Vector2d cellMax)
        {
            for (int i = 0; i < _context.RoadSegments.Count; ++i)
            {
                (Vector2d start, Vector2d end) = _context.RoadSegments[i];
                if (SegmentIntersectsRect(start, end, cellMin, cellMax))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SegmentIntersectsRect(Vector2d segmentStart, Vector2d segmentEnd, Vector2d rectMin, Vector2d rectMax)
        {
            double segmentMinX = System.Math.Min(segmentStart.x, segmentEnd.x);
            double segmentMaxX = System.Math.Max(segmentStart.x, segmentEnd.x);
            double segmentMinY = System.Math.Min(segmentStart.y, segmentEnd.y);
            double segmentMaxY = System.Math.Max(segmentStart.y, segmentEnd.y);

            if (segmentMaxX < rectMin.x || segmentMinX > rectMax.x || segmentMaxY < rectMin.y || segmentMinY > rectMax.y)
            {
                return false;
            }

            if (PointInRect(segmentStart, rectMin, rectMax) || PointInRect(segmentEnd, rectMin, rectMax))
            {
                return true;
            }

            Vector2d bottomLeft = new Vector2d(rectMin.x, rectMin.y);
            Vector2d bottomRight = new Vector2d(rectMax.x, rectMin.y);
            Vector2d topRight = new Vector2d(rectMax.x, rectMax.y);
            Vector2d topLeft = new Vector2d(rectMin.x, rectMax.y);

            return SegmentsIntersect(segmentStart, segmentEnd, bottomLeft, bottomRight)
                || SegmentsIntersect(segmentStart, segmentEnd, bottomRight, topRight)
                || SegmentsIntersect(segmentStart, segmentEnd, topRight, topLeft)
                || SegmentsIntersect(segmentStart, segmentEnd, topLeft, bottomLeft);
        }

        private static bool PointInRect(Vector2d point, Vector2d rectMin, Vector2d rectMax)
        {
            return point.x >= rectMin.x && point.x <= rectMax.x && point.y >= rectMin.y && point.y <= rectMax.y;
        }

        private static bool SegmentsIntersect(Vector2d p1, Vector2d p2, Vector2d q1, Vector2d q2)
        {
            const double epsilon = 1e-9;

            double o1 = Orientation(p1, p2, q1);
            double o2 = Orientation(p1, p2, q2);
            double o3 = Orientation(q1, q2, p1);
            double o4 = Orientation(q1, q2, p2);

            if ((o1 > epsilon && o2 < -epsilon || o1 < -epsilon && o2 > epsilon) &&
                (o3 > epsilon && o4 < -epsilon || o3 < -epsilon && o4 > epsilon))
            {
                return true;
            }

            if (System.Math.Abs(o1) <= epsilon && OnSegment(p1, q1, p2)) return true;
            if (System.Math.Abs(o2) <= epsilon && OnSegment(p1, q2, p2)) return true;
            if (System.Math.Abs(o3) <= epsilon && OnSegment(q1, p1, q2)) return true;
            if (System.Math.Abs(o4) <= epsilon && OnSegment(q1, p2, q2)) return true;

            return false;
        }

        private static double Orientation(Vector2d a, Vector2d b, Vector2d c)
        {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        private static bool OnSegment(Vector2d a, Vector2d b, Vector2d c)
        {
            return b.x >= System.Math.Min(a.x, c.x) && b.x <= System.Math.Max(a.x, c.x) &&
                   b.y >= System.Math.Min(a.y, c.y) && b.y <= System.Math.Max(a.y, c.y);
        }

        private static int ResolveDroneOwnerForColumn(int column, int totalColumns, int droneCount)
        {
            if (droneCount <= 1 || totalColumns <= 1)
            {
                return 0;
            }

            int owner = (int)((long)column * droneCount / totalColumns);
            return System.Math.Min(droneCount - 1, System.Math.Max(0, owner));
        }
    }
}
