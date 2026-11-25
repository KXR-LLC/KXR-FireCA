using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace FireCA.Tests
{
    /// <summary>
    /// PlayMode tests of milestone M6. The Scenario south-canyon comes from tools/import_canyon.py, from a USGS 3DEP
    /// elevation raster and a LANDFIRE FBFM13 raster of Storm King Mountain, Colorado.
    /// </summary>
    public sealed class RealCanyonMilestoneTests
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
        }

        static string Folder => Path.Combine(ScenarioLoader.ScenariosRoot, RealScenario);

        // ---- criterion 1 ----

        [Test]
        public void Criterion1_ScenarioFromTheScriptPassesEveryValidationCheck()
        {
            Assert.IsTrue(Directory.Exists(Folder), "The Scenario south-canyon does not exist. Run tools/import_canyon.py.");
            ScenarioFiles s = null;
            Assert.DoesNotThrow(() => s = ScenarioLoader.Load(Folder));
            Assert.AreEqual(4096, s.Grid.FireCells);
            Assert.AreEqual(2.0f, s.Data.cellSizeM);
            Assert.AreEqual(39.578f, s.Data.latitudeDeg, 0.01f, "The script must write the latitude of the site.");
            Assert.Less(s.MaxElevation - s.MinElevation, 3000f);

            // The fuel codes are LANDFIRE FBFM13 codes, with no remapping.
            var counts = new long[256];
            foreach (byte code in s.Fuel) counts[code]++;
            long burnable = 0;
            for (int code = 0; code < 256; code++)
            {
                if (counts[code] == 0) continue;
                bool known = FuelCodes.IsBurnable(code) || code == FuelCodes.Urban || code == FuelCodes.SnowOrIce
                    || code == FuelCodes.Agriculture || code == FuelCodes.Water || code == FuelCodes.Barren;
                Assert.IsTrue(known, $"The fuel code {code} is not a LANDFIRE FBFM13 code.");
                if (FuelCodes.IsBurnable(code)) burnable += counts[code];
            }
            Debug.Log($"[FireCA test] south-canyon: elevation {s.MinElevation:F0} m to {s.MaxElevation:F0} m, " +
                $"{100.0 * burnable / s.Fuel.Length:F1} percent of the cells burnable, latitude {s.Data.latitudeDeg}.");
            Assert.Greater(burnable, s.Fuel.Length / 2);
        }

        // ---- criterion 2 ----

        [UnityTest, Timeout(1800000)]
        public IEnumerator Criterion2_FireWeatherAndWindRunWithNoCodeChange()
        {
            Assert.IsTrue(_app.LoadScenario(RealScenario), _app.LastError);
            ScenarioFiles s = _app.Scenario;
            WindSolver wind = _app.Wind;
            SpreadSolver spread = _app.Spread;
            WeatherModel weather = _app.Weather;
            spread.SynchronousReadback = true;
            Debug.Log($"[FireCA test] south-canyon: loaded in {_app.LastLoadSeconds:F2} s. Wind Grid bottom {s.Grid.WindBottomY:F0} m.");
            Assert.Less(_app.LastLoadSeconds, 10f);

            // The camera presets come from the fallback, because the Scenario has no cameraPresets.
            var rig = Object.FindFirstObjectByType<CameraRig>();
            yield return null;
            Assert.AreEqual(4, rig.Presets.Count);
            for (int i = 0; i < 4; i++)
            {
                rig.ApplyPreset(i);
                yield return null;
                Vector3 p = rig.transform.position;
                Assert.IsFalse(float.IsNaN(p.x + p.y + p.z), $"Camera preset {rig.Presets[i].Name} is not valid.");
            }

            wind.SpinUp();
            var ignition = s.Data.ignitions[0];
            spread.Ignite(new Vector2(ignition.xM, ignition.zM), ignition.radiusM, 0.0);
            Assert.Greater(spread.IgnitedCells, 0, "The ignition of the Scenario is not on burnable fuel.");
            float moistureStart = weather.M10;

            // The frame loop order: one Wind step, the Weather Model, then Fire steps. 5 simulated hours.
            const double hours = 5.0;
            double sinceFull = 0.0;
            int step = 0;
            float peak = 0f;
            float wallStart = Time.realtimeSinceStartup;
            for (double t = 0.0; t < hours * 3600.0 - 1e-6; t += wind.DtWind)
            {
                wind.Step();
                sinceFull += wind.DtWind;
                if (weather.Update(t) || sinceFull >= 30.0) { spread.RunBehavior(true); sinceFull = 0.0; }
                spread.RunUntil(t + wind.DtWind);
                if (++step % 900 == 0)
                {
                    WindStats stats = wind.ReadStats();
                    Assert.AreEqual(0, stats.NonFiniteCells, $"The Wind Grid holds a NaN value at {t / 3600.0:F2} h.");
                    Assert.That(stats.ThetaMax, Is.LessThanOrEqualTo(wind.ThetaMax + 1e-3f));
                    peak = Mathf.Max(peak, stats.PeakSpeed);
                    Debug.Log($"[FireCA test] south-canyon at {(t + wind.DtWind) / 3600.0:F1} h: {spread.BurnedAreaHa:F0} ha burned, " +
                        $"{spread.ActiveBlocks} active blocks, background wind {weather.WindSpeedMps:F1} m/s from {weather.WindFromDeg:F0} degrees.");
                    yield return null;
                }
                else if (step % 50 == 0) wind.ReadStats();
            }
            float wall = Time.realtimeSinceStartup - wallStart;

            // Fire: the fire grew, and no texture holds a NaN value.
            Assert.Greater(spread.BurnedAreaHa, 1.0, "The fire did not grow.");
            foreach (var spec in TextureRegistry.Specs)
            {
                if (spec.Grid == TextureRegistry.GridKind.Wind) continue;
                Assert.AreEqual(0, spread.CountNonFinite(_app.Registry.Get(spec.Name)), $"'{spec.Name}' holds a NaN value or an infinite value.");
            }

            // Weather: the Sim Clock reached 17:00 solar time, and the fuel moisture changed.
            Assert.AreEqual(12.0 + hours, weather.SolarHour, 0.05);
            Assert.AreNotEqual(moistureStart, weather.M10);
            Assert.Greater(weather.InsolationRecomputes, 50);

            // Wind: the Surface Wind has the terrain in it. The values differ across the Fire Grid.
            var request = AsyncGPUReadback.Request(_app.Registry.Get(SimTextures.SurfaceWind), 0, TextureFormat.RGFloat);
            request.WaitForCompletion();
            float low = float.PositiveInfinity, high = 0f;
            foreach (Vector2 v in request.GetData<Vector2>()) { low = Mathf.Min(low, v.magnitude); high = Mathf.Max(high, v.magnitude); }
            Assert.Greater(high - low, 1f, "The Surface Wind does not vary with the terrain.");
            Assert.Less(peak, 100f);

            Debug.Log($"[FireCA test] south-canyon: {hours} simulated hours in {wall:F0} s, {wind.StepCount} Wind steps, {spread.StepCount} Fire steps. " +
                $"Burned area {spread.BurnedAreaHa:F0} ha ({spread.BurnedAreaHa * 2.471:F0} acres). Peak wind speed {peak:F1} m/s. " +
                $"Surface Wind from {low:F1} m/s to {high:F1} m/s. m10 {weather.M10 * 100f:F1} percent.");
        }
    }
}
