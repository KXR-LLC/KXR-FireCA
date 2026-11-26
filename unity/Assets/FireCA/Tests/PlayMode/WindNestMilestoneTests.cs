using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace FireCA.Tests
{
    /// <summary>
    /// PlayMode tests of milestone M7: the acceptance criteria of the Wind Nest (docs/05-milestones.md and
    /// docs/decisions.md, decision D32).
    /// </summary>
    public sealed class WindNestMilestoneTests
    {
        const string RealScenario = "south-canyon";

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
            _app.WindNestEnabled = true;
        }

        // Runs Wind steps. Each Wind step runs the steps of the Wind Nest.
        void RunSteps(int count)
        {
            for (int i = 0; i < count; i++)
            {
                _app.Wind.Step();
                _app.Weather.Update(_app.Wind.WindTime);
                if (i % 50 == 49) _app.Wind.ReadStats();
            }
        }

        Vector2 PlaceAtIgnition()
        {
            var ignition = _app.Scenario.Data.ignitions[0];
            var center = new Vector2(ignition.xM, ignition.zM);
            Assert.IsTrue(_app.PlaceWindNest(center), "The Wind Nest was not placed.");
            Assert.IsTrue(_app.Nest.Active);
            return center;
        }

        static Vector2[] ReadVectors(Texture texture)
        {
            var request = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGFloat);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError);
            return request.GetData<Vector2>().ToArray();
        }

        // The mean vorticity (1/s) of the air cells under 150 m above the ground, inside a world rectangle.
        static double LowLevelVorticity(Texture turbulence, float[] ground, WindDomain d, Rect box, out float highest)
        {
            var rt = (RenderTexture)turbulence;
            var request = AsyncGPUReadback.Request(rt, 0, 0, rt.width, 0, rt.height, 0, rt.volumeDepth, TextureFormat.RGBAHalf);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError);
            double sum = 0.0;
            long count = 0;
            highest = 0f;
            for (int z = 0; z < d.Nz; z++)
            {
                float worldZ = d.OriginXZ.y + (z + 0.5f) * d.CellSize;
                if (worldZ < box.yMin || worldZ > box.yMax) continue;
                var layer = request.GetData<ushort>(z);
                for (int x = 0; x < d.Nx; x++)
                {
                    float worldX = d.OriginXZ.x + (x + 0.5f) * d.CellSize;
                    if (worldX < box.xMin || worldX > box.xMax) continue;
                    float g = ground[z * d.Nx + x];
                    for (int y = 0; y < d.Ny; y++)
                    {
                        float height = d.BottomY + (y + 0.5f) * d.CellSize - g;
                        if (height < 0f) continue;
                        if (height > 150f) break;
                        int i = 4 * (x + y * rt.width);
                        // A solid cell has a turbulence speed of 0.
                        if (Mathf.HalfToFloat(layer[i + 3]) <= 0f) continue;
                        var omega = new Vector3(Mathf.HalfToFloat(layer[i]), Mathf.HalfToFloat(layer[i + 1]), Mathf.HalfToFloat(layer[i + 2]));
                        sum += omega.magnitude;
                        highest = Mathf.Max(highest, omega.magnitude);
                        count++;
                    }
                }
            }
            Assert.Greater(count, 1000);
            return sum / count;
        }

        // ---- criterion 1 ----

        [UnityTest, Timeout(600000)]
        public IEnumerator Criterion1_FlatFlowKeepsTheBackgroundWind()
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
            _app.Wind.SpinUp();
            RunSteps(100);
            _app.WindNestCellSizeM = 8f;
            Assert.IsTrue(_app.PlaceWindNest(new Vector2(512f, 512f)));
            WindNest nest = _app.Nest;
            RunSteps(150);
            yield return null;
            Assert.AreEqual(300, nest.Solver.StepCount, "The Wind Nest must run 2 steps per Wind step.");

            Vector2 background = _app.Weather.BackgroundWind;
            Vector2[] wind = ReadVectors(nest.SurfaceWind);
            WindDomain d = nest.Domain;
            int margin = Mathf.RoundToInt(nest.Solver.SpongeCells) + 2;
            float worstSpeed = 0f, worstAngle = 0f;
            for (int z = margin; z < d.Nz - margin; z++)
            {
                for (int x = margin; x < d.Nx - margin; x++)
                {
                    Vector2 v = wind[z * d.Nx + x];
                    worstSpeed = Mathf.Max(worstSpeed, Mathf.Abs(v.magnitude - background.magnitude) / background.magnitude);
                    worstAngle = Mathf.Max(worstAngle, Vector2.Angle(v, background));
                }
            }
            WindStats stats = nest.Solver.ReadStats();
            Debug.Log($"[FireCA test] Wind Nest, flat flow after 300 steps: largest speed error {worstSpeed * 100f:F1} percent, " +
                $"largest direction error {worstAngle:F2} degrees. {d.Nx} x {d.Ny} x {d.Nz} cells of {d.CellSize} m.");
            Assert.AreEqual(0, stats.NonFiniteCells);
            Assert.Less(worstSpeed, 0.20f);
            Assert.Less(worstAngle, 5f);
        }

        // ---- criteria 2 and 3 ----

        [UnityTest, Timeout(900000)]
        public IEnumerator Criteria2And3_ProjectionWorks_AndTheNestHoldsMoreLowLevelEddies()
        {
            Assert.IsTrue(_app.LoadScenario(RealScenario), _app.LastError);
            _app.Wind.SpinUp();
            PlaceAtIgnition();
            WindNest nest = _app.Nest;
            RunSteps(450);
            yield return null;

            // Criterion 2: the projection of the Wind Nest.
            // The step of the Wind Nest is short and starts from a field with little divergence, so the criterion is
            // the value after the project kernel and not the ratio (docs/decisions.md, decision D32).
            double worst = 0.0, worstRatio = 0.0;
            for (int k = 0; k < 5; k++)
            {
                nest.Solver.StepWithDivergenceReport(out double before, out double after);
                Assert.Greater(before, 0.0);
                Assert.Less(after, before);
                worst = Math.Max(worst, after);
                worstRatio = Math.Max(worstRatio, after / before);
            }
            Debug.Log($"[FireCA test] Wind Nest divergence: largest mean absolute value after the project kernel {worst:E2} 1/s, " +
                $"largest ratio to the value before the pressure kernel {worstRatio * 100.0:F1} percent.");
            Assert.Less(worst, 3.0e-4);

            // Criterion 3: the vorticity under 150 m above the ground, in the box without the blend zones.
            WindDomain d = nest.Domain;
            float m = 2f * nest.RampM;
            var box = Rect.MinMaxRect(d.OriginXZ.x + m, d.OriginXZ.y + m, d.OriginXZ.x + d.Nx * d.CellSize - m, d.OriginXZ.y + d.Nz * d.CellSize - m);
            var groundRequest = AsyncGPUReadback.Request(nest.GroundHeight, 0, TextureFormat.RFloat);
            groundRequest.WaitForCompletion();
            double nestVorticity = LowLevelVorticity(nest.Turbulence, groundRequest.GetData<float>().ToArray(), d, box, out float nestHighest);
            double gridVorticity = LowLevelVorticity(_app.Registry.Get(SimTextures.Turbulence), _app.GroundHeight, _app.Wind.Domain, box, out float gridHighest);
            Debug.Log($"[FireCA test] Vorticity under 150 m above the ground: Wind Nest mean {nestVorticity:E2} 1/s and highest {nestHighest:F2} 1/s, " +
                $"Wind Grid mean {gridVorticity:E2} 1/s and highest {gridHighest:F2} 1/s. Ratio of the means {nestVorticity / gridVorticity:F2}.");
            Assert.GreaterOrEqual(nestVorticity / gridVorticity, 1.4);
        }

        // ---- criteria 4, 5, and 6 ----

        [UnityTest, Timeout(1200000)]
        public IEnumerator Criteria4To6_FireRunIsStable_SurfaceWindComesFromTheNest_MemoryIsReported()
        {
            Assert.IsTrue(_app.LoadScenario(RealScenario), _app.LastError);
            WindSolver wind = _app.Wind;
            SpreadSolver spread = _app.Spread;
            spread.SynchronousReadback = true;
            wind.SpinUp();
            Vector2 center = PlaceAtIgnition();
            WindNest nest = _app.Nest;
            spread.Ignite(center, _app.Scenario.Data.ignitions[0].radiusM, 0.0);

            // Two simulated hours: 3600 Wind steps and 7200 steps of the Wind Nest.
            double sinceFull = 0.0;
            float wallStart = Time.realtimeSinceStartup;
            for (int step = 0; step < 3600; step++)
            {
                double t = step * (double)wind.DtWind;
                wind.Step();
                sinceFull += wind.DtWind;
                if (_app.Weather.Update(t) || sinceFull >= 30.0) { spread.RunBehavior(true); sinceFull = 0.0; }
                spread.RunUntil(t + wind.DtWind);
                if (step % 300 == 299)
                {
                    WindStats now = nest.Solver.ReadStats();
                    Assert.AreEqual(0, now.NonFiniteCells, $"The Wind Nest holds a NaN value at Wind step {step + 1}.");
                    Assert.That(now.ThetaMax, Is.LessThanOrEqualTo(wind.ThetaMax + 1e-3f));
                    Assert.Less(now.PeakSpeed, 100f);
                    yield return null;
                }
                else if (step % 50 == 49) wind.ReadStats();
            }
            float wall = Time.realtimeSinceStartup - wallStart;
            WindStats stats = nest.Solver.ReadStats();
            Debug.Log($"[FireCA test] Wind Nest with a fire, 2 simulated hours in {wall:F0} s ({wall / 3600f * 1000f:F1} ms per Wind step with the Wind Nest): " +
                $"burned area {spread.BurnedAreaHa:F1} ha, peak wind {stats.PeakSpeed:F1} m/s, highest theta {stats.ThetaMax:F1} K, highest smoke {stats.SmokeMax:F2}.");
            Assert.Greater(spread.BurnedAreaHa, 0.5, "The fire did not grow.");
            Assert.Greater(stats.SmokeMax, 0f, "The Wind Nest holds no smoke.");

            // Criterion 5: inside the box, the Surface Wind is the Surface Wind of the Wind Nest.
            WindDomain d = nest.Domain;
            Vector2[] nestWind = ReadVectors(nest.SurfaceWind);
            Vector2[] gridWind = ReadVectors(_app.Registry.Get(SimTextures.SurfaceWind));
            float m = 2f * nest.RampM + GridSpec.WindCellSizeM;
            int columns = _app.Scenario.Grid.WindCellsX;
            Vector2 gridMean = Vector2.zero, nestMean = Vector2.zero;
            int gridCount = 0, nestCount = 0;
            float x0 = d.OriginXZ.x + m, x1 = d.OriginXZ.x + d.Nx * d.CellSize - m, z0 = d.OriginXZ.y + m, z1 = d.OriginXZ.y + d.Nz * d.CellSize - m;
            for (int bz = Mathf.CeilToInt(z0 / 32f); bz < Mathf.FloorToInt(z1 / 32f); bz++)
                for (int bx = Mathf.CeilToInt(x0 / 32f); bx < Mathf.FloorToInt(x1 / 32f); bx++) { gridMean += gridWind[bz * columns + bx]; gridCount++; }
            for (int z = 0; z < d.Nz; z++)
            {
                float worldZ = d.OriginXZ.y + (z + 0.5f) * d.CellSize;
                if (worldZ < Mathf.CeilToInt(z0 / 32f) * 32f || worldZ > Mathf.FloorToInt(z1 / 32f) * 32f) continue;
                for (int x = 0; x < d.Nx; x++)
                {
                    float worldX = d.OriginXZ.x + (x + 0.5f) * d.CellSize;
                    if (worldX < Mathf.CeilToInt(x0 / 32f) * 32f || worldX > Mathf.FloorToInt(x1 / 32f) * 32f) continue;
                    nestMean += nestWind[z * d.Nx + x];
                    nestCount++;
                }
            }
            gridMean /= gridCount;
            nestMean /= nestCount;
            Debug.Log($"[FireCA test] Surface Wind inside the box: mean {gridMean} m/s over {gridCount} columns of the Wind Grid, " +
                $"mean {nestMean} m/s over {nestCount} columns of the Wind Nest.");
            Assert.Greater(gridCount, 400);
            // The Surface Wind of the Wind Grid is one Wind step older than the Surface Wind of the Wind Nest.
            Assert.Less((gridMean - nestMean).magnitude, 0.25f);

            // Criterion 6: the memory of the Wind Nest.
            float megabytes = nest.TotalBytes / (1024f * 1024f);
            Debug.Log($"[FireCA test] Wind Nest texture memory: {megabytes:F0} MB.");
            Assert.That(megabytes, Is.InRange(200f, 450f));

            // The Wind Nest goes away when the user turns it off.
            _app.WindNestEnabled = false;
            Assert.IsFalse(nest.Active);
            wind.Step();
            Assert.AreEqual(0, wind.ReadStats().NonFiniteCells);
        }
    }
}
