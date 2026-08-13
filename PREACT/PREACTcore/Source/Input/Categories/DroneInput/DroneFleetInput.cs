using System.Collections.Generic;
using PREACT.Math;

namespace PREACT.Input
{
    [System.Serializable]
    public class DroneFleetInput
    {
        public int DroneCount = 5;
        public double CruiseSpeedMps = 15.0;
        public double BatteryEnduranceSeconds = 1200.0;
        public double ReturnReserveFraction = 0.10;
        public double RechargeDurationSeconds = 1200.0;
        public bool AutoPlaceChargingStationBottomLeft = true;
        public Vector2d ChargingStationSimulationPos = Vector2d.zero;
        public string UniformTypeId = "default";

        // When true, drones spawn at random points on the largest connected component
        // of the drone graph instead of all clustering at the dispatch entry. Useful
        // for testing the algorithm's performance from a uniform initial distribution.
        // RandomSpawnSeed >= 0 gives reproducible placement; < 0 uses a time-based seed.
        public bool RandomSpawnAcrossNetwork = false;
        public int RandomSpawnSeed = 0;

        public static DroneFleetInput Parse(string[] inputLines, int startIndex, out bool success)
        {
            success = true;
            DroneFleetInput newInput = new DroneFleetInput();
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string userInput;

            if (inputToParse.TryGetValue(nameof(DroneCount), out userInput))
            {
                if (int.TryParse(userInput, out int droneCount) && droneCount > 0)
                {
                    newInput.DroneCount = droneCount;
                }
                else
                {
                    Engine.Message(null, Engine.LogType.Warning, $"{nameof(DroneCount)} must be > 0, using default {newInput.DroneCount}.");
                }
            }

            if (inputToParse.TryGetValue(nameof(CruiseSpeedMps), out userInput))
            {
                if (double.TryParse(userInput, out double speed) && speed > 0.0)
                {
                    newInput.CruiseSpeedMps = speed;
                }
                else
                {
                    Engine.Message(null, Engine.LogType.Warning, $"{nameof(CruiseSpeedMps)} must be > 0, using default {newInput.CruiseSpeedMps}.");
                }
            }

            if (inputToParse.TryGetValue(nameof(BatteryEnduranceSeconds), out userInput))
            {
                if (double.TryParse(userInput, out double endurance) && endurance > 0.0)
                {
                    newInput.BatteryEnduranceSeconds = endurance;
                }
                else
                {
                    Engine.Message(null, Engine.LogType.Warning, $"{nameof(BatteryEnduranceSeconds)} must be > 0, using default {newInput.BatteryEnduranceSeconds}.");
                }
            }

            if (inputToParse.TryGetValue(nameof(ReturnReserveFraction), out userInput))
            {
                if (double.TryParse(userInput, out double reserve) && reserve >= 0.0 && reserve < 1.0)
                {
                    newInput.ReturnReserveFraction = reserve;
                }
                else
                {
                    Engine.Message(null, Engine.LogType.Warning, $"{nameof(ReturnReserveFraction)} must be in [0,1), using default {newInput.ReturnReserveFraction}.");
                }
            }

            if (inputToParse.TryGetValue(nameof(RechargeDurationSeconds), out userInput))
            {
                if (double.TryParse(userInput, out double chargeSeconds) && chargeSeconds > 0.0)
                {
                    newInput.RechargeDurationSeconds = chargeSeconds;
                }
                else
                {
                    Engine.Message(null, Engine.LogType.Warning, $"{nameof(RechargeDurationSeconds)} must be > 0, using default {newInput.RechargeDurationSeconds}.");
                }
            }

            if (inputToParse.TryGetValue(nameof(AutoPlaceChargingStationBottomLeft), out userInput))
            {
                bool.TryParse(userInput, out newInput.AutoPlaceChargingStationBottomLeft);
            }

            if (inputToParse.TryGetValue(nameof(ChargingStationSimulationPos), out userInput))
            {
                string[] data = userInput.Split(',');
                if (data.Length >= 2 && double.TryParse(data[0], out double x) && double.TryParse(data[1], out double y))
                {
                    newInput.ChargingStationSimulationPos = new Vector2d(x, y);
                }
                else
                {
                    Engine.Message(null, Engine.LogType.Warning, $"Could not parse {nameof(ChargingStationSimulationPos)}, using default {newInput.ChargingStationSimulationPos.x},{newInput.ChargingStationSimulationPos.y}.");
                }
            }

            if (inputToParse.TryGetValue(nameof(UniformTypeId), out userInput))
            {
                if (!string.IsNullOrWhiteSpace(userInput))
                {
                    newInput.UniformTypeId = userInput;
                }
            }

            if (inputToParse.TryGetValue(nameof(RandomSpawnAcrossNetwork), out userInput))
            {
                bool.TryParse(userInput, out newInput.RandomSpawnAcrossNetwork);
            }

            if (inputToParse.TryGetValue(nameof(RandomSpawnSeed), out userInput))
            {
                int.TryParse(userInput, out newInput.RandomSpawnSeed);
            }

            return newInput;
        }
    }
}
