using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace FireCA
{
    /// <summary>The CPU content of one loaded Scenario.</summary>
    public sealed class ScenarioFiles
    {
        public string Id;
        public string Folder;
        public ScenarioData Data;
        /// <summary>Ground elevation, m. Row-major. Row 0 is the south edge.</summary>
        public float[] Elevation;
        /// <summary>Fuel codes. Row-major. Row 0 is the south edge.</summary>
        public byte[] Fuel;
        public float MinElevation;
        public float MaxElevation;
        public GridSpec Grid;
        /// <summary>The path of the Ground Image file, or null when the Scenario has none.</summary>
        public string GroundImagePath;
    }

    /// <summary>The Scenario loader. Reads and validates the three files of a Scenario folder.</summary>
    public static class ScenarioLoader
    {
        public const string JsonFileName = "scenario.json";

        public static string ScenariosRoot => Path.Combine(Application.streamingAssetsPath, "Scenarios");

        /// <summary>The Scenario identifiers under StreamingAssets/Scenarios.</summary>
        public static string[] ListScenarios()
        {
            if (!Directory.Exists(ScenariosRoot)) return Array.Empty<string>();
            var dirs = Directory.GetDirectories(ScenariosRoot);
            var ids = new System.Collections.Generic.List<string>();
            foreach (var dir in dirs)
                if (File.Exists(Path.Combine(dir, JsonFileName))) ids.Add(Path.GetFileName(dir));
            ids.Sort(StringComparer.Ordinal);
            return ids.ToArray();
        }

        /// <summary>Loads a Scenario folder. Throws a <see cref="ScenarioException"/> when a check fails.</summary>
        public static ScenarioFiles Load(string folder)
        {
            string jsonPath = Path.Combine(folder, JsonFileName);
            if (!File.Exists(jsonPath))
                throw new ScenarioException(ScenarioCheck.ScenarioFiles, $"{JsonFileName} is not in the folder '{folder}'.");

            ScenarioData data;
            try
            {
                data = JsonUtility.FromJson<ScenarioData>(File.ReadAllText(jsonPath));
            }
            catch (Exception e)
            {
                throw new ScenarioException(ScenarioCheck.ScenarioFiles, $"{JsonFileName} is not valid JSON. {e.Message}");
            }
            if (data == null)
                throw new ScenarioException(ScenarioCheck.ScenarioFiles, $"{JsonFileName} is empty.");

            ScenarioValidation.CheckGrid(data);

            string elevationPath = Path.Combine(folder, data.elevationFile ?? "");
            if (string.IsNullOrEmpty(data.elevationFile) || !File.Exists(elevationPath))
                throw new ScenarioException(ScenarioCheck.ScenarioFiles, $"The elevation file '{data.elevationFile}' is not in the folder.");
            ScenarioValidation.CheckElevationFileSize(data, new FileInfo(elevationPath).Length);

            string fuelPath = Path.Combine(folder, data.fuelFile ?? "");
            if (string.IsNullOrEmpty(data.fuelFile) || !File.Exists(fuelPath))
                throw new ScenarioException(ScenarioCheck.ScenarioFiles, $"The fuel file '{data.fuelFile}' is not in the folder.");

            var elevation = ReadFloats(elevationPath, data.gridSize * data.gridSize);
            ScenarioValidation.CheckElevationValues(elevation, out float min, out float max);

            byte[] png = File.ReadAllBytes(fuelPath);
            int width, height;
            try
            {
                PngCodec.ReadSize(png, out width, out height);
            }
            catch (FormatException e)
            {
                throw new ScenarioException(ScenarioCheck.ScenarioFiles, $"{data.fuelFile}: {e.Message}");
            }
            ScenarioValidation.CheckFuelImageSize(data, width, height);

            string groundImagePath = null;
            if (!string.IsNullOrEmpty(data.groundImageFile))
            {
                groundImagePath = Path.Combine(folder, data.groundImageFile);
                if (!File.Exists(groundImagePath))
                    throw new ScenarioException(ScenarioCheck.ScenarioFiles, $"The Ground Image file '{data.groundImageFile}' is not in the folder.");
            }
            ScenarioValidation.CheckSettings(data);

            byte[] fuel;
            try
            {
                // The top row of the image is the north edge, so the rows flip on load.
                fuel = PngCodec.DecodeRed(png, true, out _, out _);
            }
            catch (Exception e) when (e is FormatException || e is InvalidDataException)
            {
                throw new ScenarioException(ScenarioCheck.ScenarioFiles, $"{data.fuelFile}: {e.Message}");
            }

            return new ScenarioFiles
            {
                Id = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                Folder = folder,
                Data = data,
                Elevation = elevation,
                Fuel = fuel,
                MinElevation = min,
                MaxElevation = max,
                Grid = new GridSpec(data.gridSize, min),
                GroundImagePath = groundImagePath,
            };
        }

        static float[] ReadFloats(string path, int count)
        {
            var values = new float[count];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            Span<byte> bytes = MemoryMarshal.AsBytes(values.AsSpan());
            int total = 0;
            while (total < bytes.Length)
            {
                int n = stream.Read(bytes.Slice(total));
                if (n <= 0) break;
                total += n;
            }
            if (!BitConverter.IsLittleEndian)
            {
                for (int i = 0; i < count; i++)
                {
                    var b = BitConverter.GetBytes(values[i]);
                    Array.Reverse(b);
                    values[i] = BitConverter.ToSingle(b, 0);
                }
            }
            return values;
        }
    }
}
