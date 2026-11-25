using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace FireCA.Tests
{
    /// <summary>M0 acceptance criterion 2: each validation check rejects a Scenario that breaks the check.</summary>
    public sealed class ScenarioValidationTests
    {
        const int N = 512;
        string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "fireca-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        // Writes a valid flat Scenario. Each callback can break one part.
        string WriteScenario(Action<ScenarioData> editData = null, Func<float[], float[]> editElevation = null, int fuelSize = N)
        {
            string folder = Path.Combine(_root, "scenario");
            Directory.CreateDirectory(folder);
            var data = ScenarioData.CreateDefault("scenario", N);
            data.ignitions[0].xM = 500f;
            data.ignitions[0].zM = 500f;
            editData?.Invoke(data);

            var elevation = new float[N * N];
            for (int i = 0; i < elevation.Length; i++) elevation[i] = 1200f;
            if (editElevation != null) elevation = editElevation(elevation);
            var bytes = new byte[elevation.Length * 4];
            Buffer.BlockCopy(elevation, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(folder, "elevation.r32"), bytes);

            var fuel = new byte[fuelSize * fuelSize];
            for (int i = 0; i < fuel.Length; i++) fuel[i] = 1;
            File.WriteAllBytes(Path.Combine(folder, "fuel.png"), PngCodec.EncodeGray(fuel, fuelSize, fuelSize, true));

            File.WriteAllText(Path.Combine(folder, "scenario.json"), JsonUtility.ToJson(data, true));
            return folder;
        }

        static void AssertRejected(string folder, ScenarioCheck expected)
        {
            var e = Assert.Throws<ScenarioException>(() => ScenarioLoader.Load(folder));
            Assert.AreEqual(expected, e.Check, e.Message);
            StringAssert.Contains(expected.ToString(), e.DisplayText, "The display text must name the failed check.");
        }

        [Test]
        public void ValidScenario_Loads()
        {
            ScenarioFiles files = ScenarioLoader.Load(WriteScenario());
            Assert.AreEqual(N, files.Grid.FireCells);
            Assert.AreEqual(N * N, files.Elevation.Length);
            Assert.AreEqual(N * N, files.Fuel.Length);
            Assert.AreEqual(1200f, files.MinElevation);
            Assert.AreEqual(1184f, files.Grid.WindBottomY, "The Wind Grid bottom is the lowest elevation rounded down to 32 m.");
            Assert.AreEqual(32, files.Grid.WindCellsX);
        }

        [Test]
        public void Check01_GridSize()
        {
            AssertRejected(WriteScenario(d => d.gridSize = 500), ScenarioCheck.GridSize);
        }

        [Test]
        public void Check02_CellSize()
        {
            AssertRejected(WriteScenario(d => d.cellSizeM = 1.0f), ScenarioCheck.CellSize);
        }

        [Test]
        public void Check03_ElevationFileSize()
        {
            AssertRejected(WriteScenario(null, e => new float[e.Length - 1]), ScenarioCheck.ElevationFileSize);
        }

        [Test]
        public void Check04_ElevationValues_NotFinite()
        {
            AssertRejected(WriteScenario(null, e => { e[1000] = float.NaN; return e; }), ScenarioCheck.ElevationValues);
            AssertRejected(WriteScenario(null, e => { e[1000] = float.PositiveInfinity; return e; }), ScenarioCheck.ElevationValues);
        }

        [Test]
        public void Check04_ElevationValues_OutOfBounds()
        {
            AssertRejected(WriteScenario(null, e => { e[7] = 9001f; return e; }), ScenarioCheck.ElevationValues);
            AssertRejected(WriteScenario(null, e => { e[7] = -501f; return e; }), ScenarioCheck.ElevationValues);
        }

        [Test]
        public void Check05_ElevationRange()
        {
            AssertRejected(WriteScenario(null, e => { e[0] = 0f; e[1] = 3000f; return e; }), ScenarioCheck.ElevationRange);
        }

        [Test]
        public void Check06_FuelImageSize()
        {
            AssertRejected(WriteScenario(null, null, 256), ScenarioCheck.FuelImageSize);
        }

        [Test]
        public void Check07_WindEntries_Empty()
        {
            AssertRejected(WriteScenario(d => d.weather.wind = new ScenarioData.WindEntry[0]), ScenarioCheck.WindEntries);
        }

        [Test]
        public void Check07_WindEntries_HoursDoNotIncrease()
        {
            AssertRejected(WriteScenario(d => d.weather.wind[1].hour = 0f), ScenarioCheck.WindEntries);
        }

        [Test]
        public void Check08_TemperatureRange()
        {
            AssertRejected(WriteScenario(d => d.weather.tempMinC = d.weather.tempMaxC), ScenarioCheck.TemperatureRange);
        }

        [Test]
        public void Check09_MoistureValues()
        {
            AssertRejected(WriteScenario(d => d.moisture.m1Pct = 0.5f), ScenarioCheck.MoistureValues);
            AssertRejected(WriteScenario(d => d.moisture.m10Pct = 301f), ScenarioCheck.MoistureValues);
            AssertRejected(WriteScenario(d => d.moisture.m100Pct = 0f), ScenarioCheck.MoistureValues);
            AssertRejected(WriteScenario(d => d.moisture.livePct = 400f), ScenarioCheck.MoistureValues);
        }

        [Test]
        public void Check10_IgnitionInsideGrid()
        {
            AssertRejected(WriteScenario(d => d.ignitions[0].xM = -10f), ScenarioCheck.IgnitionInsideGrid);
            // The center is inside the Fire Grid, and the circle crosses the edge.
            AssertRejected(WriteScenario(d => { d.ignitions[0].zM = 1021f; d.ignitions[0].radiusM = 6f; }), ScenarioCheck.IgnitionInsideGrid);
        }

        [Test]
        public void MissingFile_IsRejected()
        {
            string folder = WriteScenario();
            File.Delete(Path.Combine(folder, "fuel.png"));
            AssertRejected(folder, ScenarioCheck.ScenarioFiles);
        }

        [Test]
        public void FuelImage_RowsFlipOnLoad_AndBytesAreRaw()
        {
            string folder = WriteScenario();
            // The top row of the image holds code 99. Every other row holds a ramp of raw byte values.
            var image = new byte[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    image[y * N + x] = y == 0 ? (byte)99 : (byte)(x % 256);
            File.WriteAllBytes(Path.Combine(folder, "fuel.png"), PngCodec.EncodeGray(image, N, N, false));

            ScenarioFiles files = ScenarioLoader.Load(folder);
            Assert.AreEqual(99, files.Fuel[(N - 1) * N + 5], "The top row of the image must be the north edge.");
            for (int x = 0; x < 256; x++)
                Assert.AreEqual(x, files.Fuel[x], "The loader must return the raw byte values.");
        }

        [Test]
        public void PngCodec_RoundTrip()
        {
            var random = new System.Random(7);
            var gray = new byte[64 * 48];
            random.NextBytes(gray);
            byte[] png = PngCodec.EncodeGray(gray, 64, 48, false);
            byte[] decoded = PngCodec.DecodeRed(png, false, out int w, out int h);
            Assert.AreEqual(64, w);
            Assert.AreEqual(48, h);
            CollectionAssert.AreEqual(gray, decoded);
        }
    }
}
