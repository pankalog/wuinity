using System.Collections.Generic;
using PREACT.Input;
using PREACT.Math;

namespace PREACT.Evacuation
{
    public enum DroneTaskType { None, RasterScan, EdgeScan }

    public class DroneTaskRuntime
    {
        public DroneTaskType TaskType;
        public int TaskIndex;
        public Vector2d TransitTarget;
        public Vector2d ScanStart;
        public Vector2d ScanEnd;
    }

    public class DroneRoutingContext
    {
        public DroneRoadGraphRuntime Graph;
        public DronePolicyInput PolicyInput;
        public AcsPolicyInput AcsInput;
        public RasterPolicyInput RasterInput;
        public AcoStigmergicPolicyInput AcoInput;
        public Vector2d DomainSize;
        public List<(Vector2d start, Vector2d end)> RoadSegments;
        public int DroneCount;
    }
}
