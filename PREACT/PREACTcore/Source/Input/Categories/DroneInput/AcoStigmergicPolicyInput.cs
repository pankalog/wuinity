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
        public int RandomSeed = -1;

        // When false (default), the SUMO-reference-derived weights gamma_e and d_ref
        // are still loaded from GammaSourceFile and emitted in telemetry, but they
        // do NOT affect the on-line dynamics:
        //   - ApplyDecay uses gamma = 1 for every edge.
        //   - OnTaskCompleted deposits tau_v in raw veh/m/lane (no d_ref division).
        // Decisions become a function of only live tau_s/tau_v/tau_c and the user
        // hyperparameters (alpha, beta, delta, rho_*, Q_*) — no offline-world bias.
        // Set to true to restore the spec-faithful behaviour where gamma_e amplifies
        // decay rates and d_ref normalises tau_v deposits.
        public bool UseReferenceWeightsOnline = false;

        // Pheromone diffusion across the graph (graph Laplacian on the tau_v field).
        // Lets a deposit on one edge "leak" outward to its graph-neighbours, so distant
        // drones can feel a non-local gradient and orient toward known-busy regions
        // even when they're many hops away. Set DiffusionV=0 to disable entirely
        // (default — keeps decisions purely local in the classical-stigmergy sense).
        //
        // DiffusionV         — rate coefficient [1/s] for tau_v diffusion. Stability
        //                      requires DiffusionV * DiffusionStepSeconds < 0.5; the
        //                      runtime clamps to that and subdivides if violated.
        // DiffusionStepSeconds — how often the global diffusion pass runs. Smaller is
        //                       smoother but more expensive. 1.0 s is usually fine.
        public double DiffusionV = 0.0;
        public double DiffusionStepSeconds = 1.0;

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

            if (inputToParse.TryGetValue(nameof(RandomSeed), out userInput))
            {
                int.TryParse(userInput, out newInput.RandomSeed);
            }

            if (inputToParse.TryGetValue(nameof(UseReferenceWeightsOnline), out userInput))
            {
                bool.TryParse(userInput, out newInput.UseReferenceWeightsOnline);
            }

            if (inputToParse.TryGetValue(nameof(DiffusionV), out userInput))
            {
                if (double.TryParse(userInput, out double value) && value >= 0.0)
                {
                    newInput.DiffusionV = value;
                }
            }

            if (inputToParse.TryGetValue(nameof(DiffusionStepSeconds), out userInput))
            {
                if (double.TryParse(userInput, out double value) && value > 0.0)
                {
                    newInput.DiffusionStepSeconds = value;
                }
            }

            return newInput;
        }
    }
}
