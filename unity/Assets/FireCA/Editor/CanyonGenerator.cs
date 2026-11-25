using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;

namespace FireCA.Editor
{
    /// <summary>The output of one Canyon Generator run.</summary>
    public sealed class CanyonResult
    {
        public CanyonLayout Layout;
        public ScenarioData Data;
        /// <summary>World elevation, m. Row-major, row 0 is the south edge.</summary>
        public float[] Elevation;
        /// <summary>Fuel codes. Row-major, row 0 is the south edge.</summary>
        public byte[] Fuel;
        public int GridSize => Data.gridSize;
    }

    /// <summary>
    /// The Canyon Generator. Writes a synthetic Scenario from a settings asset and a seed.
    /// The same settings and the same seed produce the same files.
    /// </summary>
    public static class CanyonGenerator
    {
        public static CanyonResult Generate(CanyonGeneratorSettings settings)
        {
            if (!GridSpec.IsValidFireSize(settings.gridSize))
                throw new ArgumentException($"gridSize is {settings.gridSize}. The value must be 512, 1024, 2048, or 4096.");
            if (settings.noiseAmplitudeM >= 40f)
                throw new ArgumentException("noiseAmplitudeM must be under 40.");

            var layout = new CanyonLayout(settings);
            int n = settings.gridSize;
            double fullCell = GridSpec.FireCellSizeM / layout.Scale;   // full-scale meters per Fire Grid cell
            uint seed = (uint)settings.seed;

            var elevation = new float[n * n];
            var baseElevation = new float[n * n];
            var relativeHeight = new byte[n * n];
            var flags = new byte[n * n];        // 1: creek, 2: riparian, 4: chute
            double creekHalfFull = CanyonLayout.CreekHalfWidthWorldM / layout.Scale;
            double riparianFull = creekHalfFull + Math.Max(CanyonLayout.RiparianFullM, 8.0 / layout.Scale);

            Parallel.For(0, n, j =>
            {
                double z = (j + 0.5) * fullCell;
                for (int i = 0; i < n; i++)
                {
                    double x = (i + 0.5) * fullCell;
                    CanyonLayout.Sample s = layout.Evaluate(x, z);
                    double fade = 0.08 + 0.92 * Smoothstep(0.0, 0.2, s.RelativeHeight);
                    double amplitude = settings.noiseAmplitudeM * fade * (1.0 - 0.6 * s.ChuteCore);
                    double noise = amplitude * Fbm(x, z, seed, settings.noiseWavelengthM, 5);
                    int index = j * n + i;
                    elevation[index] = (float)layout.ToWorldElevation(s.BaseElevation + noise);
                    baseElevation[index] = (float)s.BaseElevation;
                    relativeHeight[index] = (byte)Math.Round(s.RelativeHeight * 255.0);
                    byte f = 0;
                    if (s.RelativeHeight <= 0.0 && s.AxisDistance <= creekHalfFull) f |= 1;
                    if (s.AxisDistance <= riparianFull) f |= 2;
                    if (s.InChute) f |= 4;
                    flags[index] = f;
                }
            });

            var fuel = new byte[n * n];
            Parallel.For(0, n, j =>
            {
                double z = (j + 0.5) * fullCell;
                int j0 = Math.Max(j - 1, 0), j1 = Math.Min(j + 1, n - 1);
                for (int i = 0; i < n; i++)
                {
                    int i0 = Math.Max(i - 1, 0), i1 = Math.Min(i + 1, n - 1);
                    int index = j * n + i;
                    double x = (i + 0.5) * fullCell;

                    // Slope of the final surface, with the differences of the Scenario loader.
                    double dhdx = (elevation[j * n + i1] - elevation[j * n + i0]) / ((i1 - i0) * GridSpec.FireCellSizeM);
                    double dhdz = (elevation[j1 * n + i] - elevation[j0 * n + i]) / ((j1 - j0) * GridSpec.FireCellSizeM);
                    double slope = Math.Sqrt(dhdx * dhdx + dhdz * dhdz);

                    // Aspect of the surface without noise: the azimuth that the slope faces.
                    double gx = baseElevation[j * n + i1] - baseElevation[j * n + i0];
                    double gz = baseElevation[j1 * n + i] - baseElevation[j0 * n + i];
                    double aspect = Math.Atan2(-gx, -gz) * 180.0 / Math.PI;
                    if (aspect < 0.0) aspect += 360.0;
                    // Ground with a slope under 5 percent has no aspect and counts as sunny (docs/decisions.md, decision D5).
                    double baseSlope = Math.Sqrt(
                        Sq(gx / ((i1 - i0) * fullCell)) + Sq(gz / ((j1 - j0) * fullCell)));
                    bool sunny = baseSlope < FlatSlope || (aspect >= 135.0 && aspect <= 315.0);

                    double rel = relativeHeight[index] / 255.0;
                    byte f = flags[index];
                    byte code;
                    if (slope > 1.2) code = FuelCodes.Barren;
                    else if ((f & 1) != 0) code = FuelCodes.Water;
                    else if ((f & 2) != 0) code = 9;
                    else if ((f & 4) != 0) code = 4;
                    else if (rel > 0.85) code = 2;
                    else if (sunny)
                    {
                        if (rel < 0.35) code = 1;
                        else code = Fbm(x, z, seed + 101u, settings.fuelPatchWavelengthM, 2) > 0.15 ? (byte)4 : (byte)5;
                    }
                    else
                    {
                        code = Fbm(x, z, seed + 202u, settings.fuelPatchWavelengthM, 2) > 0.15 ? (byte)8 : (byte)10;
                    }
                    fuel[index] = code;
                }
            });

            return new CanyonResult
            {
                Layout = layout,
                Data = BuildScenarioData(settings, layout),
                Elevation = elevation,
                Fuel = fuel,
            };
        }

