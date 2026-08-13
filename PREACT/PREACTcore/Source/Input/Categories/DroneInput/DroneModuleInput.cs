using System.Collections.Generic;

namespace PREACT.Input
{
    [System.Serializable]
    public class DroneModuleInput
    {
        public enum DroneModules { SwarmScaffold }

        public bool Enabled = false;
        public DroneModules Module = DroneModules.SwarmScaffold;
        public bool EnableTelemetryOutput = true;
        public double TelemetryIntervalSeconds = 1.0;
        public bool TelemetryIncludeEdgePheromones = true;
        public DroneFleetInput Fleet = new DroneFleetInput();
        public DronePolicyInput Policy = new DronePolicyInput();
        public AcsPolicyInput AcsPolicy = new AcsPolicyInput();
        public RasterPolicyInput RasterPolicy = new RasterPolicyInput();
        public AcoStigmergicPolicyInput AcoStigmergicPolicy = new AcoStigmergicPolicyInput();

        public void Parse(string[] inputLines, int startIndex, Dictionary<string, int> headerLineIndex, out bool success)
        {
            success = true;
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string nameOfInput, userInput;

            nameOfInput = nameof(Enabled);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                bool.TryParse(userInput, out Enabled);
            }
            else
            {
                PREACTInput.InputNotFoundMessage(nameOfInput);
                Enabled = false;
            }

            if (!Enabled)
            {
                success = true;
                return;
            }

            nameOfInput = nameof(Module);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                switch (userInput)
                {
                    case nameof(DroneModules.SwarmScaffold):
                        Module = DroneModules.SwarmScaffold;
                        break;
                    default:
                        Engine.Message(null, Engine.LogType.Warning, $"Unknown {nameOfInput}={userInput}, using default {Module}.");
                        break;
                }
            }

            nameOfInput = nameof(EnableTelemetryOutput);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                bool.TryParse(userInput, out EnableTelemetryOutput);
            }

            nameOfInput = nameof(TelemetryIntervalSeconds);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (double.TryParse(userInput, out double value) && value > 0.0)
                {
                    TelemetryIntervalSeconds = value;
                }
                else
                {
                    Engine.Message(null, Engine.LogType.Warning, $"{nameOfInput} must be > 0, using default {TelemetryIntervalSeconds}.");
                }
            }

            nameOfInput = nameof(TelemetryIncludeEdgePheromones);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                bool.TryParse(userInput, out TelemetryIncludeEdgePheromones);
            }

            if (headerLineIndex.TryGetValue(nameof(DroneFleet), out int lineIndex))
            {
                PREACTInput.ReadingInputMessage(nameof(DroneFleet));
                Fleet = DroneFleetInput.Parse(inputLines, lineIndex, out success);
                if (!success)
                {
                    return;
                }
            }
            else
            {
                Engine.Message(null, Engine.LogType.Warning, $"{nameof(DroneFleet)} header not found, using defaults.");
            }

            if (headerLineIndex.TryGetValue(nameof(DronePolicy), out lineIndex))
            {
                PREACTInput.ReadingInputMessage(nameof(DronePolicy));
                Policy = DronePolicyInput.Parse(inputLines, lineIndex, out success);
                if (!success)
                {
                    return;
                }
            }
            else
            {
                Engine.Message(null, Engine.LogType.Warning, $"{nameof(DronePolicy)} header not found, using defaults.");
            }

            if (headerLineIndex.TryGetValue(AcsPolicyHeader, out lineIndex))
            {
                PREACTInput.ReadingInputMessage(AcsPolicyHeader);
                AcsPolicy = AcsPolicyInput.Parse(inputLines, lineIndex, out success);
                if (!success)
                {
                    return;
                }
            }

            if (headerLineIndex.TryGetValue(RasterPolicyHeader, out lineIndex))
            {
                PREACTInput.ReadingInputMessage(RasterPolicyHeader);
                RasterPolicy = RasterPolicyInput.Parse(inputLines, lineIndex, out success);
                if (!success)
                {
                    return;
                }
            }

            if (headerLineIndex.TryGetValue(AcoStigmergicPolicyHeader, out lineIndex))
            {
                PREACTInput.ReadingInputMessage(AcoStigmergicPolicyHeader);
                AcoStigmergicPolicy = AcoStigmergicPolicyInput.Parse(inputLines, lineIndex, out success);
                if (!success)
                {
                    return;
                }
            }

            success = true;

            Engine.Message(null, Engine.LogType.Log,
                $"[DroneScaffold][Input] DroneModule parsed: Enabled={Enabled}, Module={Module}, DroneCount={Fleet.DroneCount}, RoutingPolicy={Policy.RoutingPolicy}, Telemetry={EnableTelemetryOutput}, TelemetryInterval={TelemetryIntervalSeconds:0.###}s");
        }

        private const string DroneFleet = "DroneFleet";
        private const string DronePolicy = "DronePolicy";
        private const string AcsPolicyHeader = "AcsPolicy";
        private const string RasterPolicyHeader = "RasterPolicy";
        private const string AcoStigmergicPolicyHeader = "AcoStigmergicPolicy";
    }
}
