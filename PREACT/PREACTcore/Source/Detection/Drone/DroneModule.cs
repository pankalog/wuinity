namespace PREACT.Evacuation
{
    public abstract class DroneModule : SimulationModule
    {
        protected DroneModule(Simulation simulation) : base(simulation)
        {
            // Engine.Message(_simulation, Engine.LogType.Log, "[DroneModule] Constructor called.");
        }

        public override bool IsSimulationDone()
        {
            // Engine.Message(_simulation, Engine.LogType.Log, "[DroneModule] IsSimulationDone called.");
            // Engine.Message(_simulation, Engine.LogType.Log, "[DroneModule] Test2");
            return false;
        }

        public override void Step(double simulationTime, double deltaTime)
        {
            // Engine.Message(_simulation, Engine.LogType.Log, $"[DroneModule] Step called. Time: {simulationTime}, Delta: {deltaTime}");
        }

        public override void Stop()
        {
            // Engine.Message(_simulation, Engine.LogType.Log, "[DroneModule] Stop called.");
        }
    }
}