        static ScenarioData BuildScenarioData(CanyonGeneratorSettings settings, CanyonLayout layout)
        {
            var data = ScenarioData.CreateDefault(settings.scenarioName, settings.gridSize);

            Vector2 ignition = layout.DefaultIgnition;
            data.ignitions = new[]
            {
                new ScenarioData.IgnitionData
                {
                    hour = 0.0f,
                    xM = Round1(layout.ToWorld(ignition.x)),
                    zM = Round1(layout.ToWorld(ignition.y)),
                    radiusM = 6.0f,
                },
            };

            // Camera presets (docs/decisions.md, decision D3).
            var s = settings;
            double extent = CanyonLayout.FullExtentM;
            double mid = extent * 0.5;
            double floorMid = layout.FloorElevation(mid);
            Vector2 chute = layout.ChuteBase, chuteDir = layout.ChuteDirection;
            double chuteFloor = layout.FloorElevation(s.chuteZM);
            // The Canyon Mouth view looks up the canyon from the south, 1500 m from the default ignition (decision D25).
            double mouthZ = ignition.y - 1500.0;
            data.cameraPresets = new[]
            {
                Preset("Overview",
                    layout.WorldPoint(extent * 1.02, s.rimElevationM + 3300.0, -extent * 0.22),
                    layout.WorldPoint(mid, floorMid + 250.0, mid)),
                Preset("Canyon Mouth",
                    layout.WorldPoint(layout.AxisX(mouthZ) + 60.0, layout.FloorElevation(mouthZ) + 430.0, mouthZ),
                    layout.WorldPoint(ignition.x, chuteFloor + 20.0, ignition.y + 250.0)),
                Preset("Chute",
                    layout.WorldPoint(chute.x - chuteDir.x * 750.0 + chuteDir.y * 300.0, chuteFloor + 640.0,
                        chute.y - chuteDir.y * 750.0 - chuteDir.x * 300.0),
                    layout.WorldPoint(chute.x + chuteDir.x * 420.0, chuteFloor + 300.0, chute.y + chuteDir.y * 420.0)),
                Preset("Top Down",
                    layout.WorldPoint(mid, s.rimElevationM + extent * 0.92, mid),
                    layout.WorldPoint(mid, floorMid, mid)),
            };
            // Landmarks (docs/decisions.md, decision D3). The chute top is 850 m up the chute from the base.
            Vector2 chuteTop = chute + chuteDir * 850f;
            data.landmarks = new[]
            {
                new ScenarioData.LandmarkData { name = ScenarioData.LandmarkChuteBase, xM = Round1(layout.ToWorld(chute.x)), zM = Round1(layout.ToWorld(chute.y)) },
                new ScenarioData.LandmarkData { name = ScenarioData.LandmarkChuteTop, xM = Round1(layout.ToWorld(chuteTop.x)), zM = Round1(layout.ToWorld(chuteTop.y)) },
            };
            return data;
        }

