using System;
using System.Collections.Generic;
using System.IO;
using PREACT.Input;
using PREACT.Math;
using PREACT.Traffic;

namespace PREACT.Evacuation
{
    public class SwarmDroneModule : DroneModule
    {
        private const double ArrivalToleranceMeters = 0.5;

        private readonly DroneModuleInput _moduleInput = new DroneModuleInput();
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

        public SwarmDroneModule(Simulation simulation, out bool success) : base(simulation)
        {
            success = true;
            _diagnosticsIntervalSeconds = _moduleInput.Policy.DiagnosticsIntervalSeconds;
            _diagnosticsTimer = 0.0;
            _chargingStation = ResolveChargingStation(_moduleInput.Fleet, _simulation.Input.Simulation.DomainSize);

            BuildGraphFromSumo();

            BuildDroneAgents();
            BuildPolicy();
            UpdateDroneSnapshots();

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
            AdvanceDroneStates(deltaTime);
            AssignTargetsIfNeeded(simulationTime);
            UpdateDroneSnapshots();
            UpdateDiagnostics(deltaTime, simulationTime);
        }

        public override void Stop()
        {
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

                    DroneEdgeRuntime runtimeEdge = new DroneEdgeRuntime(edge.Id, edge.From, edge.To, start, end, length);
                    _graph.AddEdge(runtimeEdge);
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
            for (int i = 0; i < _moduleInput.Fleet.DroneCount; ++i)
            {
                DroneAgentRuntime drone = new DroneAgentRuntime(i, _moduleInput.Fleet.UniformTypeId, _chargingStation, _moduleInput.Fleet.CruiseSpeedMps);
                _drones.Add(drone);
            }
        }

        private void BuildPolicy()
        {
            if (_moduleInput.Policy.RoutingPolicy == DronePolicyInput.RoutingPolicies.ACS)
            {
                _policy = new AcsRoutingPolicy();
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
            if (drone.CurrentTask != null)
            {
                _policy.OnTaskCompleted(drone, drone.CurrentTask, _simulation.Time.SimulationTime);
            }

            drone.CurrentTask = null;
            drone.State = DroneAgentState.Idle;
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
