using System.Collections.Generic;

namespace PREACT.Input
{
    [System.Serializable]
    public class AcsPolicyInput
    {
        public enum HeuristicModes { PureDistance }

        public double Alpha = 1.0;
        public double Beta = 2.0;
        public double EvaporationRate = 0.2;
        public bool OneAntPerDrone = true;
        public bool NextEdgeOnly = true;
        public bool AgeUpAndResetOnScan = true;
        public HeuristicModes HeuristicMode = HeuristicModes.PureDistance;

        public static AcsPolicyInput Parse(string[] inputLines, int startIndex, out bool success)
        {
            success = true;
            AcsPolicyInput newInput = new AcsPolicyInput();
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string nameOfInput, userInput;

            nameOfInput = nameof(Alpha);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (double.TryParse(userInput, out double value) && value > 0.0)
                {
                    newInput.Alpha = value;
                }
            }

            nameOfInput = nameof(Beta);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (double.TryParse(userInput, out double value) && value > 0.0)
                {
                    newInput.Beta = value;
                }
            }

            nameOfInput = nameof(EvaporationRate);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (double.TryParse(userInput, out double value) && value >= 0.0 && value <= 1.0)
                {
                    newInput.EvaporationRate = value;
                }
            }

            nameOfInput = nameof(OneAntPerDrone);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                bool.TryParse(userInput, out newInput.OneAntPerDrone);
            }

            nameOfInput = nameof(NextEdgeOnly);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                bool.TryParse(userInput, out newInput.NextEdgeOnly);
            }

            nameOfInput = nameof(AgeUpAndResetOnScan);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                bool.TryParse(userInput, out newInput.AgeUpAndResetOnScan);
            }

            nameOfInput = nameof(HeuristicMode);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                switch (userInput)
                {
                    case nameof(HeuristicModes.PureDistance):
                        newInput.HeuristicMode = HeuristicModes.PureDistance;
                        break;
                    default:
                        Engine.Message(null, Engine.LogType.Warning, $"Unknown {nameOfInput}={userInput}, using default {newInput.HeuristicMode}.");
                        break;
                }
            }

            return newInput;
        }
    }
}
