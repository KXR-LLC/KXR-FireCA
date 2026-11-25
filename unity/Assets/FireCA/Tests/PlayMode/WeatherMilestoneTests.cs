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
    /// <summary>PlayMode tests of milestone M2: Insolation, fuel moisture, the sun light, and manual weather.</summary>
    public sealed class WeatherMilestoneTests
    {
        const string DemoScenario = "synthetic-canyon-01";
        const float Cell = GridSpec.FireCellSizeM;

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
            // The fire tests and the weather tests use a uniform Surface Wind. The Wind Solver is off.
            _app.WindSolverEnabled = false;
        }

        // Loads synthetic-canyon-01 with changes to the scenario.json values.
        ScenarioFiles LoadDemo(Action<ScenarioData> edit = null)
        {
            ScenarioFiles files = ScenarioLoader.Load(Path.Combine(ScenarioLoader.ScenariosRoot, DemoScenario));
            edit?.Invoke(files.Data);
            Assert.IsTrue(_app.LoadScenarioFiles(files));
            _app.Spread.SynchronousReadback = true;
            return files;
        }

        static float[] Readback(Texture texture)
        {
            var request = AsyncGPUReadback.Request(texture, 0, TextureFormat.RFloat);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError, $"The readback of '{texture.name}' failed.");
            return request.GetData<float>().ToArray();
        }

        // Runs the Weather Model from one time to another, in steps of 60 simulated seconds.
        void RunWeather(double fromS, double toS)
        {
            for (double t = fromS; t <= toS + 1e-6; t += WeatherModel.UpdateIntervalS) _app.Weather.Update(t);
        }

        static void Gradient(float[] e, int n, int i, int j, out float dhdx, out float dhdz)
        {
            int i0 = Mathf.Max(i - 1, 0), i1 = Mathf.Min(i + 1, n - 1), j0 = Mathf.Max(j - 1, 0), j1 = Mathf.Min(j + 1, n - 1);
            dhdx = (e[j * n + i1] - e[j * n + i0]) / ((i1 - i0) * Cell);
            dhdz = (e[j1 * n + i] - e[j0 * n + i]) / ((j1 - j0) * Cell);
        }

        // ---- criterion 2 ----

        [UnityTest]
        public IEnumerator Criterion2_MorningSunLightsEastFacingSlopes()
        {
            ScenarioFiles s = LoadDemo(d => d.startSolarHour = 7.0f);
            yield return null;
            int n = s.Grid.FireCells;
            float[] insolation = Readback(_app.Registry.Get(SimTextures.Insolation));

            double east = 0, west = 0;
            long eastCount = 0, westCount = 0;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    Gradient(s.Elevation, n, i, j, out float dhdx, out float dhdz);
                    if (dhdx * dhdx + dhdz * dhdz <= 0.4f * 0.4f) continue;
                    // The azimuth that the slope faces: the downslope direction.
                    float aspect = Mathf.Atan2(-dhdx, -dhdz) * Mathf.Rad2Deg;
                    if (aspect < 0f) aspect += 360f;
                    if (aspect >= 45f && aspect <= 135f) { east += insolation[j * n + i]; eastCount++; }
                    else if (aspect >= 225f && aspect <= 315f) { west += insolation[j * n + i]; westCount++; }
                }
            }
            Assert.Greater(eastCount, 10000);
            Assert.Greater(westCount, 10000);
            double eastMean = east / eastCount, westMean = west / westCount;
            Debug.Log($"[FireCA test] Insolation at 07:00 on slopes over 40 percent: east-facing {eastMean:F0} W/m2 ({eastCount} cells), " +
                $"west-facing {westMean:F0} W/m2 ({westCount} cells).");
            Assert.Greater(eastMean, 50.0);
            Assert.GreaterOrEqual(eastMean, 3.0 * westMean);
        }

        // ---- GPU against CPU ----

        [UnityTest]
        public IEnumerator Insolation_AndMoisture_MatchTheCpuEquations()
        {
            ScenarioFiles s = LoadDemo();
            yield return null;
            int n = s.Grid.FireCells;
            ScenarioData d = s.Data;
            WeatherModel weather = _app.Weather;

            // Insolation of ground that no terrain shadows: dni * dot(normal, sun).
            float[] insolation = Readback(_app.Registry.Get(SimTextures.Insolation));
            WeatherEquations.SunDirection(d.latitudeDeg, d.dayOfYear, d.startSolarHour, out double sx, out double sy, out double sz);
            double dni = WeatherEquations.DirectNormalIrradiance(sy);
            int checkedCells = 0;
            for (int j = 60; j < 400; j += 17)
            {
                for (int i = 60; i < 400; i += 17)
                {
                    Gradient(s.Elevation, n, i, j, out float dhdx, out float dhdz);
                    Vector3 normal = new Vector3(-dhdx, 1f, -dhdz).normalized;
                    double expected = dni * Math.Max(0.0, normal.x * sx + normal.y * sy + normal.z * sz);
                    Assert.AreEqual(expected, insolation[j * n + i], Math.Max(2.0, expected * 0.01), $"Insolation of cell ({i}, {j}).");
                    checkedCells++;
                }
            }
            Assert.Greater(checkedCells, 100);
            Assert.AreEqual((float)sy, weather.SunDirection.y, 1e-5f);

            // Moisture 1h after one update of 60 simulated seconds.
            float m0 = d.moisture.m1Pct / 100f;
            weather.Update(60.0);
            float[] moisture = Readback(_app.Registry.Get(SimTextures.Moisture1h));
            double midflame = WeatherEquations.MoistureWindFactor * weather.BackgroundWind.magnitude;
            double solarHour = WeatherEquations.SolarHour(d.startSolarHour, 60.0);
            double tRef = WeatherEquations.ReferenceTempC(d.weather.tempMaxC, d.weather.tempMinC, solarHour);
            double vapor = WeatherEquations.VaporPressure(d.weather.rhMinPct, d.weather.tempMaxC);
            double worst = 0.0;
            for (int j = 13; j < n; j += 211)
            {
                for (int i = 7; i < n; i += 197)
                {
                    double air = WeatherEquations.AirTempC(tRef, d.weather.lapseRateCPerKm, s.Elevation[j * n + i], d.weather.referenceElevationM);
                    double rh = WeatherEquations.RelativeHumidityPct(vapor, air);
                    double emc = WeatherEquations.FuelLevelEmc(air, rh, insolation[j * n + i], midflame);
                    double expected = WeatherEquations.Relax(m0, emc, 60.0, WeatherEquations.Tau1hS);
                    expected = Math.Min(WeatherEquations.MaxMoisture1h, Math.Max(WeatherEquations.MinMoisture1h, expected));
                    worst = Math.Max(worst, Math.Abs(expected - moisture[j * n + i]));
                }
            }
            Debug.Log($"[FireCA test] Moisture 1h, GPU against CPU after one update: largest difference {worst * 100.0:F4} percent moisture.");
            Assert.Less(worst, 1e-4, "The Moisture 1h kernel does not match the CPU equations.");

            // m10 and m100 move toward the EMC at the reference elevation.
            double emcRef = WeatherEquations.FuelLevelEmc(tRef, WeatherEquations.RelativeHumidityPct(vapor, tRef), 0.0, 0.0);
            Assert.AreEqual(WeatherEquations.Relax(d.moisture.m10Pct / 100.0, emcRef, 60.0, WeatherEquations.Tau10hS), weather.M10, 1e-6);
            Assert.AreEqual(WeatherEquations.Relax(d.moisture.m100Pct / 100.0, emcRef, 60.0, WeatherEquations.Tau100hS), weather.M100, 1e-6);
            Assert.AreEqual(d.moisture.livePct / 100f, weather.MLive, "The Weather Model must hold mLive at the Scenario value.");
        }

        // ---- criterion 3 ----

        [UnityTest]
        public IEnumerator Criterion3_SunlitFuelIsDrierThanShadedFuelAtTheSameElevation()
        {
            ScenarioFiles s = LoadDemo();   // starts at 13:00 solar time
            yield return null;
            RunWeather(0.0, 2.0 * 3600.0);  // to 15:00
            Assert.AreEqual(15.0, _app.Weather.SolarHour, 1e-6);
            int n = s.Grid.FireCells;
            float[] insolation = Readback(_app.Registry.Get(SimTextures.Insolation));
            float[] moisture = Readback(_app.Registry.Get(SimTextures.Moisture1h));

            // Elevation bands of 20 m. A sunlit cell has an Insolation over 500 W/m2. A shaded cell has under 150 W/m2.
            const float band = 20f;
            int bands = Mathf.CeilToInt((s.MaxElevation - s.MinElevation) / band) + 1;
            var sunSum = new double[bands]; var shadeSum = new double[bands];
            var sunCount = new long[bands]; var shadeCount = new long[bands];
            for (int k = 0; k < n * n; k++)
            {
                int b = (int)((s.Elevation[k] - s.MinElevation) / band);
                if (insolation[k] > 500f) { sunSum[b] += moisture[k]; sunCount[b]++; }
                else if (insolation[k] < 150f) { shadeSum[b] += moisture[k]; shadeCount[b]++; }
            }
            int compared = 0;
            double smallestGap = double.PositiveInfinity;
            for (int b = 0; b < bands; b++)
            {
                if (sunCount[b] < 500 || shadeCount[b] < 500) continue;
                double sun = sunSum[b] / sunCount[b], shade = shadeSum[b] / shadeCount[b];
                Assert.Less(sun, shade, $"Elevation band {s.MinElevation + b * band:F0} m: the sunlit fuel is not drier than the shaded fuel.");
                smallestGap = Math.Min(smallestGap, shade - sun);
                compared++;
            }
            Debug.Log($"[FireCA test] Moisture 1h at 15:00: sunlit fuel is drier than shaded fuel in {compared} elevation bands. " +
                $"Smallest difference {smallestGap * 100.0:F2} percent moisture.");
            Assert.Greater(compared, 10, "Too few elevation bands hold both sunlit cells and shaded cells.");
        }

        // ---- criterion 4 ----

        [UnityTest, Timeout(900000)]
        public IEnumerator Criterion4_MoistureAtTheReferenceElevationStaysBetween1And20Percent()
        {
            ScenarioFiles s = LoadDemo();
            yield return null;
            int n = s.Grid.FireCells;
            float reference = s.Data.weather.referenceElevationM;
            var cells = new System.Collections.Generic.List<int>();
            for (int k = 0; k < n * n; k++) if (Mathf.Abs(s.Elevation[k] - reference) <= 5f) cells.Add(k);
            Assert.Greater(cells.Count, 1000, "Too few cells are at referenceElevationM.");

            float low = float.PositiveInfinity, high = float.NegativeInfinity;
            float lowHour = 0f, highHour = 0f;
            float wallStart = Time.realtimeSinceStartup;
            for (int hour = 0; hour < 24; hour++)
            {
                RunWeather(hour * 3600.0 + 60.0, (hour + 1) * 3600.0);
                float[] moisture = Readback(_app.Registry.Get(SimTextures.Moisture1h));
                foreach (int k in cells)
                {
                    float m = moisture[k];
                    if (float.IsNaN(m)) Assert.Fail("Moisture 1h holds a NaN value.");
                    if (m < low) { low = m; lowHour = (float)_app.Weather.SolarHour; }
                    if (m > high) { high = m; highHour = (float)_app.Weather.SolarHour; }
                }
                yield return null;
            }
            Debug.Log($"[FireCA test] Moisture 1h at {reference:F0} m over 24 simulated hours: {low * 100f:F2} percent (at {lowHour:F0}:00) to " +
                $"{high * 100f:F2} percent (at {highHour:F0}:00). {cells.Count} cells. {_app.Weather.InsolationRecomputes} Insolation recomputes. " +
                $"{Time.realtimeSinceStartup - wallStart:F0} s. m10 {_app.Weather.M10 * 100f:F2}, m100 {_app.Weather.M100 * 100f:F2} percent.");
            Assert.GreaterOrEqual(low, 0.01f);
            Assert.LessOrEqual(high, 0.20f);
            Assert.AreEqual(1 + 24 * 12, _app.Weather.InsolationRecomputes, "Insolation must recompute once per 300 simulated seconds.");
            Assert.AreEqual(0, _app.Spread.CountNonFinite(_app.Registry.Get(SimTextures.Moisture1h)));
            Assert.AreEqual(0, _app.Spread.CountNonFinite(_app.Registry.Get(SimTextures.Insolation)));
        }

        // ---- criterion 5 ----

        // A fire from the default ignition, with a constant background wind. The weather runs for 2 hours before the
        // ignition, so the fuel moisture follows the time of day. Returns the burned area of the 3 hours after the ignition.
        double BurnThreeHours(float ignitionSolarHour)
        {
            const double spinUpS = 2.0 * 3600.0, burnS = 3.0 * 3600.0;
            ScenarioFiles s = LoadDemo(d =>
            {
                d.startSolarHour = Mathf.Repeat(ignitionSolarHour - 2f, 24f);
                d.weather.wind = new[] { new ScenarioData.WindEntry { hour = 0f, speedMps = 4f, fromDeg = 225f } };
            });
            SpreadSolver spread = _app.Spread;
            var ignition = s.Data.ignitions[0];
            bool lit = false;
            for (double t = 0.0; t < spinUpS + burnS; t += WeatherModel.UpdateIntervalS)
            {
                if (_app.Weather.Update(t)) spread.RunBehavior(true);
                if (!lit && t >= spinUpS)
                {
                    spread.SetFireTime(t);
                    spread.Ignite(new Vector2(ignition.xM, ignition.zM), ignition.radiusM, t);
                    lit = true;
                }
                if (lit) spread.RunUntil(t + WeatherModel.UpdateIntervalS);
            }
            Assert.AreEqual(0, spread.CountNonFinite(_app.Registry.Get(SimTextures.SpreadA)));
            return spread.BurnedAreaHa;
        }

        [UnityTest, Timeout(900000)]
        public IEnumerator Criterion5_AfternoonFireBurnsMoreAreaThanNightFire()
        {
            double day = BurnThreeHours(13f);
            float dayM10 = _app.Weather.M10;
            yield return null;
            double night = BurnThreeHours(1f);
            yield return null;
            Debug.Log($"[FireCA test] Burned area with a constant wind: {day:F1} ha from 13:00 to 16:00, {night:F1} ha from 01:00 to 04:00. " +
                $"m10 at the end: {dayM10 * 100f:F2} percent (day), {_app.Weather.M10 * 100f:F2} percent (night).");
            Assert.Greater(night, 0.0, "The night fire did not burn.");
            Assert.Greater(day, night);
        }

        // ---- sun light, manual weather, Overlays ----

        [UnityTest]
        public IEnumerator SunLight_FollowsTheSunDirection_AndIsOffAtNight()
        {
            LoadDemo();
            yield return null;
            Light sun = _app.SunLight;
            Assert.IsNotNull(sun, "The scene has no sun light.");
            Vector3 toSun = _app.Weather.SunDirection;
            Assert.Greater(toSun.y, 0.8f, "At 13:00 the sun is high.");
            Assert.Less(Vector3.Angle(-sun.transform.forward, toSun), 0.5f, "The directional light must point along the sun direction.");
            float dayIntensity = sun.intensity;
            Assert.Greater(dayIntensity, 0f);

            LoadDemo(d => d.startSolarHour = 1.0f);
            yield return null;
            Assert.Less(_app.Weather.SunDirection.y, 0f);
            Assert.AreEqual(0f, sun.intensity, "The directional light must be off at night.");

            LoadDemo();
            yield return null;
            Assert.AreEqual(dayIntensity, sun.intensity, dayIntensity * 1e-4f, "A reload must restore the day intensity.");
        }

        [UnityTest]
        public IEnumerator ManualWeather_OverridesTheScenario_UntilScenarioWeatherReturns()
        {
            ScenarioFiles s = LoadDemo();
            yield return null;
            WeatherModel weather = _app.Weather;
            Assert.IsTrue(weather.UseScenarioWeather);
            Vector2 scenarioWind = weather.BackgroundWind;
            Assert.AreEqual(4f, scenarioWind.magnitude, 1e-3f);

            // A manual value applies at the next update, before 60 simulated seconds pass.
            weather.SetManualWind(10f, 90f);
            weather.SetManualTemperature(40f, 20f);
            weather.SetManualRhMin(8f);
            Assert.IsFalse(weather.UseScenarioWeather);
            Assert.IsTrue(weather.Update(1.0));
            Assert.AreEqual(-10f, weather.BackgroundWind.x, 1e-3f, "A wind from the east goes toward the west.");
            Assert.AreEqual(0f, weather.BackgroundWind.y, 1e-3f);
            Assert.AreEqual(40f, weather.TempMaxC);
            Assert.AreEqual(8f, weather.RhMinPct);

            // The manual value stays while the Sim Clock advances.
            weather.Update(3.5 * 3600.0);
            Assert.AreEqual(10f, weather.BackgroundWind.magnitude, 1e-3f);

            weather.RestoreScenarioWeather();
            Assert.IsTrue(weather.UseScenarioWeather);
            Assert.IsTrue(weather.Update(3.5 * 3600.0 + 1.0));
            Assert.AreEqual(7f, weather.BackgroundWind.magnitude, 1e-3f, "The Scenario wind at hour 3.5 is 7 m/s.");
            Assert.AreEqual(s.Data.weather.tempMaxC, weather.TempMaxC);
        }

        [Test]
        public void Overlays_WeatherOverlaysAreAvailable()
        {
            foreach (string name in new[] { "Fuel moisture", "Air temperature", "Relative humidity", "Insolation" })
            {
                OverlayInfo overlay = Overlays.Find(name);
                Assert.IsNotNull(overlay, name);
                Assert.IsTrue(overlay.Available, name);
                Assert.IsFalse(overlay.Categorical, name);
            }
            Assert.AreEqual("0 to 25", Overlays.Find("Fuel moisture").RangeText);
            Assert.AreEqual("0 to 1100", Overlays.Find("Insolation").RangeText);
            Assert.AreEqual("0 to 100", Overlays.Find("Relative humidity").RangeText);
        }
    }
}
