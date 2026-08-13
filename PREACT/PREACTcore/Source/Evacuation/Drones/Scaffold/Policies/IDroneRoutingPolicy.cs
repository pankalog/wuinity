using PREACT.Input;
using PREACT.Math;

namespace PREACT.Evacuation
{
    public interface IDroneRoutingPolicy
    {
        string Name { get; }
        void Initialize(DroneRoutingContext context);
        void OnSimulationStep(double simulationTime, double deltaTime);
        bool TryAssignTask(DroneAgentRuntime drone, double simulationTime, out DroneTaskRuntime task);
        void OnTaskStarted(DroneAgentRuntime drone, DroneTaskRuntime task, double simulationTime);
        void OnTaskCompleted(DroneAgentRuntime drone, DroneTaskRuntime task, double simulationTime, double measuredDensityPerLane);
        void OnTaskAborted(DroneAgentRuntime drone, DroneTaskRuntime task, double simulationTime);
        bool TryGetRasterGrid(out Vector2d min, out Vector2d max, out int rows, out int columns);
        bool TryGetRasterActiveMask(out bool[] activeMask);
    }
}
