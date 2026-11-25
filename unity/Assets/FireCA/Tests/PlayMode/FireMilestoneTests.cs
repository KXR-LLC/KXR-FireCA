using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace FireCA.Tests
{
    /// <summary>
    /// PlayMode tests of milestone M1. The first six tests are the PlayMode tests of docs/02-fire-spread.md.
    /// Each of the six uses a 512 Fire Grid, turns the Weather Model off, and uses the moisture values 8, 8, 8, and 100 percent.
    /// </summary>
    public sealed class FireMilestoneTests
    {
        const int N = 512;
        const float Cell = GridSpec.FireCellSizeM;
        const float M1 = 0.08f, M10 = 0.08f, M100 = 0.08f, MLive = 1.0f;

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

        // ---- helpers ----

        static ScenarioFiles MakeScenario(string id, Func<int, int, float> elevation, Func<int, int, byte> fuel)
        {
            var data = ScenarioData.CreateDefault(id, N);
            data.ignitions = Array.Empty<ScenarioData.IgnitionData>();
            data.moisture = new ScenarioData.MoistureData { m1Pct = 8f, m10Pct = 8f, m100Pct = 8f, livePct = 100f };
            var e = new float[N * N];
            var f = new byte[N * N];
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int j = 0; j < N; j++)
            {
                for (int i = 0; i < N; i++)
                {
                    float h = elevation(i, j);
                    e[j * N + i] = h;
                    f[j * N + i] = fuel(i, j);
                    min = Mathf.Min(min, h);
                    max = Mathf.Max(max, h);
                }
            }
            ScenarioValidation.CheckElevationValues(e, out _, out _);
            return new ScenarioFiles
            {
                Id = id, Folder = null, Data = data, Elevation = e, Fuel = f,
                MinElevation = min, MaxElevation = max, Grid = new GridSpec(N, min),
            };
        }

        // Loads a test Scenario with the Weather Model off and a uniform wind at 6.1 m.
        void Load(ScenarioFiles files, Vector2 surfaceWind)
        {
            Assert.IsTrue(_app.LoadScenarioFiles(files));
            _app.Weather.Enabled = false;
            _app.Weather.SetMoisture(M1, M10, M100, MLive);
            _app.Weather.FillSurfaceWind(surfaceWind);
            _app.Spread.SynchronousReadback = true;
            _app.Spread.RunBehavior(true);
        }

        static float WindAdjustmentFactor(int fuelCode) => (float)FuelTable.Get(fuelCode).WindAdjustmentFactor;

        static T[] Readback<T>(Texture texture, TextureFormat format) where T : struct
        {
            var request = AsyncGPUReadback.Request(texture, 0, format);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError, $"The readback of '{texture.name}' failed.");
            return request.GetData<T>().ToArray();
        }

        float[] ReadArrival() => Readback<float>(_app.Registry.Get(SimTextures.ArrivalTime), TextureFormat.RFloat);

        static bool Ignited(float arrival, float time) => arrival <= time;

        static FireBehavior Reference(int fuel, double midflameMps, double windToDeg, double slope, double upslopeDeg) =>
            ReferenceModel.Spread(fuel, M1, M10, M100, MLive, midflameMps, windToDeg, slope, upslopeDeg);

        static void AssertWithin(double expected, double actual, double relative, double absolute, string what)
        {
            double tolerance = Math.Max(absolute, Math.Abs(expected) * relative);
            if (Math.Abs(actual - expected) > tolerance)
                Assert.Fail($"{what}: expected {expected:G6}, measured {actual:G6}, tolerance {tolerance:G3}.");
        }

        // ---- test 1: GPU match ----

        [Serializable] sealed class CaseFile { public Case[] cases; }
        [Serializable] sealed class Case { public CaseIn @in; }
        [Serializable]
        sealed class CaseIn
        {
            public int fuel;
            public double m1, m10, m100, mLive, midflameWindMps, windToDeg, slope, upslopeDeg;
        }

        [UnityTest]
        public IEnumerator Test1_GpuMatch_BehaviorPassMatchesTheReferenceModel()
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "tools", "reference_cases.json"));
            Case[] cases = JsonUtility.FromJson<CaseFile>(File.ReadAllText(path)).cases;
            Assert.AreEqual(468, cases.Length);

            // One case per slot of 32 x 32 cells (2 x 2 Wind Grid columns), so the bilinear Surface Wind sample of the
            // test cell is exact. The moisture values m10, m100, and mLive are uniforms, so each moisture set is one batch.
            const int slot = 32, slotsPerSide = N / slot;
            int checkedCases = 0;
            double worst = 0.0;
            for (int start = 0; start < cases.Length;)
            {
                CaseIn first = cases[start].@in;
                int count = 0;
                while (start + count < cases.Length && count < slotsPerSide * slotsPerSide
                    && cases[start + count].@in.m1 == first.m1 && cases[start + count].@in.m10 == first.m10
                    && cases[start + count].@in.m100 == first.m100 && cases[start + count].@in.mLive == first.mLive)
                    count++;

                int batchStart = start, batchCount = count;
                CaseIn CaseAt(int i, int j)
                {
                    int k = (j / slot) * slotsPerSide + i / slot;
                    return k < batchCount ? cases[batchStart + k].@in : null;
                }

                ScenarioFiles files = MakeScenario("gpu-match",
                    (i, j) =>
                    {
                        CaseIn c = CaseAt(i, j);
                        if (c == null) return 1500f;
                        double up = c.upslopeDeg * Math.PI / 180.0;
                        double dx = (i % slot - 8) * Cell, dz = (j % slot - 8) * Cell;
                        return (float)(1500.0 + c.slope * (Math.Sin(up) * dx + Math.Cos(up) * dz));
                    },
                    (i, j) => CaseAt(i, j) != null ? (byte)CaseAt(i, j).fuel : FuelCodes.Barren);
                Assert.IsTrue(_app.LoadScenarioFiles(files));
                _app.Weather.Enabled = false;
                _app.Weather.SetMoisture((float)first.m1, (float)first.m10, (float)first.m100, (float)first.mLive);

                // Surface Wind: the midflame wind divided by the wind adjustment factor, per Wind Grid column.
                int columns = N / GridSpec.BlockSize;
                var wind = new ushort[columns * columns * 2];
                for (int bz = 0; bz < columns; bz++)
                {
                    for (int bx = 0; bx < columns; bx++)
                    {
                        CaseIn c = CaseAt(bx * GridSpec.BlockSize, bz * GridSpec.BlockSize);
                        Vector2 v = Vector2.zero;
                        if (c != null)
                        {
                            double speed = c.midflameWindMps / FuelTable.Get(c.fuel).WindAdjustmentFactor;
                            double to = c.windToDeg * Math.PI / 180.0;
                            v = new Vector2((float)(Math.Sin(to) * speed), (float)(Math.Cos(to) * speed));
                        }
                        wind[(bz * columns + bx) * 2] = Mathf.FloatToHalf(v.x);
                        wind[(bz * columns + bx) * 2 + 1] = Mathf.FloatToHalf(v.y);
                    }
                }
                var windTexture = new Texture2D(columns, columns, TextureFormat.RGHalf, false, true);
                windTexture.SetPixelData(wind, 0);
                windTexture.Apply(false, false);
                Graphics.CopyTexture(windTexture, _app.Registry.Get(SimTextures.SurfaceWind));
                UnityEngine.Object.Destroy(windTexture);

                _app.Spread.RunBehavior(true);
                Vector4[] a = Readback<Vector4>(_app.Registry.Get(SimTextures.SpreadA), TextureFormat.RGBAFloat);
                Vector4[] b = Readback<Vector4>(_app.Registry.Get(SimTextures.SpreadB), TextureFormat.RGBAFloat);

                for (int k = 0; k < count; k++)
                {
                    CaseIn c = cases[start + k].@in;
                    int ci = (k % slotsPerSide) * slot + 8, cj = (k / slotsPerSide) * slot + 8;
                    Vector4 ga = a[cj * N + ci], gb = b[cj * N + ci];
                    FireBehavior r = ReferenceModel.Spread(c.fuel, c.m1, c.m10, c.m100, c.mLive, c.midflameWindMps, c.windToDeg, c.slope, c.upslopeDeg);
                    string label = $"Reference Case {start + k} (fuel {c.fuel}, wind {c.midflameWindMps}, slope {c.slope})";

                    AssertWithin(r.RHeadMps, ga.x, 0.01, 1e-6, label + ", head rate");
                    bool hasHead = r.RHeadMps > 0.0 && (c.midflameWindMps > 0.0 || c.slope > 0.0);
                    double hx = hasHead ? Math.Sin(r.HeadDeg * Math.PI / 180.0) : 0.0;
                    double hz = hasHead ? Math.Cos(r.HeadDeg * Math.PI / 180.0) : 0.0;
                    AssertWithin(hx, ga.y, 0.01, 0.01, label + ", head direction X");
                    AssertWithin(hz, ga.z, 0.01, 0.01, label + ", head direction Z");
                    AssertWithin(r.Eccentricity, ga.w, 0.01, 1e-3, label + ", eccentricity");
                    AssertWithin(r.ReactionKwM2, gb.x, 0.01, 1e-3, label + ", reaction intensity");
                    AssertWithin(r.ResidenceS, gb.y, 0.01, 1e-3, label + ", residence time");
                    AssertWithin(r.FlameLengthM, gb.z, 0.01, 1e-4, label + ", flame length");
                    AssertWithin(r.R0Mps, gb.w, 0.01, 2e-6, label + ", no-wind rate");
                    if (r.RHeadMps > 0.0) worst = Math.Max(worst, Math.Abs(ga.x - r.RHeadMps) / r.RHeadMps);
                    checkedCases++;
                }
                start += count;
                yield return null;
            }
            Debug.Log($"[FireCA test] GPU match: {checkedCases} cases, largest head rate error {worst * 100.0:F3} percent.");
            Assert.AreEqual(468, checkedCases);
        }

        // ---- test 2: circle ----

        [UnityTest]
        public IEnumerator Test2_Circle_FlatNoWindFireIsACircle()
        {
            Load(MakeScenario("circle", (i, j) => 1200f, (i, j) => 1), Vector2.zero);
            const int c = N / 2;
            const float time = 3600f;
            _app.Spread.IgniteCell(c, c, 0.0);
            _app.Spread.RunUntil(time);
            yield return null;
            float[] arrival = ReadArrival();

            double r0 = Reference(1, 0.0, 0.0, 0.0, 0.0).R0Mps;
            double expected = r0 * time;
            double worst = 0.0;
            for (int k = 0; k < 16; k++)
            {
                double angle = k * Math.PI / 8.0;
                Vector2 dir = new Vector2((float)Math.Sin(angle), (float)Math.Cos(angle));
                double edge = EdgeDistance(arrival, new Vector2(c + 0.5f, c + 0.5f) * Cell, dir, time, r0);
                worst = Math.Max(worst, Math.Abs(edge - expected) / expected);
                AssertWithin(expected, edge, 0.04, 0.0, $"Circle, direction {k * 22.5} degrees, edge distance");
            }
            Debug.Log($"[FireCA test] Circle: expected radius {expected:F1} m, largest error {worst * 100.0:F2} percent.");
        }

        // The distance from the origin to the fire edge along a direction: the farthest ignited cell on the ray,
        // plus the distance that the front covers after the arrival at the cell.
        static double EdgeDistance(float[] arrival, Vector2 origin, Vector2 dir, float time, double rate)
        {
            double edge = 0.0;
            for (float d = 0f; d < N * Cell; d += 0.5f)
            {
                Vector2 p = origin + dir * d;
                int i = Mathf.FloorToInt(p.x / Cell), j = Mathf.FloorToInt(p.y / Cell);
                if (i < 0 || j < 0 || i >= N || j >= N) break;
                float a = arrival[j * N + i];
                if (!Ignited(a, time)) break;
                Vector2 center = new Vector2(i + 0.5f, j + 0.5f) * Cell;
                edge = Math.Max(edge, Vector2.Dot(center - origin, dir) + (time - a) * rate);
            }
            return edge;
        }

        // ---- test 3: ellipse, and test 6: step independence ----

        const int EllipseI = 100, EllipseJ = N / 2;
        const float EllipseTime = 600f;

        float[] RunEllipse(float stepScale)
        {
            float waf = WindAdjustmentFactor(1);
            Load(MakeScenario("ellipse", (i, j) => 1200f, (i, j) => 1), new Vector2(2f / waf, 0f));
            _app.Spread.StepScale = stepScale;
            _app.Spread.IgniteCell(EllipseI, EllipseJ, 0.0);
            _app.Spread.RunUntil(EllipseTime);
            return ReadArrival();
        }

        static int BurnedCells(float[] arrival, float time)
        {
            int count = 0;
            for (int k = 0; k < arrival.Length; k++) if (Ignited(arrival[k], time)) count++;
            return count;
        }

        [UnityTest]
        public IEnumerator Test3_Ellipse_UniformWindFireMatchesTheAnalyticEllipse()
        {
            float[] arrival = RunEllipse(1f);
            yield return null;

            FireBehavior r = Reference(1, 2.0, 90.0, 0.0, 0.0);
            double head = r.RHeadMps * EllipseTime;
            double back = head * (1.0 - r.Eccentricity) / (1.0 + r.Eccentricity);
            double width = (head + back) * Math.Sqrt(1.0 - r.Eccentricity * r.Eccentricity);

            Vector2 origin = new Vector2(EllipseI + 0.5f, EllipseJ + 0.5f) * Cell;
            double headMeasured = EdgeDistance(arrival, origin, Vector2.right, EllipseTime, r.RHeadMps);
            double backMeasured = EdgeDistance(arrival, origin, Vector2.left, EllipseTime, r.RateAtAngle(180.0));
            // The largest width: the longest run of ignited cells across the wind.
            int widest = 0;
            for (int i = 0; i < N; i++)
            {
                int run = 0;
                for (int j = 0; j < N; j++) if (Ignited(arrival[j * N + i], EllipseTime)) run++;
                widest = Mathf.Max(widest, run);
            }
            double widthMeasured = widest * Cell;

            Debug.Log($"[FireCA test] Ellipse: head {headMeasured:F1} m (analytic {head:F1}), back {backMeasured:F1} m (analytic {back:F1}), " +
                $"width {widthMeasured:F1} m (analytic {width:F1}).");
            AssertWithin(head, headMeasured, 0.06, 4.0, "Ellipse, head distance");
            AssertWithin(back, backMeasured, 0.06, 4.0, "Ellipse, back distance");
            AssertWithin(width, widthMeasured, 0.06, 4.0, "Ellipse, largest width");
        }

        [UnityTest]
        public IEnumerator Test6_StepIndependence_QuarterStepGivesTheSameBurnedArea()
        {
            int full = BurnedCells(RunEllipse(1f), EllipseTime);
            long fullSteps = _app.Spread.StepCount;
            yield return null;
            int quarter = BurnedCells(RunEllipse(0.25f), EllipseTime);
            long quarterSteps = _app.Spread.StepCount;
            yield return null;

            Debug.Log($"[FireCA test] Step independence: {full} cells in {fullSteps} Fire steps, {quarter} cells in {quarterSteps} Fire steps.");
            Assert.Greater(quarterSteps, fullSteps * 3);
            AssertWithin(full, quarter, 0.02, 0.0, "Step independence, burned area");
        }

        // ---- test 4: slope ----

        [UnityTest]
        public IEnumerator Test4_Slope_HeadIsUpslope_AndTheRateMatchesTheReferenceModel()
        {
            const float slope = 0.30f;
            // The plane rises toward the east.
            Load(MakeScenario("slope", (i, j) => 1200f + slope * (i + 0.5f) * Cell, (i, j) => 5), Vector2.zero);
            const int i0 = 100, j0 = N / 2, reach = 20;
            const float time = 3600f;
            _app.Spread.IgniteCell(i0, j0, 0.0);
            _app.Spread.RunUntil(time);
            yield return null;
            float[] arrival = ReadArrival();
            Vector4[] spreadA = Readback<Vector4>(_app.Registry.Get(SimTextures.SpreadA), TextureFormat.RGBAFloat);

            FireBehavior r = Reference(5, 0.0, 0.0, slope, 90.0);

            // The head direction of the behavior pass is upslope (east).
            Vector4 a = spreadA[j0 * N + i0 + reach];
            Assert.AreEqual(1f, a.y, 0.01f, "The head direction must be upslope.");
            Assert.AreEqual(0f, a.z, 0.01f, "The head direction must be upslope.");

            // The fire reaches a cell upslope before the cells at the same distance in the other directions.
            float up = arrival[j0 * N + i0 + reach];
            Assert.IsTrue(Ignited(up, time), "The fire did not reach the upslope cell.");
            Assert.Less(up, arrival[j0 * N + i0 - reach], "The fire must spread fastest upslope.");
            Assert.Less(up, arrival[(j0 + reach) * N + i0]);
            Assert.Less(up, arrival[(j0 - reach) * N + i0]);

            // The head rate over the surface distance.
            double surface = reach * Cell * Math.Sqrt(1.0 + slope * slope);
            double rate = surface / up;
            Debug.Log($"[FireCA test] Slope: head rate {rate:F5} m/s, Reference Model {r.RHeadMps:F5} m/s.");
            AssertWithin(r.RHeadMps, rate, 0.04, 0.0, "Slope, head rate");
        }

        // ---- test 5: barrier ----

        [UnityTest]
        public IEnumerator Test5_Barrier_FireDoesNotCrossAStripTwoCellsWide()
        {
            const int strip = 300;
            float waf = WindAdjustmentFactor(1);
            // The wind blows toward the east-northeast, so the fire meets the strip at an angle.
            Vector2 wind = new Vector2(2f, 0.8f).normalized * (2f / waf);
            Load(MakeScenario("barrier", (i, j) => 1200f, (i, j) => i == strip || i == strip + 1 ? FuelCodes.Barren : (byte)1), wind);
            const float time = 1800f;
            _app.Spread.IgniteCell(200, N / 2, 0.0);
            _app.Spread.RunUntil(time);
            yield return null;
            float[] arrival = ReadArrival();

            int reached = 0, crossed = 0;
            for (int j = 0; j < N; j++)
            {
                if (Ignited(arrival[j * N + strip - 1], time)) reached++;
                for (int i = strip; i < N; i++) if (Ignited(arrival[j * N + i], time)) crossed++;
            }
            Debug.Log($"[FireCA test] Barrier: the fire reached the strip along {reached * Cell:F0} m. Cells ignited past the strip: {crossed}.");
            Assert.Greater(reached, 50, "The fire must reach the strip.");
            Assert.AreEqual(0, crossed, "The fire must not cross the strip.");
        }

        // ---- criteria 3 to 5: a fire on synthetic-canyon-01 ----

        [UnityTest, Timeout(1200000)]
        public IEnumerator Canyon_SixHourRun_NoNaN_StopsAtCreekAndBarren_ChuteIsFaster()
        {
            Assert.IsTrue(_app.LoadScenario("synthetic-canyon-01"), _app.LastError);
            ScenarioFiles s = _app.Scenario;
            int n = s.Grid.FireCells;
            SpreadSolver spread = _app.Spread;
            spread.SynchronousReadback = true;
            var ignition = s.Data.ignitions[0];
            Vector2 start = new Vector2(ignition.xM, ignition.zM);
            spread.Ignite(start, ignition.radiusM, 0.0);
            Assert.Greater(spread.IgnitedCells, 0, "The default ignition ignited no cell.");

            const double hours = 6.0;
            float wallStart = Time.realtimeSinceStartup;
            for (double t = 0.0; t < hours * 3600.0; t += WeatherModel.UpdateIntervalS)
            {
                if (_app.Weather.Update(t)) spread.RunBehavior(true);
                spread.RunUntil(t + WeatherModel.UpdateIntervalS);
                if (((int)(t / WeatherModel.UpdateIntervalS)) % 30 == 29) yield return null;
            }
            float wall = Time.realtimeSinceStartup - wallStart;
            Debug.Log($"[FireCA test] Canyon run: 6 simulated hours in {spread.StepCount} Fire steps and {wall:F0} s. " +
                $"Burned area {spread.BurnedAreaHa:F0} ha. Active blocks {spread.ActiveBlocks}.");

            // Criterion 3: no NaN value in any texture.
            foreach (var spec in TextureRegistry.Specs)
            {
                if (spec.Grid == TextureRegistry.GridKind.Wind) continue;   // the Wind Solver writes these from M3
                if (spec.Name == SimTextures.Insolation || spec.Name == SimTextures.SurfaceHeating
                    || spec.Name == SimTextures.HeatFluxCoarse) continue;   // written from M2 and M4
                Assert.AreEqual(0, spread.CountNonFinite(_app.Registry.Get(spec.Name)), $"'{spec.Name}' holds a NaN value or an infinite value.");
                if (spec.Copies == 2)
                    Assert.AreEqual(0, spread.CountNonFinite(_app.Registry.GetWrite(spec.Name)), $"'{spec.Name}' (write copy) holds a NaN value.");
            }

            float end = (float)(hours * 3600.0);
            float[] arrival = Readback<float>(_app.Registry.Get(SimTextures.ArrivalTime), TextureFormat.RFloat);
            byte[] fuel = s.Fuel;

            // Criterion 4: the fire stops at the creek channel and at barren cells.
            int ignitedNotBurnable = 0, westOfCreek = 0, rowsAtCreek = 0, barrenAtFire = 0, rowsWithCreek = 0;
            for (int j = 0; j < n; j++)
            {
                int waterMin = -1, waterMax = -1;
                for (int i = 0; i < n; i++)
                {
                    int code = fuel[j * n + i];
                    bool burning = Ignited(arrival[j * n + i], end);
                    if (code == FuelCodes.Water) { if (waterMin < 0) waterMin = i; waterMax = i; }
                    if (burning && !FuelCodes.IsBurnable(code)) ignitedNotBurnable++;
                    if (code == FuelCodes.Barren && i > 0 && i < n - 1 && j > 0 && j < n - 1
                        && (Ignited(arrival[j * n + i - 1], end) || Ignited(arrival[j * n + i + 1], end)
                            || Ignited(arrival[(j - 1) * n + i], end) || Ignited(arrival[(j + 1) * n + i], end)))
                        barrenAtFire++;
                }
                if (waterMin < 0) continue;
                rowsWithCreek++;
                // The default ignition is east of the creek channel. No cell west of the creek channel can ignite.
                for (int i = 0; i < waterMin; i++) if (Ignited(arrival[j * n + i], end)) westOfCreek++;
                bool atCreek = false;
                for (int i = waterMax + 1; i <= Mathf.Min(n - 1, waterMax + 4); i++) atCreek |= Ignited(arrival[j * n + i], end);
                if (atCreek) rowsAtCreek++;
            }
            Debug.Log($"[FireCA test] Creek: the fire reached the creek channel along {rowsAtCreek * Cell:F0} m. Cells ignited west of the creek channel: {westOfCreek}. " +
                $"Barren cells next to the fire: {barrenAtFire}. Ignited cells that are not burnable: {ignitedNotBurnable}.");
            Assert.AreEqual(n, rowsWithCreek, "The creek channel must cross every row.");
            Assert.AreEqual(0, ignitedNotBurnable);
            Assert.AreEqual(0, westOfCreek, "The fire crossed the creek channel.");
            Assert.Greater(rowsAtCreek, 100, "The fire must reach the creek channel.");
            Assert.Greater(barrenAtFire, 0, "The fire must reach barren cells.");

            // Criterion 5: the fire spreads faster up the chute than across the canyon floor.
            Assert.IsTrue(s.Data.TryGetLandmark(ScenarioData.LandmarkChuteBase, out Vector2 chuteBase));
            Assert.IsTrue(s.Data.TryGetLandmark(ScenarioData.LandmarkChuteTop, out Vector2 chuteTop));
            Vector2 chuteDir = (chuteTop - chuteBase).normalized;
            Vector2 low = chuteBase + chuteDir * 100f, high = chuteBase + chuteDir * 700f;
            float tLow = ArrivalAt(arrival, n, low), tHigh = ArrivalAt(arrival, n, high);
            Assert.IsTrue(Ignited(tLow, end) && Ignited(tHigh, end), "The fire did not burn the chute.");
            double chuteSurface = Math.Sqrt(600.0 * 600.0 + Math.Pow(_app.SampleElevation(high.x, high.y) - _app.SampleElevation(low.x, low.y), 2.0));
            double chuteRate = chuteSurface / (tHigh - tLow);

            // The floor rate: the fastest mean rate from the ignition to a grass cell on the canyon floor.
            float floorElevation = _app.SampleElevation(start.x, start.y);
            double floorRate = 0.0;
            int reachCells = Mathf.RoundToInt(600f / Cell);
            int si = (int)(start.x / Cell), sj = (int)(start.y / Cell);
            for (int j = sj - reachCells; j <= sj + reachCells; j++)
            {
                for (int i = si - reachCells; i <= si + reachCells; i++)
                {
                    if (i < 0 || j < 0 || i >= n || j >= n) continue;
                    float a = arrival[j * n + i];
                    if (!Ignited(a, end) || a <= 0f || fuel[j * n + i] != 1) continue;
                    if (Mathf.Abs(s.Elevation[j * n + i] - floorElevation) > 15f) continue;
                    double distance = Vector2.Distance(new Vector2(i + 0.5f, j + 0.5f) * Cell, start);
                    if (distance < 100.0 || distance > 600.0) continue;
                    floorRate = Math.Max(floorRate, distance / a);
                }
            }
            Debug.Log($"[FireCA test] Chute: {chuteRate:F3} m/s up the chute (reached at {tLow / 60f:F0} min, top at {tHigh / 60f:F0} min). " +
                $"Canyon floor: {floorRate:F3} m/s.");
            Assert.Greater(floorRate, 0.0, "The fire did not spread on the canyon floor.");
            Assert.Greater(chuteRate, floorRate, "The fire must spread faster up the chute than across the canyon floor.");
        }

        static float ArrivalAt(float[] arrival, int n, Vector2 p)
        {
            // The earliest arrival within 6 m of the point.
            float best = float.PositiveInfinity;
            int ci = (int)(p.x / Cell), cj = (int)(p.y / Cell);
            for (int j = cj - 3; j <= cj + 3; j++)
                for (int i = ci - 3; i <= ci + 3; i++)
                    if (i >= 0 && j >= 0 && i < n && j < n) best = Mathf.Min(best, arrival[j * n + i]);
            return best;
        }

        // ---- Sim Clock, Step Scheduler, ignition ----

        [UnityTest]
        public IEnumerator Scheduler_RunsTheScenarioIgnition_AndTheIgniteToolAndClearFireWork()
        {
            Assert.IsTrue(_app.LoadScenario("test-canyon-512"), _app.LastError);
            _app.SchedulerEnabled = true;
            yield return null;
            yield return null;

            // The Scenario ignition at hour 0 runs while the Sim Clock is paused.
            Assert.IsFalse(_app.Clock.Playing, "The Sim Clock must start paused.");
            Assert.AreEqual(0.0, _app.Clock.Seconds);
            long scenarioIgnition = _app.Spread.IgnitedCells;
            Assert.Greater(scenarioIgnition, 0, "The Scenario ignition did not run.");

            _app.Clock.SetTimeScale(1000f);
            _app.Clock.Playing = true;
            for (int frame = 0; frame < 120; frame++) yield return null;
            _app.Clock.Playing = false;
            yield return null;
            Assert.Greater(_app.Clock.Seconds, 10.0, "The Sim Clock did not advance.");
            Assert.GreaterOrEqual(_app.Spread.FireTime, _app.Clock.Seconds - 1e-3, "The fire time must reach the Sim Clock.");
            Assert.Greater(_app.Spread.StepCount, 0);
            AsyncGPUReadback.WaitAllRequests();
            yield return null;
            Assert.Greater(_app.Spread.IgnitedCells, scenarioIgnition, "The fire did not grow.");
            StringAssert.Contains("solar time", _app.Clock.Readout);

            // Clear Fire sets every cell to not ignited.
            _app.ClearFire();
            Assert.AreEqual(0, _app.Spread.IgnitedCells);
            float[] arrival = Readback<float>(_app.Registry.Get(SimTextures.ArrivalTime), TextureFormat.RFloat);
            foreach (float a in arrival) if (a < 1.0e29f) Assert.Fail("Clear Fire left an ignited cell.");

            // The ignite tool ignites the burnable cells within 6 m of the hit point, at the Sim Clock value.
            var data = _app.Scenario.Data;
            _app.IgniteAt(data.ignitions[0].xM, data.ignitions[0].zM);
            Assert.That(_app.Spread.IgnitedCells, Is.InRange(15, 40), "A circle of 6 m radius holds about 28 cells.");
            arrival = Readback<float>(_app.Registry.Get(SimTextures.ArrivalTime), TextureFormat.RFloat);
            int n = _app.Scenario.Grid.FireCells;
            float at = arrival[(int)(data.ignitions[0].zM / Cell) * n + (int)(data.ignitions[0].xM / Cell)];
            Assert.AreEqual((float)_app.Clock.Seconds, at, 1e-3f);

            // A terrain ray from above hits the terrain at the same point.
            Vector3 above = new Vector3(data.ignitions[0].xM, _app.Scenario.MaxElevation + 500f, data.ignitions[0].zM);
            Assert.IsTrue(_app.RaycastTerrain(new Ray(above, Vector3.down), out Vector3 hit));
            Assert.AreEqual(_app.SampleElevation(above.x, above.z), hit.y, 0.5f);
        }

        [Test]
        public void BackgroundWind_InterpolatesSpeedAndDirection()
        {
            var wind = new[]
            {
                new ScenarioData.WindEntry { hour = 0f, speedMps = 4f, fromDeg = 350f },
                new ScenarioData.WindEntry { hour = 2f, speedMps = 8f, fromDeg = 10f },
            };
            // Halfway: 6 m/s from 0 degrees (north), along the shorter arc. The wind goes toward the south.
            Vector2 mid = WeatherModel.InterpolateWind(wind, 1.0);
            Assert.AreEqual(0f, mid.x, 1e-4f);
            Assert.AreEqual(-6f, mid.y, 1e-4f);
            // A wind from 225 degrees (southwest) goes toward the northeast.
            Vector2 sw = WeatherModel.WindVector(4f, 225f);
            Assert.Greater(sw.x, 0f);
            Assert.Greater(sw.y, 0f);
            Assert.AreEqual(4f, sw.magnitude, 1e-4f);
            Assert.AreEqual(wind[1].speedMps, WeatherModel.InterpolateWind(wind, 5.0).magnitude, 1e-4f);
        }
    }
}
