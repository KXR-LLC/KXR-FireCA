using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace FireCA.Tests
{
    /// <summary>PlayMode tests of milestone M4: fire coupling, smoke, and the stability of the coupled run.</summary>
    public sealed class FireWindMilestoneTests
    {
        const string DemoScenario = "synthetic-canyon-01";
        const float Cell = GridSpec.FireCellSizeM;
        const float WindCell = GridSpec.WindCellSizeM;

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
            _app.Spread.SynchronousReadback = true;
            return files;
        }

        // Runs the coupled simulation in the order of the frame loop: one Wind step, the Weather Model, then Fire steps.
        // Returns the time at which the stop condition became true, or the end time.
        double RunCoupled(double fromS, double toS, Func<double, bool> stop = null)
        {
            WindSolver wind = _app.Wind;
            SpreadSolver spread = _app.Spread;
            double sinceFull = 0.0;
            int step = 0;
            for (double t = fromS; t < toS - 1e-6; t += wind.DtWind)
            {
                wind.Step();
                sinceFull += wind.DtWind;
                if (_app.Weather.Update(t) || sinceFull >= 30.0)
                {
                    spread.RunBehavior(true);
                    sinceFull = 0.0;
                }
                spread.RunUntil(t + wind.DtWind);
                if (++step % 50 == 0) wind.ReadStats();
                if (stop != null && stop(t + wind.DtWind)) return t + wind.DtWind;
            }
            return toS;
        }

        // ---- criteria 1 and 2 ----

        [UnityTest, Timeout(900000)]
        public IEnumerator Criteria1And2_BurningDiscMakesAPlume_AndPullsAirIn()
        {
            // Flat terrain, fuel 4, a background wind of 0. The Fire Grid has 1024 cells per side, so the point
            // 200 m outside the disc edge is outside the lateral relaxation zone of the Wind Grid.
            const int n = 1024;
            const float discRadius = 150f;
            var data = ScenarioData.CreateDefault("disc", n);
            data.ignitions = Array.Empty<ScenarioData.IgnitionData>();
            data.weather.wind = new[] { new ScenarioData.WindEntry { hour = 0f, speedMps = 0f, fromDeg = 225f } };
            var elevation = new float[n * n];
            var fuel = new byte[n * n];
            for (int k = 0; k < n * n; k++) { elevation[k] = 1200f; fuel[k] = 4; }
            var files = new ScenarioFiles
            {
                Id = "disc", Data = data, Elevation = elevation, Fuel = fuel,
                MinElevation = 1200f, MaxElevation = 1200f, Grid = new GridSpec(n, 1200f),
            };
            Assert.IsTrue(_app.LoadScenarioFiles(files));
            _app.Spread.SynchronousReadback = true;
            WindSolver wind = _app.Wind;
            Assert.AreEqual(1f, wind.CouplingGain);

            float extent = n * Cell;
            Vector2 center = new Vector2(extent * 0.5f, extent * 0.5f);
            _app.Spread.Ignite(center, discRadius, 0.0);
            Assert.Greater(_app.Spread.IgnitedCells, 15000, "The disc did not ignite.");

            int columns = files.Grid.WindCellsX;
            var velocity = (RenderTexture)_app.Registry.Get(SimTextures.Velocity);
            float bestUp = 0f, inflowAtEnd = 0f, inflowBest = float.NegativeInfinity;
            int firstStepOver5 = -1;
            var series = new System.Text.StringBuilder();
            int samplesWithBoth = 0, lastStepWithBoth = -1;
            for (int step = 10; step <= 300; step += 10)
            {
                RunCoupled((step - 10) * (double)wind.DtWind, step * (double)wind.DtWind);
                velocity = (RenderTexture)_app.Registry.Get(SimTextures.Velocity);

                // The largest vertical velocity above the disc.
                var request = AsyncGPUReadback.Request(velocity, 0, 0, columns, 0, GridSpec.WindCellsY, 0, columns, TextureFormat.RGBAFloat);
                request.WaitForCompletion();
                Assert.IsFalse(request.hasError);
                float up = 0f;
                for (int z = 0; z < columns; z++)
                {
                    NativeArray<Vector4> layer = request.GetData<Vector4>(z);
                    for (int x = 0; x < columns; x++)
                    {
                        Vector2 p = new Vector2((x + 0.5f) * WindCell, (z + 0.5f) * WindCell);
                        if (Vector2.Distance(p, center) > discRadius) continue;
                        for (int y = 0; y < GridSpec.WindCellsY; y++) up = Mathf.Max(up, layer[y * columns + x].y);
                    }
                }
                bestUp = Mathf.Max(bestUp, up);
                if (firstStepOver5 < 0 && up > 5f) firstStepOver5 = step;

                // The Surface Wind 200 m outside the disc edge, at 8 points: the component toward the disc center.
                var windRequest = AsyncGPUReadback.Request(_app.Registry.Get(SimTextures.SurfaceWind), 0, TextureFormat.RGFloat);
                windRequest.WaitForCompletion();
                NativeArray<Vector2> surfaceWind = windRequest.GetData<Vector2>();
                float inflow = float.PositiveInfinity;
                for (int k = 0; k < 8; k++)
                {
                    float angle = k * Mathf.PI / 4f;
                    Vector2 outward = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle));
                    Vector2 p = center + outward * (discRadius + 200f);
                    Vector2 v = surfaceWind[(int)(p.y / WindCell) * columns + (int)(p.x / WindCell)];
                    inflow = Mathf.Min(inflow, Vector2.Dot(v, -outward));
                }
                inflowBest = Mathf.Max(inflowBest, inflow);
                inflowAtEnd = inflow;
                series.Append($"{step}:{up:F1}/{inflow:F2} ");
                if (up > 5f && inflow >= 0.5f) { samplesWithBoth++; lastStepWithBoth = step; }
                yield return null;
            }
            WindStats stats = wind.ReadStats();
            Debug.Log($"[FireCA test] Plume: largest vertical velocity above the disc {bestUp:F1} m/s, over 5 m/s from Wind step {firstStepOver5}. " +
                $"Peak speed {stats.PeakSpeed:F1} m/s, highest theta {stats.ThetaMax:F0} K.");
            Debug.Log($"[FireCA test] Inflow: the Surface Wind 200 m outside the disc edge blows toward the disc center at {inflowAtEnd:F2} m/s or more " +
                $"at Wind step 300 (lowest of 8 points). Largest value of the run {inflowBest:F2} m/s.");
            Debug.Log("[FireCA test] Series (Wind step:vertical velocity m/s/inflow m/s): " + series);
            Assert.Greater(bestUp, 5f, "The plume must reach a vertical velocity over 5 m/s within 300 Wind steps.");
            // The inflow is measured while the plume builds. The inflow fades after about 150 Wind steps (decision D24).
            Debug.Log($"[FireCA test] Inflow of 0.5 m/s or more at all 8 points, together with a plume over 5 m/s: {samplesWithBoth} of 30 samples, the last at Wind step {lastStepWithBoth}.");
            Assert.GreaterOrEqual(samplesWithBoth, 3, "The Surface Wind 200 m outside the disc edge must blow toward the disc center at 0.5 m/s or more while the plume rises.");
            Assert.AreEqual(0, stats.NonFiniteCells);
        }

        // ---- criterion 3 ----

        float ArrivalNear(Vector2 pointXZ)
        {
            int n = _app.Scenario.Grid.FireCells;
            int ci = Mathf.Clamp((int)(pointXZ.x / Cell) - 3, 0, n - 7), cj = Mathf.Clamp((int)(pointXZ.y / Cell) - 3, 0, n - 7);
            var request = AsyncGPUReadback.Request(_app.Registry.Get(SimTextures.ArrivalTime), 0, ci, 7, cj, 7, 0, 1, TextureFormat.RFloat);
            request.WaitForCompletion();
            float best = float.PositiveInfinity;
            foreach (float a in request.GetData<float>()) best = Mathf.Min(best, a);
            return best;
        }

        // A fire from the default ignition with the Wind Solver on. Returns the time at which the fire reaches the top of the chute.
        double TimeToChuteTop(float couplingGain, out double baseTime, out Vector2 chuteWind)
        {
            ScenarioFiles s = LoadDemo();
            WindSolver wind = _app.Wind;
            wind.CouplingGain = couplingGain;
            wind.SpinUp();
            Assert.IsTrue(s.Data.TryGetLandmark(ScenarioData.LandmarkChuteBase, out Vector2 chuteBase));
            Assert.IsTrue(s.Data.TryGetLandmark(ScenarioData.LandmarkChuteTop, out Vector2 chuteTop));
            var ignition = s.Data.ignitions[0];
            _app.Spread.Ignite(new Vector2(ignition.xM, ignition.zM), ignition.radiusM, 0.0);

            const double limit = 4.0 * 3600.0;
            double reachedBase = -1.0;
            Vector2 windSum = Vector2.zero;
            int windSamples = 0;
            Vector2 mid = Vector2.Lerp(chuteBase, chuteTop, 0.5f);
            int columns = s.Grid.WindCellsX;
            double top = RunCoupled(0.0, limit, t =>
            {
                if (((long)Math.Round(t)) % 30 != 0) return false;
                if (reachedBase < 0.0 && ArrivalNear(chuteBase + (chuteTop - chuteBase).normalized * 100f) < 1e29f) reachedBase = t;
                if (reachedBase >= 0.0)
                {
                    // The Surface Wind in the middle of the chute, while the fire climbs the chute.
                    var request = AsyncGPUReadback.Request(_app.Registry.Get(SimTextures.SurfaceWind), 0,
                        (int)(mid.x / WindCell), 1, (int)(mid.y / WindCell), 1, 0, 1, TextureFormat.RGFloat);
                    request.WaitForCompletion();
                    windSum += request.GetData<Vector2>()[0];
                    windSamples++;
                }
                return ArrivalNear(chuteTop) < 1e29f;
            });
            baseTime = reachedBase;
            chuteWind = windSamples > 0 ? windSum / windSamples : Vector2.zero;
            Assert.AreEqual(0, wind.ReadStats().NonFiniteCells);
            return ArrivalNear(chuteTop) < 1e29f ? top : double.PositiveInfinity;
        }

        [UnityTest, Timeout(1800000)]
        public IEnumerator Criterion3_FireCouplingSpeedsTheFireUpTheChute()
        {
            double coupled = TimeToChuteTop(1f, out double coupledBase, out Vector2 coupledWind);
            yield return null;
            double uncoupled = TimeToChuteTop(0f, out double uncoupledBase, out Vector2 uncoupledWind);
            yield return null;
            Debug.Log($"[FireCA test] Chute, couplingGain 1: top reached at {coupled / 60.0:F1} min (chute entered at {coupledBase / 60.0:F1} min). " +
                $"Mean Surface Wind in the chute ({coupledWind.x:F2}, {coupledWind.y:F2}) m/s.");
            Debug.Log($"[FireCA test] Chute, couplingGain 0: top reached at {uncoupled / 60.0:F1} min (chute entered at {uncoupledBase / 60.0:F1} min). " +
                $"Mean Surface Wind in the chute ({uncoupledWind.x:F2}, {uncoupledWind.y:F2}) m/s.");
            Assert.IsFalse(double.IsInfinity(coupled), "The coupled fire did not reach the top of the chute in 4 hours.");
            Assert.Less(coupled, uncoupled, "The fire must reach the top of the chute sooner with couplingGain 1 than with couplingGain 0.");
        }

        // ---- criteria 4 and 5 ----

        [UnityTest, Timeout(1800000)]
        public IEnumerator Criteria4And5_SmokeRisesAndBends_AndTheCoupledRunIsStable()
        {
            ScenarioFiles s = LoadDemo();
            WindSolver wind = _app.Wind;
            wind.SpinUp();
            var ignition = s.Data.ignitions[0];
            _app.Spread.Ignite(new Vector2(ignition.xM, ignition.zM), ignition.radiusM, 0.0);

            float peak = 0f, thetaHigh = 0f, thetaLow = 0f, smokeHigh = 0f;
            float wallStart = Time.realtimeSinceStartup;
            const int hours = 6, blocksPerHour = 6;
            for (int block = 0; block < hours * blocksPerHour; block++)
            {
                double from = block * 3600.0 / blocksPerHour;
                RunCoupled(from, from + 3600.0 / blocksPerHour);
                WindStats stats = wind.ReadStats();
                Assert.AreEqual(0, stats.NonFiniteCells, $"The Wind Grid holds a NaN value at {from / 3600.0 + 1.0 / blocksPerHour:F2} h.");
                Assert.GreaterOrEqual(stats.ThetaMin, wind.ThetaMin - 1e-3f, "theta is under the clamp range.");
                Assert.LessOrEqual(stats.ThetaMax, wind.ThetaMax + 1e-3f, "theta is over the clamp range.");
                peak = Mathf.Max(peak, stats.PeakSpeed);
                thetaHigh = Mathf.Max(thetaHigh, stats.ThetaMax);
                thetaLow = Mathf.Min(thetaLow, stats.ThetaMin);
                smokeHigh = Mathf.Max(smokeHigh, stats.SmokeMax);

                if (block == blocksPerHour - 1) CheckSmoke(s);   // after 1 simulated hour
                yield return null;
            }
            float wall = Time.realtimeSinceStartup - wallStart;

            foreach (var spec in TextureRegistry.Specs)
            {
                if (spec.Grid == TextureRegistry.GridKind.Wind) continue;
                Assert.AreEqual(0, _app.Spread.CountNonFinite(_app.Registry.Get(spec.Name)), $"'{spec.Name}' holds a NaN value or an infinite value.");
            }
            Debug.Log($"[FireCA test] Coupled run: 6 simulated hours with no NaN value, in {wall:F0} s. {wind.StepCount} Wind steps, {_app.Spread.StepCount} Fire steps. " +
                $"Burned area {_app.Spread.BurnedAreaHa:F0} ha. theta from {thetaLow:F1} K to {thetaHigh:F1} K (clamp range {wind.ThetaMin} to {wind.ThetaMax}). " +
                $"Peak wind speed {peak:F1} m/s. Highest smoke density {smokeHigh:F1}.");
        }

        // Criterion 4, measured: the smoke rises from the fire and bends with the wind.
        void CheckSmoke(ScenarioFiles s)
        {
            int columns = s.Grid.WindCellsX;
            var smokeTexture = (RenderTexture)_app.Registry.Get(SimTextures.Smoke);
            var request = AsyncGPUReadback.Request(smokeTexture, 0, 0, columns, 0, GridSpec.WindCellsY, 0, columns, TextureFormat.RFloat);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError);
            var heatRequest = AsyncGPUReadback.Request(_app.Registry.Get(SimTextures.HeatFluxCoarse), 0, TextureFormat.RFloat);
            heatRequest.WaitForCompletion();
            NativeArray<float> heat = heatRequest.GetData<float>();
            float[] ground = _app.GroundHeight;

            double total = 0, sx = 0, sz = 0, sh = 0, heatTotal = 0, hx = 0, hz = 0;
            for (int z = 0; z < columns; z++)
            {
                NativeArray<float> layer = request.GetData<float>(z);
                for (int x = 0; x < columns; x++)
                {
                    double q = heat[z * columns + x];
                    heatTotal += q; hx += q * (x + 0.5) * WindCell; hz += q * (z + 0.5) * WindCell;
                    for (int y = 0; y < GridSpec.WindCellsY; y++)
                    {
                        double m = layer[y * columns + x];
                        if (m <= 0.0) continue;
                        total += m;
                        sx += m * (x + 0.5) * WindCell;
                        sz += m * (z + 0.5) * WindCell;
                        sh += m * (s.Grid.WindBottomY + (y + 0.5) * WindCell - ground[z * columns + x]);
                    }
                }
            }
            Assert.Greater(total, 0.0, "The fire made no smoke.");
            Assert.Greater(heatTotal, 0.0, "The fire releases no heat.");
            Vector2 fire = new Vector2((float)(hx / heatTotal), (float)(hz / heatTotal));
            Vector2 smoke = new Vector2((float)(sx / total), (float)(sz / total));
            float height = (float)(sh / total);
            Vector2 drift = smoke - fire;
            Vector2 background = _app.Weather.BackgroundWind;
            float downwind = Vector2.Dot(drift, background.normalized);
            Debug.Log($"[FireCA test] Smoke after 1 hour: the mean height above the ground is {height:F0} m. The smoke center is {drift.magnitude:F0} m from the fire center, " +
                $"{downwind:F0} m of that downwind. Background wind ({background.x:F1}, {background.y:F1}) m/s.");
            Assert.Greater(height, 100f, "The smoke must rise from the fire.");
            Assert.Greater(downwind, 100f, "The smoke must bend with the wind.");
        }

        // ---- Smoke Pass and Clear Fire ----

        [UnityTest]
        public IEnumerator SmokePass_IsInTheScene_AndClearFireClearsTheSmoke()
        {
            yield return null;
            var volume = UnityEngine.Object.FindFirstObjectByType<CustomPassVolume>();
            Assert.IsNotNull(volume, "The scene has no Custom Pass volume.");
            Assert.AreEqual(CustomPassInjectionPoint.BeforePostProcess, volume.injectionPoint);
            bool found = false;
            foreach (CustomPass pass in volume.customPasses) found |= pass is SmokePass;
            Assert.IsTrue(found, "The Custom Pass volume has no Smoke Pass.");

            Assert.IsTrue(_app.LoadScenario("test-canyon-512"), _app.LastError);
            _app.Spread.SynchronousReadback = true;
            var ignition = _app.Scenario.Data.ignitions[0];
            _app.Spread.Ignite(new Vector2(ignition.xM, ignition.zM), 30f, 0.0);
            RunCoupled(0.0, 240.0);
            Assert.Greater(_app.Wind.ReadStats().SmokeTotal, 0.0, "The fire made no smoke.");
            _app.ClearFire();
            Assert.AreEqual(0.0, _app.Wind.ReadStats().SmokeTotal, "Clear Fire must clear the Smoke texture.");
            Assert.AreEqual(0, _app.Spread.IgnitedCells);
        }
    }
}
