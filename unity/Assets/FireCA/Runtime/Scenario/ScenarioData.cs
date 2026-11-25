using System;
using UnityEngine;

namespace FireCA
{
    /// <summary>The content of a scenario.json file. Field names, types, and units follow docs/01-data-layers.md.</summary>
    [Serializable]
    public sealed class ScenarioData
    {
        public string name = "";
        public int gridSize;
        public float cellSizeM;
        public string elevationFile = "elevation.r32";
        public string fuelFile = "fuel.png";
        public float latitudeDeg;
        public int dayOfYear;
        public float startSolarHour;
        public WeatherData weather = new WeatherData();
        public MoistureData moisture = new MoistureData();
        public IgnitionData[] ignitions = Array.Empty<IgnitionData>();
        /// <summary>Optional. See docs/decisions.md, decision D3.</summary>
        public CameraPresetData[] cameraPresets = Array.Empty<CameraPresetData>();
        /// <summary>Optional named points of the terrain. See docs/decisions.md, decision D3.</summary>
        public LandmarkData[] landmarks = Array.Empty<LandmarkData>();

        [Serializable]
        public sealed class LandmarkData
        {
            public string name = "";
            public float xM;
            public float zM;
        }

        public const string LandmarkChuteBase = "chuteBase";
        public const string LandmarkChuteTop = "chuteTop";

        /// <summary>Finds a landmark by name. Returns false when the Scenario has no landmark with the name.</summary>
        public bool TryGetLandmark(string landmarkName, out Vector2 positionXZ)
        {
            positionXZ = default;
            if (landmarks == null) return false;
            foreach (var landmark in landmarks)
            {
                if (landmark == null || landmark.name != landmarkName) continue;
                positionXZ = new Vector2(landmark.xM, landmark.zM);
                return true;
            }
            return false;
        }

        [Serializable]
        public sealed class WeatherData
        {
            public float tempMaxC;
            public float tempMinC;
            public float rhMinPct;
            public float referenceElevationM;
            public float lapseRateCPerKm;
            public float stabilityKPerM;
            public WindEntry[] wind = Array.Empty<WindEntry>();
        }

        [Serializable]
        public sealed class WindEntry
        {
            public float hour;
            public float speedMps;
            public float fromDeg;
        }

        [Serializable]
        public sealed class MoistureData
        {
            public float m1Pct;
            public float m10Pct;
            public float m100Pct;
            public float livePct;
        }

        [Serializable]
        public sealed class IgnitionData
        {
            public float hour;
            public float xM;
            public float zM;
            public float radiusM;
        }

        [Serializable]
        public sealed class CameraPresetData
        {
            public string name = "";
            public Vector3 positionM;
            public Vector3 lookAtM;
        }

        /// <summary>The default values from the example in docs/01-data-layers.md.</summary>
        public static ScenarioData CreateDefault(string name, int gridSize)
        {
            return new ScenarioData
            {
                name = name,
                gridSize = gridSize,
                cellSizeM = 2.0f,
                elevationFile = "elevation.r32",
                fuelFile = "fuel.png",
                latitudeDeg = 37.0f,
                dayOfYear = 213,
                startSolarHour = 13.0f,
                weather = new WeatherData
                {
                    tempMaxC = 35.0f,
                    tempMinC = 16.0f,
                    rhMinPct = 12.0f,
                    referenceElevationM = 1200.0f,
                    lapseRateCPerKm = 6.5f,
                    stabilityKPerM = 0.003f,
                    wind = new[]
                    {
                        new WindEntry { hour = 0.0f, speedMps = 4.0f, fromDeg = 225.0f },
                        new WindEntry { hour = 3.0f, speedMps = 7.0f, fromDeg = 250.0f },
                    },
                },
                moisture = new MoistureData { m1Pct = 5.0f, m10Pct = 6.0f, m100Pct = 8.0f, livePct = 80.0f },
                ignitions = new[] { new IgnitionData { hour = 0.0f, xM = 4100.0f, zM = 1500.0f, radiusM = 6.0f } },
            };
        }
    }
}
