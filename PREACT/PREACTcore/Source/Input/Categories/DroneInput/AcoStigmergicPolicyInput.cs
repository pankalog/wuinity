using System.Collections.Generic;

namespace PREACT.Input
{
    [System.Serializable]
    public class AcoStigmergicPolicyInput
    {
        public double RhoS = 1.0e-2;
        public double RhoV = 5.0e-3;
        public double RhoC = 1.0e-1;
        public double Qs = 1.0;
        public double Qv = 1.0;
        public double Qc = 1.0;
        public double Alpha = 1.0;
        public double Beta = 1.0;
        public double Delta = 1.0;
        public bool UseBidiAggregation = true;
        public string GammaSourceFile = "_output/edge_density_1s.xml";
        public double DefaultGamma = 1.0;

        public static AcoStigmergicPolicyInput Parse(string[] inputLines, int startIndex, out bool success)
        {
            success = true;
            AcoStigmergicPolicyInput newInput = new AcoStigmergicPolicyInput();
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string userInput;

            if (inputToParse.TryGetValue(nameof(RhoS), out userInput))
            {
                if (double.TryParse(userInput, out double value) && value > 0.0)
                {
                    newInput.RhoS = value;
                }
            }

            if (inputToParse.TryGetValue(nameof(RhoV), out userInput))
            {
                if (double.TryParse(userInput, out double value) && value > 0.0)
                {
                    newInput.RhoV = value;
                }
            }

            if (inputToParse.TryGetValue(nameof(RhoC), out userInput))
            {
                if (double.TryParse(userInput, out double value) && value > 0.0)
                {
                    newInput.RhoC = value;
                }
            }

            if (inputToParse.TryGetValue(nameof(Qs), out userInput))
            {
                if (double.TryParse(userInput, out double value) && value >= 0.0)
                {
                    newInput.Qs = value;
                }
            }

            if (inputToParse.TryGetValue(nameof(Qv), out userInput))
            {
                if (double.TryParse(userInput, out double value) && value >= 0.0)
                {
                    newInput.Qv = value;
                }
            }

            if (inputToParse.TryGetValue(nameof(Qc), out userInput))
            {
                if (double.TryParse(userInput, out double value) && value >= 0.0)
                {
                    newInput.Qc = value;
                }
            }

            if (inputToParse.TryGetValue(nameof(Alpha), out userInput))
            {
                if (double.TryParse(userInput, out double value) && value >= 0.0)
                {
                    newInput.Alpha = value;
                }
            }

            if (inputToParse.TryGetValue(nameof(Beta), out userInput))
            {
                if (double.TryParse(userInput, out double value) && value >= 0.0)
                {
                    newInput.Beta = value;
                }
            }

            if (inputToParse.TryGetValue(nameof(Delta), out userInput))
            {
                if (double.TryParse(userInput, out double value) && value >= 0.0)
                {
                    newInput.Delta = value;
                }
            }

            if (inputToParse.TryGetValue(nameof(UseBidiAggregation), out userInput))
            {
                bool.TryParse(userInput, out newInput.UseBidiAggregation);
            }

            if (inputToParse.TryGetValue(nameof(GammaSourceFile), out userInput) && !string.IsNullOrWhiteSpace(userInput))
            {
                newInput.GammaSourceFile = userInput;
            }

            if (inputToParse.TryGetValue(nameof(DefaultGamma), out userInput))
            {
                if (double.TryParse(userInput, out double value) && value > 0.0)
                {
                    newInput.DefaultGamma = value;
                }
            }

            return newInput;
        }
    }
}
