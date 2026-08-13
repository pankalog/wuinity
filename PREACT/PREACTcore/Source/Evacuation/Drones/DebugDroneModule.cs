namespace PREACT.Evacuation
{
    public class DebugDroneModule : DroneModule
    {
        private readonly SwarmDroneModule _swarmFallback;

        public DebugDroneModule(Simulation simulation) : base(simulation)
        {
            _swarmFallback = new SwarmDroneModule(simulation, out bool success);
            if (success)
            {
                Engine.Message(simulation, Engine.LogType.Warning,
                    "[DroneScaffold] DebugDroneModule was instantiated. Delegating to SwarmDroneModule.");
            }
            else
            {
                Engine.Message(simulation, Engine.LogType.Warning,
                    "[DroneScaffold] DebugDroneModule fallback to SwarmDroneModule failed. Stack: " + System.Environment.StackTrace);
            }
        }

        public override bool IsSimulationDone()
        {
            return _swarmFallback != null && _swarmFallback.IsSimulationDone();
        }

        public override void Step(double simulationTime, double deltaTime)
        {
            _swarmFallback?.Step(simulationTime, deltaTime);
        }

        public override void Stop()
        {
            _swarmFallback?.Stop();
        }

        public bool TryGetDronePositions(out PREACT.Math.Vector2d[] dronePositions)
        {
            if (_swarmFallback != null)
            {
                return _swarmFallback.TryGetDronePositions(out dronePositions);
            }

            dronePositions = System.Array.Empty<PREACT.Math.Vector2d>();
            return false;
        }

        public bool TryGetDroneStates(out DroneAgentState[] droneStates)
        {
            if (_swarmFallback != null)
            {
                return _swarmFallback.TryGetDroneStates(out droneStates);
            }

            droneStates = System.Array.Empty<DroneAgentState>();
            return false;
        }

        public bool TryGetRasterGrid(out PREACT.Math.Vector2d min, out PREACT.Math.Vector2d max, out int rows, out int columns)
        {
            if (_swarmFallback != null)
            {
                return _swarmFallback.TryGetRasterGrid(out min, out max, out rows, out columns);
            }

            min = PREACT.Math.Vector2d.zero;
            max = PREACT.Math.Vector2d.zero;
            rows = 0;
            columns = 0;
            return false;
        }

        public bool TryGetRasterActiveMask(out bool[] activeMask)
        {
            if (_swarmFallback != null)
            {
                return _swarmFallback.TryGetRasterActiveMask(out activeMask);
            }

            activeMask = System.Array.Empty<bool>();
            return false;
        }
    }
}
