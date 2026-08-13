using System.Collections.Generic;

namespace PREACT.Input
{
    [System.Serializable]
    public class DronePolicyInput
    {
        public enum RoutingPolicies { Raster, ACS, AcoStigmergic }

        public RoutingPolicies RoutingPolicy = RoutingPolicies.Raster;
        public bool TreatEdgesAsUndirected = true;
        public bool MarkScannedOnFullTraversal = true;
        public bool AllowOffRoadTransit = true;
        public bool AllowEdgeOverlap = true;
        public double DiagnosticsIntervalSeconds = 10.0;
        public double StalenessWeight = 1.0;
        public double VehicleDensityWeight = 0.0;

        // SUMO networks imported from OSM often contain pairs of junctions within a few
        // metres of each other carrying different IDs (cleanup artefacts at complex
        // intersections, network-clipping boundaries, etc.). When > 0, junctions whose
        // endpoint coordinates are within this many metres of each other are merged
        // into a single canonical node id before connected-component analysis. Default
        // 0 keeps strict ID-based adjacency. Only used as a supplement when SUMO
        // <connection> data is unavailable or sparse.
        public double EdgeMergeToleranceMeters = 0.0;

        // Minimum component size (in edges) to keep after connectivity analysis.
        // Default 1 keeps every component (full network, no filtering). Set to a small
        // positive value (e.g. 3) to drop tiny noise stubs while retaining real
        // sub-networks. Set to 0 to fall back to legacy behaviour (keep largest only).
        public int MinComponentEdges = 1;

        public static DronePolicyInput Parse(string[] inputLines, int startIndex, out bool success)
        {
            success = true;
            DronePolicyInput newInput = new DronePolicyInput();
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string nameOfInput, userInput;

            nameOfInput = nameof(RoutingPolicy);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                switch (userInput)
                {
                    case nameof(RoutingPolicies.Raster):
                        newInput.RoutingPolicy = RoutingPolicies.Raster;
                        break;
                    case nameof(RoutingPolicies.ACS):
                        newInput.RoutingPolicy = RoutingPolicies.ACS;
                        break;
                    case nameof(RoutingPolicies.AcoStigmergic):
                        newInput.RoutingPolicy = RoutingPolicies.AcoStigmergic;
                        break;
                    default:
                        Engine.Message(null, Engine.LogType.Warning, $"Unknown {nameOfInput}={userInput}, using default {newInput.RoutingPolicy}.");
                        break;
                }
            }

            nameOfInput = nameof(TreatEdgesAsUndirected);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                bool.TryParse(userInput, out newInput.TreatEdgesAsUndirected);
            }

            nameOfInput = nameof(MarkScannedOnFullTraversal);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                bool.TryParse(userInput, out newInput.MarkScannedOnFullTraversal);
            }

            nameOfInput = nameof(AllowOffRoadTransit);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                bool.TryParse(userInput, out newInput.AllowOffRoadTransit);
            }

            nameOfInput = nameof(AllowEdgeOverlap);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                bool.TryParse(userInput, out newInput.AllowEdgeOverlap);
            }

            nameOfInput = nameof(DiagnosticsIntervalSeconds);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (double.TryParse(userInput, out double value) && value > 0.0)
                {
                    newInput.DiagnosticsIntervalSeconds = value;
                }
                else
                {
                    Engine.Message(null, Engine.LogType.Warning, $"{nameOfInput} must be > 0, using default {newInput.DiagnosticsIntervalSeconds}.");
                }
            }

            nameOfInput = nameof(StalenessWeight);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (double.TryParse(userInput, out double value) && value >= 0.0)
                {
                    newInput.StalenessWeight = value;
                }
            }

            nameOfInput = nameof(VehicleDensityWeight);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (double.TryParse(userInput, out double value) && value >= 0.0)
                {
                    newInput.VehicleDensityWeight = value;
                }
            }

            nameOfInput = nameof(EdgeMergeToleranceMeters);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (double.TryParse(userInput, out double value) && value >= 0.0)
                {
                    newInput.EdgeMergeToleranceMeters = value;
                }
                else
                {
                    Engine.Message(null, Engine.LogType.Warning, $"{nameOfInput} must be >= 0, using default {newInput.EdgeMergeToleranceMeters}.");
                }
            }

            nameOfInput = nameof(MinComponentEdges);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (int.TryParse(userInput, out int value) && value >= 0)
                {
                    newInput.MinComponentEdges = value;
                }
                else
                {
                    Engine.Message(null, Engine.LogType.Warning, $"{nameOfInput} must be >= 0, using default {newInput.MinComponentEdges}.");
                }
            }

            return newInput;
        }
    }
}
