using System;
using System.Collections.Generic;
using System.IO;
using FireCA.Editor;
using NUnit.Framework;
using UnityEngine;

namespace FireCA.Tests
{
    /// <summary>
    /// M0 acceptance criteria 3 and 4. The tests measure Canyon Generator requirements 2 to 10 of
    /// docs/01-data-layers.md on the elevation raster of synthetic-canyon-01.
    /// </summary>
    public sealed class CanyonGeneratorTests
    {
        const float Cell = GridSpec.FireCellSizeM;

        static CanyonGeneratorSettings _settings;
        static CanyonResult _canyon;
        static int _n;
        static float[] _e;

        // Per-row measurements of the main canyon.
        static float[] _centerX, _floor, _floorWidth, _rimWest, _rimEast;

        [OneTimeSetUp]
        public void Generate()
        {
            _settings = FireCAProjectSetup.LoadCanyonSettings(FireCAProjectSetup.DemoScenario);
            Assert.IsNotNull(_settings, "The settings asset of synthetic-canyon-01 does not exist. Run Tools > FireCA > Setup.");
            Assert.AreEqual(4096, _settings.gridSize);
            _canyon = CanyonGenerator.Generate(_settings);
            _n = _canyon.GridSize;
            _e = _canyon.Elevation;
            MeasureMainCanyon();
        }

        [OneTimeTearDown]
        public void Release()
        {
            _canyon = null;
            _e = null;
        }

        // ---- sampling helpers ----

        static float At(int i, int j) => _e[Mathf.Clamp(j, 0, _n - 1) * _n + Mathf.Clamp(i, 0, _n - 1)];

        /// <summary>Bilinear elevation at a world position.</summary>
        static float Sample(float x, float z)
        {
            float fx = x / Cell - 0.5f, fz = z / Cell - 0.5f;
            int i = Mathf.FloorToInt(fx), j = Mathf.FloorToInt(fz);
            float tx = fx - i, tz = fz - j;
            return Mathf.Lerp(Mathf.Lerp(At(i, j), At(i + 1, j), tx), Mathf.Lerp(At(i, j + 1), At(i + 1, j + 1), tx), tz);
        }

        /// <summary>Mean elevation of a square around a world position.</summary>
        static float Smooth(Vector2 p, float sizeM)
        {
            int steps = Mathf.Max(1, Mathf.RoundToInt(sizeM / 8f));
            float sum = 0f;
            for (int a = -steps; a <= steps; a++)
                for (int b = -steps; b <= steps; b++)
                    sum += Sample(p.x + a * sizeM * 0.5f / steps, p.y + b * sizeM * 0.5f / steps);
            int side = 2 * steps + 1;
            return sum / (side * side);
        }

        static void MeasureMainCanyon()
        {
            _centerX = new float[_n];
            _floor = new float[_n];
            _floorWidth = new float[_n];
            _rimWest = new float[_n];
            _rimEast = new float[_n];

            int rimReach = Mathf.RoundToInt(1600f / Cell);
            int window = Mathf.RoundToInt(200f / Cell);
            int center = -1;
            for (int j = 0; j < _n; j++)
            {
                int from = center < 0 ? 0 : Mathf.Max(0, center - window);
                int to = center < 0 ? _n - 1 : Mathf.Min(_n - 1, center + window);
                float min = float.PositiveInfinity;
                int argMin = from;
                for (int i = from; i <= to; i++)
                {
                    float v = _e[j * _n + i];
                    if (v < min) { min = v; argMin = i; }
                }
                // The floor: the contiguous run of cells within 5 m of the lowest cell.
                int left = argMin, right = argMin;
                while (left > 0 && _e[j * _n + left - 1] < min + 5f) left--;
                while (right < _n - 1 && _e[j * _n + right + 1] < min + 5f) right++;
                center = (left + right) / 2;

                _floor[j] = min;
                _centerX[j] = (center + 0.5f) * Cell;
                _floorWidth[j] = (right - left + 1) * Cell;

                float west = float.NegativeInfinity, east = float.NegativeInfinity;
                for (int i = Mathf.Max(0, center - rimReach); i < center; i++) west = Mathf.Max(west, _e[j * _n + i]);
                for (int i = center + 1; i <= Mathf.Min(_n - 1, center + rimReach); i++) east = Mathf.Max(east, _e[j * _n + i]);
                _rimWest[j] = west;
                _rimEast[j] = east;
            }
        }

