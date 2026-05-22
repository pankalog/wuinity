using System.Collections.Generic;

namespace PREACT.Input
{
    [System.Serializable]
    public class RasterPolicyInput
    {
        public bool UseDroneOwnedGrid = true;
        public bool PreferRoadIntersectingCells = true;
        public double CellSizeMeters = 250.0;

        public static RasterPolicyInput Parse(string[] inputLines, int startIndex, out bool success)
        {
            success = true;
            RasterPolicyInput newInput = new RasterPolicyInput();
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string userInput;

            if (inputToParse.TryGetValue(nameof(UseDroneOwnedGrid), out userInput))
            {
                bool.TryParse(userInput, out newInput.UseDroneOwnedGrid);
            }

            if (inputToParse.TryGetValue(nameof(PreferRoadIntersectingCells), out userInput))
            {
                bool.TryParse(userInput, out newInput.PreferRoadIntersectingCells);
            }

            if (inputToParse.TryGetValue(nameof(CellSizeMeters), out userInput))
            {
                if (double.TryParse(userInput, out double value) && value > 0.0)
                {
                    newInput.CellSizeMeters = value;
                }
            }

            return newInput;
        }
    }
}
