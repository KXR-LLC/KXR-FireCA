using System;

namespace FireCA
{
    /// <summary>The names of the Scenario validation checks. The Control Panel shows the name of a failed check.</summary>
    public enum ScenarioCheck
    {
        /// <summary>A Scenario file is missing or cannot be parsed.</summary>
        ScenarioFiles,
        /// <summary>Check 1. gridSize is 512, 1024, 2048, or 4096.</summary>
        GridSize,
        /// <summary>Check 2. cellSizeM is 2.0.</summary>
        CellSize,
        /// <summary>Check 3. The size of elevation.r32 equals gridSize * gridSize * 4 bytes.</summary>
        ElevationFileSize,
        /// <summary>Check 4. Every elevation value is finite and is between -500 and 9000.</summary>
        ElevationValues,
        /// <summary>Check 5. The elevation range is less than 3000 m.</summary>
        ElevationRange,
        /// <summary>Check 6. The width and height of fuel.png equal gridSize.</summary>
        FuelImageSize,
        /// <summary>Check 7. The wind array has at least one entry, and the hour values increase.</summary>
        WindEntries,
        /// <summary>Check 8. tempMaxC is greater than tempMinC.</summary>
        TemperatureRange,
        /// <summary>Check 9. Every moisture value is between 1 and 300.</summary>
        MoistureValues,
        /// <summary>Check 10. Every ignition circle lies inside the Fire Grid.</summary>
        IgnitionInsideGrid,
    }

    public sealed class ScenarioException : Exception
    {
        public ScenarioCheck Check { get; }

        public ScenarioException(ScenarioCheck check, string message) : base(message)
        {
            Check = check;
        }

        /// <summary>The text that the Control Panel shows.</summary>
        public string DisplayText => $"Scenario rejected by check {Check}. {Message}";
    }

    /// <summary>The validation checks of docs/01-data-layers.md. The class has no Unity dependency.</summary>
    public static class ScenarioValidation
    {
        public const float MinElevationM = -500f;
        public const float MaxElevationM = 9000f;
        public const float MaxElevationRangeM = 3000f;

        /// <summary>Checks 1 and 2.</summary>
        public static void CheckGrid(ScenarioData d)
        {
            if (!GridSpec.IsValidFireSize(d.gridSize))
                throw new ScenarioException(ScenarioCheck.GridSize,
                    $"gridSize is {d.gridSize}. The value must be 512, 1024, 2048, or 4096.");
            if (d.cellSizeM != GridSpec.FireCellSizeM)
                throw new ScenarioException(ScenarioCheck.CellSize,
                    $"cellSizeM is {d.cellSizeM}. The value must be 2.0.");
        }

        /// <summary>Check 3.</summary>
        public static void CheckElevationFileSize(ScenarioData d, long fileBytes)
        {
            long expected = (long)d.gridSize * d.gridSize * 4;
            if (fileBytes != expected)
                throw new ScenarioException(ScenarioCheck.ElevationFileSize,
                    $"{d.elevationFile} has {fileBytes} bytes. The size must be {expected} bytes.");
        }

        /// <summary>Checks 4 and 5. Returns the lowest and the highest elevation.</summary>
        public static void CheckElevationValues(float[] elevation, out float min, out float max)
        {
            min = float.PositiveInfinity;
            max = float.NegativeInfinity;
            for (int i = 0; i < elevation.Length; i++)
            {
                float v = elevation[i];
                // The comparison is false for NaN, so NaN fails the check.
                if (!(v >= MinElevationM && v <= MaxElevationM))
                    throw new ScenarioException(ScenarioCheck.ElevationValues,
                        $"The elevation value at index {i} is {v}. Every value must be finite and between -500 and 9000.");
                if (v < min) min = v;
                if (v > max) max = v;
            }
            if (!(max - min < MaxElevationRangeM))
                throw new ScenarioException(ScenarioCheck.ElevationRange,
                    $"The elevation range is {max - min:F0} m. The range must be less than 3000 m.");
        }

        /// <summary>Check 6.</summary>
        public static void CheckFuelImageSize(ScenarioData d, int width, int height)
        {
            if (width != d.gridSize || height != d.gridSize)
                throw new ScenarioException(ScenarioCheck.FuelImageSize,
                    $"{d.fuelFile} is {width} x {height} pixels. Each side must be {d.gridSize} pixels.");
        }

        /// <summary>Checks 7 to 10.</summary>
        public static void CheckSettings(ScenarioData d)
        {
            var wind = d.weather != null ? d.weather.wind : null;
            if (wind == null || wind.Length < 1)
                throw new ScenarioException(ScenarioCheck.WindEntries, "The wind array must have at least one entry.");
            for (int i = 1; i < wind.Length; i++)
                if (!(wind[i].hour > wind[i - 1].hour))
                    throw new ScenarioException(ScenarioCheck.WindEntries,
                        $"The hour of wind entry {i} is {wind[i].hour}. The hour values must increase.");

            if (!(d.weather.tempMaxC > d.weather.tempMinC))
                throw new ScenarioException(ScenarioCheck.TemperatureRange,
                    $"tempMaxC is {d.weather.tempMaxC} and tempMinC is {d.weather.tempMinC}. tempMaxC must be greater than tempMinC.");

            var m = d.moisture ?? new ScenarioData.MoistureData();
            CheckMoisture("m1Pct", m.m1Pct);
            CheckMoisture("m10Pct", m.m10Pct);
            CheckMoisture("m100Pct", m.m100Pct);
            CheckMoisture("livePct", m.livePct);

            float extent = d.gridSize * d.cellSizeM;
            var ignitions = d.ignitions ?? Array.Empty<ScenarioData.IgnitionData>();
            for (int i = 0; i < ignitions.Length; i++)
            {
                var ig = ignitions[i];
                bool inside = ig.radiusM >= 0f
                    && ig.xM - ig.radiusM >= 0f && ig.xM + ig.radiusM <= extent
                    && ig.zM - ig.radiusM >= 0f && ig.zM + ig.radiusM <= extent;
                if (!inside)
                    throw new ScenarioException(ScenarioCheck.IgnitionInsideGrid,
                        $"Ignition {i} at ({ig.xM}, {ig.zM}) with radius {ig.radiusM} m is not inside the Fire Grid of {extent} m.");
            }
        }

        static void CheckMoisture(string field, float pct)
        {
            if (!(pct >= 1f && pct <= 300f))
                throw new ScenarioException(ScenarioCheck.MoistureValues,
                    $"{field} is {pct}. Every moisture value must be between 1 and 300.");
        }
    }
}