        static float[] MovingAverage(float[] values, int halfWindow)
        {
            var result = new float[values.Length];
            double sum = 0;
            int count = 0, lo = 0, hi = -1;
            for (int k = 0; k < values.Length; k++)
            {
                int wantLo = Mathf.Max(0, k - halfWindow), wantHi = Mathf.Min(values.Length - 1, k + halfWindow);
                while (hi < wantHi) { sum += values[++hi]; count++; }
                while (lo < wantLo) { sum -= values[lo++]; count--; }
                result[k] = (float)(sum / count);
            }
            return result;
        }

        // ---- criterion 3: determinism ----

        [Test]
        public void SameSeed_ProducesIdenticalArrays()
        {
            CanyonResult second = CanyonGenerator.Generate(_settings);
            CollectionAssert.AreEqual(_canyon.Elevation, second.Elevation);
            CollectionAssert.AreEqual(_canyon.Fuel, second.Fuel);
            Assert.AreEqual(JsonUtility.ToJson(_canyon.Data), JsonUtility.ToJson(second.Data));
        }

        [Test]
        public void SameSeed_WritesIdenticalFiles()
        {
            var settings = FireCAProjectSetup.LoadCanyonSettings(FireCAProjectSetup.TestScenario);
            Assert.IsNotNull(settings);
            string root = Path.Combine(Path.GetTempPath(), "fireca-tests", Guid.NewGuid().ToString("N"));
            try
            {
                string a = Path.Combine(root, "a"), b = Path.Combine(root, "b");
                CanyonGenerator.Write(CanyonGenerator.Generate(settings), a);
                CanyonGenerator.Write(CanyonGenerator.Generate(settings), b);
                foreach (string file in new[] { "elevation.r32", "fuel.png", "scenario.json" })
                    CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(a, file)), File.ReadAllBytes(Path.Combine(b, file)), file);
                Assert.DoesNotThrow(() => ScenarioLoader.Load(a), "The written Scenario must pass every validation check.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void DifferentSeed_ProducesDifferentTerrain()
        {
            var other = UnityEngine.Object.Instantiate(_settings);
            other.gridSize = 512;
            var a = CanyonGenerator.Generate(other);
            other.seed = _settings.seed + 1;
            var b = CanyonGenerator.Generate(other);
            UnityEngine.Object.DestroyImmediate(other);
            CollectionAssert.AreNotEqual(a.Elevation, b.Elevation);
        }

        [Test]
        public void ScenarioFolders_MatchTheGenerator()
        {
            foreach (string id in new[] { FireCAProjectSetup.DemoScenario, FireCAProjectSetup.TestScenario })
            {
                string folder = CanyonGeneratorWindow.ScenarioFolder(id);
                Assert.IsTrue(Directory.Exists(folder), $"The Scenario folder '{id}' does not exist.");
                CanyonResult result = id == FireCAProjectSetup.DemoScenario
                    ? _canyon
                    : CanyonGenerator.Generate(FireCAProjectSetup.LoadCanyonSettings(id));
                ScenarioFiles files = ScenarioLoader.Load(folder);
                Assert.AreEqual(result.GridSize, files.Grid.FireCells);
                CollectionAssert.AreEqual(result.Elevation, files.Elevation, $"{id}: elevation.r32 is out of date.");
                CollectionAssert.AreEqual(result.Fuel, files.Fuel, $"{id}: fuel.png is out of date.");
            }
        }

        // ---- criterion 4: requirements 2 to 10 ----

        [Test]
        public void Requirement02_MainCanyonCrossesFromSouthEdgeToNorthEdge()
        {
            // The trace of the floor starts on the south edge (row 0) and ends on the north edge (last row).
            for (int j = 0; j < _n; j++)
            {
                float depth = Mathf.Min(_rimWest[j], _rimEast[j]) - _floor[j];
                Assert.Greater(depth, 300f, $"Row {j}: the canyon has no wall on one side.");
                if (j > 0)
                    Assert.Less(Mathf.Abs(_centerX[j] - _centerX[j - 1]), 30f, $"Row {j}: the canyon floor is not continuous.");
                Assert.That(_centerX[j], Is.InRange(1700f, _n * Cell - 1700f), $"Row {j}: the canyon leaves the grid.");
            }
        }

        [Test]
        public void Requirement03_ReliefIs600To900()
        {
            float low = float.PositiveInfinity, high = float.NegativeInfinity;
            for (int j = 0; j < _n; j++)
            {
                float relief = Mathf.Max(_rimWest[j], _rimEast[j]) - _floor[j];
                low = Mathf.Min(low, relief);
                high = Mathf.Max(high, relief);
            }
            Debug.Log($"[FireCA test] Relief from floor to rim: {low:F0} m to {high:F0} m.");
            Assert.GreaterOrEqual(low, 600f);
            Assert.LessOrEqual(high, 900f);
        }

        [Test]
        public void Requirement04_AxisBendsAtLeastTwice()
        {
            float[] axis = MovingAverage(_centerX, Mathf.RoundToInt(200f / Cell));
            // A bend is a reversal of the east-west direction of the axis, after a run of at least 100 m.
            int bends = 0, direction = 0;
            float extreme = axis[0];
            for (int j = 1; j < _n; j++)
            {
                if (direction >= 0 && axis[j] > extreme) { extreme = axis[j]; if (direction == 0 && axis[j] - axis[0] > 100f) direction = 1; }
                else if (direction <= 0 && axis[j] < extreme) { extreme = axis[j]; if (direction == 0 && axis[0] - axis[j] > 100f) direction = -1; }
                if (direction == 1 && extreme - axis[j] > 100f) { bends++; direction = -1; extreme = axis[j]; }
                else if (direction == -1 && axis[j] - extreme > 100f) { bends++; direction = 1; extreme = axis[j]; }
            }
            Debug.Log($"[FireCA test] The canyon axis bends {bends} times.");
            Assert.GreaterOrEqual(bends, 2);
        }

        [Test]
        public void Requirement05_OneNarrowSectionWithFloorUnder100m()
        {
            int runs = 0, longest = 0, current = 0;
            float narrowest = float.PositiveInfinity;
            for (int j = 0; j < _n; j++)
            {
                narrowest = Mathf.Min(narrowest, _floorWidth[j]);
                if (_floorWidth[j] < 100f)
                {
                    if (current == 0) runs++;
                    current++;
                    longest = Mathf.Max(longest, current);
                }
                else current = 0;
            }
            Debug.Log($"[FireCA test] Narrowest floor: {narrowest:F0} m. Narrow section length: {longest * Cell:F0} m.");
            Assert.Less(narrowest, 100f);
            Assert.AreEqual(1, runs, "The main canyon must have one narrow section.");
            Assert.GreaterOrEqual(longest * Cell, 200f, "The narrow section must be a section, not a point.");
        }

        [Test]
        public void Requirement06_AtLeastFourSideDrainages()
        {
            CanyonLayout layout = _canyon.Layout;
            int total = 0;
            foreach (float side in new[] { -1f, 1f })
            {
                // Walk along the wall, 500 m from the edge of the floor, and record the elevation.
                const float stepM = 8f;
                var t = new List<float>();
                var e = new List<float>();
                for (float s = 400f; s <= _n * Cell - 400f; s += stepM)
                {
                    layout.AxisFrame(s, out double ax, out _, out _, out double nx, out double nz);
                    double reach = layout.FloorHalfWidth(s) + 500.0;
                    t.Add(s);
                    e.Add(Sample((float)(ax + side * nx * reach), (float)(s + side * nz * reach)));
                }
                float[] smooth = MovingAverage(e.ToArray(), 3);

                // A drainage is a dip of more than 40 m below the moving median of the wall.
                int half = Mathf.RoundToInt(900f / stepM);
                bool inDip = false;
                var window = new List<float>();
                for (int k = 0; k < smooth.Length; k++)
                {
                    window.Clear();
                    for (int m = Mathf.Max(0, k - half); m <= Mathf.Min(smooth.Length - 1, k + half); m++) window.Add(smooth[m]);
                    window.Sort();
                    float median = window[window.Count / 2];
                    bool dip = median - smooth[k] > 40f;
                    // The saddle and the chute are separate requirements. Their dips do not count.
                    bool excluded = (side < 0f && Mathf.Abs(t[k] - _settings.saddleZM) < 600f)
                        || (side > 0f && Mathf.Abs(t[k] - _settings.chuteZM) < 300f);
                    if (dip && !excluded && !inDip) total++;
                    inDip = dip && !excluded;
                }
            }
            Debug.Log($"[FireCA test] Side drainages measured: {total}.");
            Assert.GreaterOrEqual(total, 4);
        }

        [Test]
        public void Requirement07_Chute()
        {
            CanyonLayout layout = _canyon.Layout;
            Vector2 origin = layout.ChuteBase, dir = layout.ChuteDirection;
            Vector2 across = new Vector2(dir.y, -dir.x);

            // Slope along the chute floor, over 100 m segments.
            const float segment = 100f, step = 4f;
            float bestLength = 0f, runStart = -1f, minSlope = float.PositiveInfinity, maxSlope = float.NegativeInfinity;
            for (float s = 0f; s <= 1100f; s += step)
            {
                float slope = (Floor(s + segment) - Floor(s)) / segment;
                bool ok = slope >= 0.55f && slope <= 1.0f;
                if (ok)
                {
                    if (runStart < 0f) runStart = s;
                    bestLength = Mathf.Max(bestLength, s + segment - runStart);
                    minSlope = Mathf.Min(minSlope, slope);
                    maxSlope = Mathf.Max(maxSlope, slope);
                }
                else runStart = -1f;
            }
            Debug.Log($"[FireCA test] Chute: {bestLength:F0} m long, slope {minSlope * 100f:F0} to {maxSlope * 100f:F0} percent.");
            Assert.GreaterOrEqual(bestLength, 600f, "The chute must be at least 600 m long with a slope of 55 to 100 percent.");

            // Straight and narrow: at each station the lowest point is on the line, and the ground rises on both sides.
            for (float s = 150f; s <= 750f; s += 100f)
            {
                Vector2 p = origin + dir * s;
                float lowest = float.PositiveInfinity, lowestOffset = 0f;
                for (float o = -150f; o <= 150f; o += 2f)
                {
                    float v = Smooth(p + across * o, 16f);
                    if (v < lowest) { lowest = v; lowestOffset = o; }
                }
                Assert.LessOrEqual(Mathf.Abs(lowestOffset), 40f, $"Station {s} m: the chute is not straight.");
                float centerHeight = Smooth(p, 16f);
                Assert.Greater(Smooth(p + across * 90f, 16f) - centerHeight, 8f, $"Station {s} m: the chute has no side wall.");
                Assert.Greater(Smooth(p - across * 90f, 16f) - centerHeight, 8f, $"Station {s} m: the chute has no side wall.");
            }

            // The wall faces west or south: the downslope azimuth is between 135 and 315 degrees.
            Vector2 mid = origin + dir * 450f;
            float dhdx = (Smooth(mid + Vector2.right * 150f, 64f) - Smooth(mid - Vector2.right * 150f, 64f)) / 300f;
            float dhdz = (Smooth(mid + Vector2.up * 150f, 64f) - Smooth(mid - Vector2.up * 150f, 64f)) / 300f;
            float aspect = Mathf.Atan2(-dhdx, -dhdz) * Mathf.Rad2Deg;
            if (aspect < 0f) aspect += 360f;
            Debug.Log($"[FireCA test] Chute wall aspect: {aspect:F0} degrees.");
            Assert.That(aspect, Is.InRange(135f, 315f), "The chute must be on a west-facing or south-facing wall.");

            float Floor(float s)
            {
                Vector2 p = origin + dir * s;
                return (Sample(p.x, p.y) + Sample(p.x + across.x * 4f, p.y + across.y * 4f) + Sample(p.x - across.x * 4f, p.y - across.y * 4f)) / 3f;
            }
        }

        [Test]
        public void Requirement08_SaddleOnARim()
        {
            CanyonLayout layout = _canyon.Layout;
            Vector2 p = layout.SaddlePoint, along = layout.SaddleAlong, across = layout.SaddleAcross;
            float center = Smooth(p, 64f);

            // On a rim: the point is near the top of the canyon wall.
            int row = Mathf.Clamp((int)(p.y / Cell), 0, _n - 1);
            Assert.Greater(center - _floor[row], 500f, "The saddle must be on a rim.");

            // A saddle: the ground rises along the rim on both sides and falls across the rim on both sides.
            float alongA = Smooth(p + along * 350f, 64f) - center, alongB = Smooth(p - along * 350f, 64f) - center;
            float acrossA = Smooth(p + across * 350f, 64f) - center, acrossB = Smooth(p - across * 350f, 64f) - center;
            Debug.Log($"[FireCA test] Saddle: along the rim +{alongA:F0} m and +{alongB:F0} m, across the rim {acrossA:F0} m and {acrossB:F0} m.");
            Assert.Greater(alongA, 30f);
            Assert.Greater(alongB, 30f);
            Assert.Less(acrossA, -10f);
            Assert.Less(acrossB, -10f);
        }

        [Test]
        public void Requirement09_FractalNoiseDetailUnder40m()
        {
            // A patch of the flat ground far behind the rim. The only relief of the patch is the noise detail.
            const float from = 100f, to = 900f;
            double sum = 0;
            int count = 0;
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (float z = from; z <= to; z += Cell)
            {
                for (float x = from; x <= to; x += Cell)
                {
                    float v = At((int)(x / Cell), (int)(z / Cell));
                    sum += v; count++;
                    min = Mathf.Min(min, v); max = Mathf.Max(max, v);
                }
            }
            float mean = (float)(sum / count);
            float amplitude = Mathf.Max(max - mean, mean - min);
            Debug.Log($"[FireCA test] Noise detail on flat ground: {min - mean:F1} m to {max - mean:F1} m.");
            Assert.Less(amplitude, 40f);
            Assert.Greater(max - min, 5f, "The terrain must contain noise detail.");
            Assert.Less(_settings.noiseAmplitudeM, 40f);
        }

        [Test]
        public void Requirement10_FloorDescendsFromSouthToNorth()
        {
            float[] floor = MovingAverage(_floor, Mathf.RoundToInt(100f / Cell));
            for (int j = 50; j < _n; j++)
                Assert.LessOrEqual(floor[j], floor[j - 50] + 1f, $"Row {j}: the floor rises toward the north.");
            Debug.Log($"[FireCA test] Floor elevation: {floor[0]:F0} m on the south edge, {floor[_n - 1]:F0} m on the north edge.");
            Assert.Greater(floor[0] - floor[_n - 1], 100f);
        }

        // ---- requirements 11 to 13 ----

        [Test]
        public void Requirement11_FuelRules()
        {
            CanyonLayout layout = _canyon.Layout;
            byte[] fuel = _canyon.Fuel;

            // Rule 1 on every cell: a slope over 120 percent is barren, and barren appears nowhere else.
            int barren = 0;
            for (int j = 0; j < _n; j++)
            {
                int j0 = Mathf.Max(j - 1, 0), j1 = Mathf.Min(j + 1, _n - 1);
                for (int i = 0; i < _n; i++)
                {
                    int i0 = Mathf.Max(i - 1, 0), i1 = Mathf.Min(i + 1, _n - 1);
                    double dhdx = (_e[j * _n + i1] - _e[j * _n + i0]) / ((i1 - i0) * Cell);
                    double dhdz = (_e[j1 * _n + i] - _e[j0 * _n + i]) / ((j1 - j0) * Cell);
                    bool steep = Math.Sqrt(dhdx * dhdx + dhdz * dhdz) > 1.2;
                    if (steep != (fuel[j * _n + i] == FuelCodes.Barren))
                        Assert.Fail($"Cell ({i}, {j}): the barren rule does not hold.");
                    if (steep) barren++;
                }
            }
            Assert.Less(barren / (float)(_n * _n), 0.10f, "Too much of the terrain is barren.");

            // Rules 2 to 8 at sample points away from steep ground.
            // The station is 500 m north of the chute, away from every side drainage.
            float stationZ = _settings.chuteZM + 500f;
            layout.AxisFrame(stationZ, out double ax, out _, out _, out double nx, out double nz);
            Vector2 axis = new Vector2((float)ax, stationZ), normal = new Vector2((float)nx, (float)nz);
            double hw = layout.FloorHalfWidth(axis.y), wall = layout.WallWidth(axis.y);

            Assert.AreEqual(FuelCodes.Water, FuelAt(axis), "Rule 2: creek channel.");
            Assert.AreEqual(9, FuelAt(axis + normal * 20f), "Rule 3: within 40 m of the creek channel.");
            Assert.AreEqual(1, FuelAt(axis + normal * 90f), "Rule 5: canyon floor, no aspect.");
            Assert.AreEqual(4, FuelAt(layout.ChuteBase + layout.ChuteDirection * 400f), "Rule 4: inside the chute.");
            Assert.AreEqual(1, MostCommonFuel(axis + normal * (float)(hw + 0.12 * wall), 30f), "Rule 5: low on the sunny wall.");
            Assert.That(MostCommonFuel(axis + normal * (float)(hw + 0.55 * wall), 30f), Is.EqualTo(4).Or.EqualTo(5), "Rule 6: middle of the sunny wall.");
            Assert.That(MostCommonFuel(axis - normal * (float)(hw + 0.40 * wall), 30f), Is.EqualTo(8).Or.EqualTo(10), "Rule 7: shaded wall.");
            Assert.AreEqual(2, MostCommonFuel(axis + normal * (float)(hw + wall + 400.0), 30f), "Rule 8: above the rim.");

            // The creek channel is 6 m wide.
            int row = (int)(axis.y / Cell), water = 0;
            for (int i = 0; i < _n; i++) if (fuel[row * _n + i] == FuelCodes.Water) water++;
            Assert.That(water * Cell, Is.InRange(4f, 10f), "The creek channel must be 6 m wide.");

            int FuelAt(Vector2 p) => fuel[Mathf.Clamp((int)(p.y / Cell), 0, _n - 1) * _n + Mathf.Clamp((int)(p.x / Cell), 0, _n - 1)];

            int MostCommonFuel(Vector2 p, float radius)
            {
                var counts = new int[256];
                for (float dz = -radius; dz <= radius; dz += Cell)
                    for (float dx = -radius; dx <= radius; dx += Cell)
                        counts[FuelAt(p + new Vector2(dx, dz))]++;
                int best = 0;
                for (int c = 1; c < 256; c++) if (counts[c] > counts[best]) best = c;
                return best;
            }
        }

        [Test]
        public void Requirement12_ScenarioJsonHasTheDefaultValues()
        {
            ScenarioData d = _canyon.Data, expected = ScenarioData.CreateDefault(_settings.scenarioName, _settings.gridSize);
            Assert.AreEqual(expected.name, d.name);
            Assert.AreEqual(expected.gridSize, d.gridSize);
            Assert.AreEqual(2.0f, d.cellSizeM);
            Assert.AreEqual("elevation.r32", d.elevationFile);
            Assert.AreEqual("fuel.png", d.fuelFile);
            Assert.AreEqual(37.0f, d.latitudeDeg);
            Assert.AreEqual(213, d.dayOfYear);
            Assert.AreEqual(13.0f, d.startSolarHour);
            Assert.AreEqual(JsonUtility.ToJson(expected.weather), JsonUtility.ToJson(d.weather));
            Assert.AreEqual(JsonUtility.ToJson(expected.moisture), JsonUtility.ToJson(d.moisture));
            Assert.AreEqual(1, d.ignitions.Length);
            Assert.AreEqual(0.0f, d.ignitions[0].hour);
            Assert.AreEqual(6.0f, d.ignitions[0].radiusM);
            Assert.DoesNotThrow(() => { ScenarioValidation.CheckGrid(d); ScenarioValidation.CheckSettings(d); });
        }

        [Test]
        public void Requirement13_DefaultIgnitionIsOnTheFloor200mFromTheChuteBase()
        {
            var ignition = _canyon.Data.ignitions[0];
            Vector2 p = new Vector2(ignition.xM, ignition.zM);
            float distance = Vector2.Distance(p, _canyon.Layout.ChuteBase);
            int row = (int)(p.y / Cell);
            float aboveFloor = Sample(p.x, p.y) - _floor[row];
            int fuel = _canyon.Fuel[row * _n + (int)(p.x / Cell)];
            Debug.Log($"[FireCA test] Default ignition: {distance:F1} m from the chute base, {aboveFloor:F1} m above the floor, fuel {fuel}.");
            Assert.AreEqual(200f, distance, 2f);
            Assert.Less(aboveFloor, 5f, "The default ignition must be on the canyon floor.");
            Assert.IsTrue(FuelCodes.IsBurnable(fuel), "The default ignition must be on burnable fuel.");
        }
    }
}
