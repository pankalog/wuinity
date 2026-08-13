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
        public string EdgeId;
        public double EdgeLengthMeters;
        public int LaneCount;
        public bool IncludeBidiEdgeInCounts;
        public double AccumulatedVehicleSeconds;
        public double AccumulatedSeconds;
        public double LastMeasuredDensityPerLane;

        // Axis-aligned area the scan covers, in domain-local simulation meters. Set by the
        // raster policy to the bounds of the cell being scanned; used to count the vehicles
        // physically inside that cell during the scan. Unused (zero) for edge scans.
        public Vector2d ScanAreaMin;
        public Vector2d ScanAreaMax;
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
