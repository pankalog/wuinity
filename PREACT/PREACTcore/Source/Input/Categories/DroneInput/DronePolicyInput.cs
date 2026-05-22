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

            return newInput;
        }
    }
}