        static ScenarioData.CameraPresetData Preset(string name, Vector3 position, Vector3 lookAt) =>
            new ScenarioData.CameraPresetData
            {
                name = name,
                positionM = new Vector3(Round1(position.x), Round1(position.y), Round1(position.z)),
                lookAtM = new Vector3(Round1(lookAt.x), Round1(lookAt.y), Round1(lookAt.z)),
            };

        static float Round1(float v) => (float)(Math.Round(v * 10.0) / 10.0);

        /// <summary>Writes the three files of the Scenario into the folder.</summary>
        public static void Write(CanyonResult result, string folder)
        {
            Directory.CreateDirectory(folder);
            int n = result.GridSize;

            byte[] elevationBytes = MemoryMarshal.AsBytes(result.Elevation.AsSpan()).ToArray();
            File.WriteAllBytes(Path.Combine(folder, result.Data.elevationFile), elevationBytes);

            // Row 0 of the fuel array is the south edge. The top row of the image is the north edge.
            File.WriteAllBytes(Path.Combine(folder, result.Data.fuelFile), PngCodec.EncodeGray(result.Fuel, n, n, true));

            File.WriteAllText(Path.Combine(folder, ScenarioLoader.JsonFileName), JsonUtility.ToJson(result.Data, true) + "\n");
        }

        /// <summary>Ground with a smaller slope has no aspect.</summary>
        public const double FlatSlope = 0.05;

        static double Sq(double v) => v * v;

        // ---- noise ----

        static double Smoothstep(double a, double b, double x)
        {
            double u = Math.Min(1.0, Math.Max(0.0, (x - a) / (b - a)));
            return u * u * (3.0 - 2.0 * u);
        }

        static double Hash(int x, int y, uint seed)
        {
            unchecked
            {
                uint h = (uint)x * 374761393u + (uint)y * 668265263u + seed * 2246822519u;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFFu) / (double)0xFFFFFF * 2.0 - 1.0;
            }
        }

        static double ValueNoise(double x, double y, uint seed)
        {
            double fx = Math.Floor(x), fy = Math.Floor(y);
            int ix = (int)fx, iy = (int)fy;
            double tx = x - fx, ty = y - fy;
            tx = tx * tx * tx * (tx * (tx * 6.0 - 15.0) + 10.0);
            ty = ty * ty * ty * (ty * (ty * 6.0 - 15.0) + 10.0);
            double a = Hash(ix, iy, seed), b = Hash(ix + 1, iy, seed);
            double c = Hash(ix, iy + 1, seed), d = Hash(ix + 1, iy + 1, seed);
            return a + (b - a) * tx + ((c + (d - c) * tx) - (a + (b - a) * tx)) * ty;
        }

        /// <summary>Fractal noise in the range -1 to 1.</summary>
        static double Fbm(double x, double y, uint seed, double wavelength, int octaves)
        {
            double sum = 0.0, weight = 0.0, amplitude = 1.0, frequency = 1.0 / wavelength;
            for (int o = 0; o < octaves; o++)
            {
                sum += amplitude * ValueNoise(x * frequency + o * 17.31, y * frequency - o * 9.73, seed + (uint)o * 7919u);
                weight += amplitude;
                amplitude *= 0.5;
                frequency *= 2.0;
            }
            return sum / weight;
        }
    }
}
