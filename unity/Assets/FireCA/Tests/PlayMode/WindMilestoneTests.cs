using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace FireCA.Tests
{
    /// <summary>PlayMode tests of milestone M3: the seven acceptance criteria of the Wind Solver, and the frame loop.</summary>
    public sealed class WindMilestoneTests
    {
        const string DemoScenario = "synthetic-canyon-01";
        const float Cell = GridSpec.FireCellSizeM;
        const float WindCell = GridSpec.WindCellSizeM;
        const int Block = GridSpec.BlockSize;

        FireCAApp _app;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("FireCA", LoadSceneMode.Single);
            yield return null;
            yield return null;
            _app = FireCAApp.Instance;
            Assert.IsNotNull(_app);
            _app.SchedulerEnabled = false;
            _app.WindSolverEnabled = true;
        }

        ScenarioFiles LoadDemo(Action<ScenarioData> edit = null)
        {
            ScenarioFiles files = ScenarioLoader.Load(Path.Combine(ScenarioLoader.ScenariosRoot, DemoScenario));
            edit?.Invoke(files.Data);
            Assert.IsTrue(_app.LoadScenarioFiles(files));
            return files;
        }

        // Runs Wind steps. A statistics readback every 100 steps keeps the GPU command queue short.
        void RunSteps(int count)
        {
            for (int i = 0; i < count; i++)
            {
                _app.Wind.Step();
                if (i % 100 == 99) _app.Wind.ReadStats();
            }
        }

        Vector2[] ReadSurfaceWind()
        {
            var request = AsyncGPUReadback.Request(_app.Registry.Get(SimTextures.SurfaceWind), 0, TextureFormat.RGFloat);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError);
            return request.GetData<Vector2>().ToArray();
        }

        float[] ReadScalar(string name)
        {
            var request = AsyncGPUReadback.Request(_app.Registry.Get(name), 0, TextureFormat.RFloat);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError);
            return request.GetData<float>().ToArray();
        }

        // The gradient of Ground Height at a Wind Grid column, rise over run.
        Vector2 GroundGradient(int bx, int bz, int columns)
        {
            float[] g = _app.GroundHeight;
            int x0 = Mathf.Max(bx - 1, 0), x1 = Mathf.Min(bx + 1, columns - 1), z0 = Mathf.Max(bz - 1, 0), z1 = Mathf.Min(bz + 1, columns - 1);
            return new Vector2(
                (g[bz * columns + x1] - g[bz * columns + x0]) / ((x1 - x0) * WindCell),
                (g[z1 * columns + bx] - g[z0 * columns + bx]) / ((z1 - z0) * WindCell));
        }

        // ---- criterion 1 ----

        [UnityTest, Timeout(600000)]
        public IEnumerator Criterion1_ProjectionRemovesTheDivergence()
        {
            LoadDemo();
            _app.Wind.SpinUp();
            RunSteps(100);
            yield return null;
            double worst = 0.0, beforeSum = 0.0, afterSum = 0.0;
            for (int k = 0; k < 5; k++)
            {
                _app.Wind.StepWithDivergenceReport(out double before, out double after);
                Assert.Greater(before, 0.0);
                worst = Math.Max(worst, after / before);
                beforeSum += before;
                afterSum += after;
            }
            Debug.Log($"[FireCA test] Divergence: mean absolute value {beforeSum / 5.0:E2} 1/s before the pressure kernel, " +
                $"{afterSum / 5.0:E2} 1/s after the project kernel. Largest ratio {worst * 100.0:F1} percent.");
            Assert.Less(worst, 0.10);
        }

        // ---- criterion 2 ----

        [UnityTest, Timeout(600000)]
        public IEnumerator Criterion2_FlatFlowKeepsTheBackgroundWind()
        {
            const int n = 512;
            var data = ScenarioData.CreateDefault("flat", n);
            data.ignitions = Array.Empty<ScenarioData.IgnitionData>();
            var elevation = new float[n * n];
            var fuel = new byte[n * n];
            for (int k = 0; k < n * n; k++) { elevation[k] = 1200f; fuel[k] = 1; }
            var files = new ScenarioFiles
            {
                Id = "flat", Data = data, Elevation = elevation, Fuel = fuel,
                MinElevation = 1200f, MaxElevation = 1200f, Grid = new GridSpec(n, 1200f),
            };
            Assert.IsTrue(_app.LoadScenarioFiles(files));
            _app.Wind.HeatingGain = 0f;
            RunSteps(600);
            yield return null;

            Vector2 background = _app.Weather.BackgroundWind;
            Assert.AreEqual(4f, background.magnitude, 1e-3f);
            Vector2[] wind = ReadSurfaceWind();
            float worstSpeed = 0f, worstAngle = 0f;
            foreach (Vector2 v in wind)
            {
                worstSpeed = Mathf.Max(worstSpeed, Mathf.Abs(v.magnitude - background.magnitude) / background.magnitude);
                worstAngle = Mathf.Max(worstAngle, Vector2.Angle(v, background));
            }
            Debug.Log($"[FireCA test] Flat flow after 600 Wind steps: largest speed error {worstSpeed * 100f:F1} percent, " +
                $"largest direction error {worstAngle:F2} degrees, over {wind.Length} columns.");
            Assert.Less(worstSpeed, 0.20f);
            Assert.Less(worstAngle, 5f);
        }

        // ---- criteria 3 and 4 ----

        // The centerline of the canyon floor and the floor width, per Fire Grid row.
        static void TraceCanyon(ScenarioFiles s, out float[] centerX, out float[] floorWidth)
        {
            int n = s.Grid.FireCells;
            float[] e = s.Elevation;
            centerX = new float[n];
            floorWidth = new float[n];
            int window = Mathf.RoundToInt(200f / Cell), center = -1;
            for (int j = 0; j < n; j++)
            {
                int from = center < 0 ? 0 : Mathf.Max(0, center - window), to = center < 0 ? n - 1 : Mathf.Min(n - 1, center + window);
                float min = float.PositiveInfinity;
                int argMin = from;
                for (int i = from; i <= to; i++) if (e[j * n + i] < min) { min = e[j * n + i]; argMin = i; }
                int left = argMin, right = argMin;
                while (left > 0 && e[j * n + left - 1] < min + 5f) left--;
                while (right < n - 1 && e[j * n + right + 1] < min + 5f) right++;
                center = (left + right) / 2;
                centerX[j] = (center + 0.5f) * Cell;
                floorWidth[j] = (right - left + 1) * Cell;
            }
        }

        [UnityTest, Timeout(900000)]
        public IEnumerator Criteria3And4_CanyonChannelsTheWind_AndTheNarrowSectionSpeedsItUp()
        {
            // The Scenario wind comes from 225 degrees: 45 degrees off the canyon axis, which runs south to north.
            ScenarioFiles s = LoadDemo(d => d.weather.wind = new[] { new ScenarioData.WindEntry { hour = 0f, speedMps = 4f, fromDeg = 225f } });
            _app.Wind.HeatingGain = 0f;
            _app.Wind.SpinUp();
            RunSteps(600);
            yield return null;

            int n = s.Grid.FireCells, columns = s.Grid.WindCellsX;
            TraceCanyon(s, out float[] centerX, out float[] floorWidth);
            Vector2[] wind = ReadSurfaceWind();
            int margin = Mathf.RoundToInt(_app.Wind.SpongeCells) + 2;

            // Criterion 3: the direction on the canyon floor against the canyon axis.
            double deviation = 0.0, along = 0.0, speedSum = 0.0;
            int floorColumns = 0, narrowFirst = -1, narrowLast = -1;
            for (int bz = margin; bz < columns - margin; bz++)
            {
                int row = bz * Block + Block / 2;
                Vector2 tangent = new Vector2(centerX[Mathf.Min(row + 64, n - 1)] - centerX[Mathf.Max(row - 64, 0)], 128f * Cell).normalized;
                if (floorWidth[row] < 100f) { if (narrowFirst < 0) narrowFirst = bz; narrowLast = bz; }
                float half = Mathf.Max(0f, floorWidth[row] * 0.5f - WindCell * 0.5f);
                for (int bx = 0; bx < columns; bx++)
                {
                    float x = (bx + 0.5f) * WindCell;
                    bool onFloor = Mathf.Abs(x - centerX[row]) <= half || Mathf.FloorToInt(centerX[row] / WindCell) == bx;
                    if (!onFloor) continue;
                    Vector2 v = wind[bz * columns + bx];
                    if (v.magnitude < 1e-3f) continue;
                    float cos = Vector2.Dot(v.normalized, tangent);
                    deviation += Mathf.Acos(Mathf.Clamp01(Mathf.Abs(cos))) * Mathf.Rad2Deg;
                    along += cos;
                    speedSum += v.magnitude;
                    floorColumns++;
                }
            }
            Assert.Greater(floorColumns, 200);
            double meanDeviation = deviation / floorColumns;
            Debug.Log($"[FireCA test] Channeling: the mean Surface Wind direction on the canyon floor is {meanDeviation:F1} degrees off the canyon axis " +
                $"({floorColumns} columns, mean speed {speedSum / floorColumns:F2} m/s, mean along-axis cosine {along / floorColumns:F2}). The background wind is 45 degrees off the axis.");
            Assert.Less(meanDeviation, 25.0);

            // Criterion 4: the speed in the narrow section against the floor 500 m upstream.
            Assert.GreaterOrEqual(narrowFirst, 0, "The canyon has no narrow section.");
            bool flowsNorth = along >= 0.0;
            int upstream = flowsNorth
                ? narrowFirst - Mathf.RoundToInt(500f / WindCell)
                : narrowLast + Mathf.RoundToInt(500f / WindCell);
            double narrowSpeed = FloorSpeed(wind, centerX, floorWidth, columns, narrowFirst, narrowLast);
            double upstreamSpeed = FloorSpeed(wind, centerX, floorWidth, columns, upstream - 3, upstream + 3);
            Debug.Log($"[FireCA test] Narrow section: mean Surface Wind speed {narrowSpeed:F2} m/s in the narrow section, " +
                $"{upstreamSpeed:F2} m/s on the canyon floor 500 m upstream ({(flowsNorth ? "south" : "north")} of the narrow section).");
            Assert.Greater(narrowSpeed, upstreamSpeed);
        }

        static double FloorSpeed(Vector2[] wind, float[] centerX, float[] floorWidth, int columns, int firstBz, int lastBz)
        {
            double sum = 0.0;
            int count = 0;
            for (int bz = firstBz; bz <= lastBz; bz++)
            {
                int row = bz * Block + Block / 2;
                float half = Mathf.Max(0f, floorWidth[row] * 0.5f - WindCell * 0.5f);
                for (int bx = 0; bx < columns; bx++)
                {
                    float x = (bx + 0.5f) * WindCell;
                    if (Mathf.Abs(x - centerX[row]) > half && Mathf.FloorToInt(centerX[row] / WindCell) != bx) continue;
                    sum += wind[bz * columns + bx].magnitude;
                    count++;
                }
            }
            return count > 0 ? sum / count : 0.0;
        }

        // ---- criteria 5 and 6 ----

        // The share of steep columns whose Surface Wind points upslope. Returns the count of columns through the out value.
        double UpslopeShare(bool sunlitOnly, out int counted)
        {
            int columns = _app.Scenario.Grid.WindCellsX;
            Vector2[] wind = ReadSurfaceWind();
            float[] heating = ReadScalar(SimTextures.SurfaceHeating);
            int margin = Mathf.RoundToInt(_app.Wind.SpongeCells) + 1;
            int up = 0;
            counted = 0;
            for (int bz = margin; bz < columns - margin; bz++)
            {
                for (int bx = margin; bx < columns - margin; bx++)
                {
                    Vector2 gradient = GroundGradient(bx, bz, columns);
                    if (gradient.magnitude <= 0.30f) continue;
                    if (sunlitOnly && heating[bz * columns + bx] <= 0f) continue;
                    counted++;
                    if (Vector2.Dot(wind[bz * columns + bx], gradient) > 0f) up++;
                }
            }
            return counted > 0 ? up / (double)counted : 0.0;
        }

        static void CalmWindAt(ScenarioData d, float solarHour)
        {
            d.startSolarHour = solarHour;
            d.weather.wind = new[] { new ScenarioData.WindEntry { hour = 0f, speedMps = 0f, fromDeg = 225f } };
        }

        [UnityTest, Timeout(900000)]
        public IEnumerator Criterion5_SunlitSlopesDriveUpslopeWind()
        {
            LoadDemo(d => CalmWindAt(d, 14f));
            RunSteps(1800);
            yield return null;
            double share = UpslopeShare(true, out int counted);
            WindStats stats = _app.Wind.ReadStats();
            Debug.Log($"[FireCA test] Upslope wind at 14:00 after 1800 Wind steps: the Surface Wind points upslope on {share * 100.0:F1} percent of " +
                $"{counted} sunlit columns with a slope over 30 percent. Peak speed {stats.PeakSpeed:F1} m/s. " +
                $"heatingGain {_app.Wind.HeatingGain}, dragCoef {_app.Wind.DragCoef}.");
            Assert.Greater(counted, 500);
            Assert.GreaterOrEqual(share, 0.70);
        }

        [UnityTest, Timeout(900000)]
        public IEnumerator Criterion6_NightSlopesDriveDownslopeWind()
        {
            LoadDemo(d => CalmWindAt(d, 2f));
            RunSteps(1800);
            yield return null;
            double share = 1.0 - UpslopeShare(false, out int counted);
            WindStats stats = _app.Wind.ReadStats();
            Debug.Log($"[FireCA test] Downslope wind at 02:00 after 1800 Wind steps: the Surface Wind points downslope on {share * 100.0:F1} percent of " +
                $"{counted} columns with a slope over 30 percent. Peak speed {stats.PeakSpeed:F1} m/s.");
            Assert.Greater(counted, 500);
            Assert.GreaterOrEqual(share, 0.70);
        }

        // ---- criterion 7 ----

        [UnityTest, Timeout(1800000)]
        public IEnumerator Criterion7_TwentyThousandStepsStayStable()
        {
            LoadDemo();
            float peak = 0f;
            float wallStart = Time.realtimeSinceStartup;
            for (int block = 0; block < 20; block++)
            {
                RunSteps(1000);
                WindStats stats = _app.Wind.ReadStats();
                Assert.AreEqual(0, stats.NonFiniteCells, $"The Velocity holds a NaN value after {(block + 1) * 1000} Wind steps.");
                peak = Mathf.Max(peak, stats.PeakSpeed);
                Assert.Less(stats.PeakSpeed, 60f, $"The peak speed is too high after {(block + 1) * 1000} Wind steps.");
                yield return null;
            }
            float wall = Time.realtimeSinceStartup - wallStart;
            Assert.AreEqual(20000, _app.Wind.StepCount);
            Assert.AreEqual(0, _app.Spread.CountNonFinite(_app.Registry.Get(SimTextures.SurfaceWind)));
            Debug.Log($"[FireCA test] Stability: 20,000 Wind steps with no NaN value. Peak speed {peak:F1} m/s. {wall:F0} s, {wall / 20f:F2} ms per Wind step.");
        }

        // ---- frame loop ----

        [UnityTest, Timeout(600000)]
        public IEnumerator Scheduler_SpinsUpBeforeTheSimClockStarts_ThenRunsWindSteps()
        {
            Assert.IsTrue(_app.LoadScenario("test-canyon-512"), _app.LastError);
            _app.SchedulerEnabled = true;
            _app.Clock.Playing = true;
            _app.Clock.SetTimeScale(60f);
            yield return null;

            // The Sim Clock does not start, and the Scenario ignition does not run, before the spin-up ends.
            Assert.IsTrue(_app.Scheduler.SpinningUp);
            Assert.AreEqual(0.0, _app.Clock.Seconds);
            Assert.AreEqual(0, _app.Spread.IgnitedCells);
            Assert.That(_app.Wind.SpinUpProgress, Is.InRange(0f, 0.2f));
            int frames = 0;
            while (_app.Scheduler.SpinningUp && frames++ < 2000) yield return null;
            Assert.IsFalse(_app.Scheduler.SpinningUp, "The spin-up did not end.");
            Assert.AreEqual(_app.Wind.SpinUpSteps, _app.Wind.SpinUpDone);
            Assert.AreEqual(0.0, _app.Wind.WindTime, "The spin-up must not advance the wind time.");

            for (int frame = 0; frame < 240; frame++) yield return null;
            _app.Clock.Playing = false;
            yield return null;
            Assert.Greater(_app.Clock.Seconds, 1.0);
            Assert.Greater(_app.Wind.StepCount, 0);
            Assert.AreEqual(_app.Wind.StepCount * (double)_app.Wind.DtWind, _app.Wind.WindTime, 1e-6, "A Wind step is a fixed step of dtWind.");
            Assert.GreaterOrEqual(_app.Wind.WindTime, _app.Clock.Seconds - 1e-3, "The wind time must reach the Sim Clock.");
            Assert.Less(_app.Wind.WindTime - _app.Clock.Seconds, _app.Wind.DtWind + 1e-3);
            AsyncGPUReadback.WaitAllRequests();
            yield return null;
            Assert.Greater(_app.Spread.IgnitedCells, 0, "The Scenario ignition must run after the spin-up.");

            // The Wind Solver writes the Surface Wind. The Weather Model does not fill the Surface Wind.
            Assert.IsFalse(_app.Weather.FillsSurfaceWind);
            Vector2[] wind = ReadSurfaceWind();
            Vector2 mean = Vector2.zero;
            foreach (Vector2 v in wind) mean += v;
            mean /= wind.Length;
            Assert.Less(Vector2.Angle(mean, _app.Weather.BackgroundWind), 20f, "The mean Surface Wind must follow the background wind.");
            Assert.AreEqual(0, _app.Wind.ReadStats().NonFiniteCells);
        }
    }
}
