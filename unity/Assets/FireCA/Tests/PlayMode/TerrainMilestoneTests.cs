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
    /// <summary>PlayMode tests of milestone M0: Scenario load, validation on screen, derived layers, camera, Overlays.</summary>
    public sealed class TerrainMilestoneTests
    {
        const string SceneName = "FireCA";
        const string DemoScenario = "synthetic-canyon-01";
        const string StartupScenario = "south-canyon";
        const string TestScenario = "test-canyon-512";

        FireCAApp _app;
        string _tempRoot;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;
            yield return null;
            _app = FireCAApp.Instance;
            Assert.IsNotNull(_app, "The FireCA scene has no FireCAApp component.");
            _tempRoot = Path.Combine(Path.GetTempPath(), "fireca-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        [TearDown]
        public void DeleteTempFiles()
        {
            if (_tempRoot != null && Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
        }

        // ---- criterion 1 ----

        [UnityTest]
        public IEnumerator SyntheticCanyon_LoadsInUnder10Seconds()
        {
            Assert.IsNull(_app.LastError, _app.LastError);
            Assert.AreEqual(StartupScenario, _app.Scenario.Id, "The scene must load south-canyon at startup.");
            Assert.IsTrue(_app.LoadScenario(DemoScenario), _app.LastError);
            float startupSeconds = _app.LastLoadSeconds;

            float before = Time.realtimeSinceStartup;
            Assert.IsTrue(_app.LoadScenario(DemoScenario));
            float wallSeconds = Time.realtimeSinceStartup - before;
            yield return null;

            Debug.Log($"[FireCA test] synthetic-canyon-01 load: {startupSeconds:F2} s at startup, {_app.LastLoadSeconds:F2} s on reload ({wallSeconds:F2} s wall clock).");
            Assert.Less(startupSeconds, 10f);
            Assert.Less(wallSeconds, 10f);
            Assert.AreEqual(4096, _app.Scenario.Grid.FireCells);
            Assert.AreEqual(64, _app.Terrain.TileCount, "A 4096 Fire Grid has 8 x 8 tiles of 128 x 128 quads.");
        }

        // ---- criterion 2 ----

        string WriteScenario(string name, Action<ScenarioData> editData = null, Func<float[], float[]> editElevation = null, int fuelSize = 512)
        {
            const int n = 512;
            string folder = Path.Combine(_tempRoot, name);
            Directory.CreateDirectory(folder);
            var data = ScenarioData.CreateDefault(name, n);
            data.ignitions[0].xM = 500f;
            data.ignitions[0].zM = 500f;
            editData?.Invoke(data);

            var elevation = new float[n * n];
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

        [UnityTest]
        public IEnumerator EachValidationCheck_ShowsItsNameOnScreen()
        {
            var panel = UnityEngine.Object.FindFirstObjectByType<ControlPanel>();
            Assert.IsNotNull(panel);
            yield return null;

            var cases = new (ScenarioCheck check, string folder)[]
            {
                (ScenarioCheck.GridSize, WriteScenario("c01", d => d.gridSize = 1000)),
                (ScenarioCheck.CellSize, WriteScenario("c02", d => d.cellSizeM = 4f)),
                (ScenarioCheck.ElevationFileSize, WriteScenario("c03", null, e => new float[e.Length / 2])),
                (ScenarioCheck.ElevationValues, WriteScenario("c04", null, e => { e[9] = float.NaN; return e; })),
                (ScenarioCheck.ElevationRange, WriteScenario("c05", null, e => { e[0] = 100f; e[1] = 3200f; return e; })),
                (ScenarioCheck.FuelImageSize, WriteScenario("c06", null, null, 128)),
                (ScenarioCheck.WindEntries, WriteScenario("c07", d => d.weather.wind = new ScenarioData.WindEntry[0])),
                (ScenarioCheck.TemperatureRange, WriteScenario("c08", d => d.weather.tempMaxC = d.weather.tempMinC - 1f)),
                (ScenarioCheck.MoistureValues, WriteScenario("c09", d => d.moisture.livePct = 0f)),
                (ScenarioCheck.IgnitionInsideGrid, WriteScenario("c10", d => d.ignitions[0].xM = 5000f)),
            };

            foreach (var (check, folder) in cases)
            {
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Scenario rejected by check " + check));
                Assert.IsFalse(_app.LoadScenarioFolder(folder), $"The loader accepted a Scenario that breaks {check}.");
                yield return null;
                Assert.AreEqual(check, _app.LastFailedCheck);
                StringAssert.Contains(check.ToString(), panel.StatusText, "The Control Panel must show the name of the failed check.");
                Assert.AreEqual(StartupScenario, _app.Scenario.Id, "A rejected load must leave the loaded Scenario in place.");
            }

            // A valid Scenario clears the message.
            Assert.IsTrue(_app.LoadScenarioFolder(WriteScenario("valid")));
            yield return null;
            StringAssert.Contains("Loaded in", panel.StatusText);
        }

        // ---- derived layers ----

        static T[] Readback<T>(Texture texture, TextureFormat format) where T : struct
        {
            var request = AsyncGPUReadback.Request(texture, 0, format);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError, $"The readback of '{texture.name}' failed.");
            return request.GetData<T>().ToArray();
        }

        [UnityTest]
        public IEnumerator DerivedLayers_MatchTheCpuReference()
        {
            Assert.IsTrue(_app.LoadScenario(TestScenario), _app.LastError);
            yield return null;
            ScenarioFiles s = _app.Scenario;
            GridSpec grid = s.Grid;
            int n = grid.FireCells;
            float[] e = s.Elevation;
            const float cell = GridSpec.FireCellSizeM;

            // Slope: central differences, one-sided on the grid edge.
            Vector2[] slope = Readback<Vector2>(_app.Registry.Get(SimTextures.Slope), TextureFormat.RGFloat);
            Vector2[] normal = Readback<Vector2>(_app.Registry.Get(SimTextures.Normal), TextureFormat.RGFloat);
            Assert.AreEqual(n * n, slope.Length);
            float worstSlope = 0f, worstNormal = 0f;
            for (int j = 0; j < n; j++)
            {
                int j0 = Mathf.Max(j - 1, 0), j1 = Mathf.Min(j + 1, n - 1);
                for (int i = 0; i < n; i++)
                {
                    int i0 = Mathf.Max(i - 1, 0), i1 = Mathf.Min(i + 1, n - 1);
                    float dhdx = (e[j * n + i1] - e[j * n + i0]) / ((i1 - i0) * cell);
                    float dhdz = (e[j1 * n + i] - e[j0 * n + i]) / ((j1 - j0) * cell);
                    Vector2 g = slope[j * n + i];
                    worstSlope = Mathf.Max(worstSlope, Mathf.Abs(g.x - dhdx), Mathf.Abs(g.y - dhdz));
                    Vector3 nrm = new Vector3(-dhdx, 1f, -dhdz).normalized;
                    Vector2 m = normal[j * n + i];
                    worstNormal = Mathf.Max(worstNormal, Mathf.Abs(m.x - nrm.x), Mathf.Abs(m.y - nrm.z));
                }
            }
            // Slope and Normal are 16-bit float textures.
            Assert.Less(worstSlope, 0.004f, "Slope does not match the CPU reference.");
            Assert.Less(worstNormal, 0.004f, "Normal does not match the CPU reference.");

            // Ground Height: the mean of each 16 x 16 block.
            int wx = grid.WindCellsX, wz = grid.WindCellsZ;
            float[] ground = Readback<float>(_app.Registry.Get(SimTextures.GroundHeight), TextureFormat.RFloat);
            Assert.AreEqual(wx * wz, ground.Length);
            var groundCpu = new float[wx * wz];
            for (int bz = 0; bz < wz; bz++)
            {
                for (int bx = 0; bx < wx; bx++)
                {
                    double sum = 0;
                    for (int j = 0; j < GridSpec.BlockSize; j++)
                        for (int i = 0; i < GridSpec.BlockSize; i++)
                            sum += e[(bz * GridSpec.BlockSize + j) * n + bx * GridSpec.BlockSize + i];
                    groundCpu[bz * wx + bx] = (float)(sum / (GridSpec.BlockSize * GridSpec.BlockSize));
                    Assert.AreEqual(groundCpu[bz * wx + bx], ground[bz * wx + bx], 0.01f, "Ground Height does not match the CPU reference.");
                }
            }

            // Solid Mask: 1 when the center of the Wind Grid cell is below Ground Height, and 1 for a pit cell.
            Assert.AreEqual(Mathf.Floor(s.MinElevation / 32f) * 32f, grid.WindBottomY);
            var mask = (RenderTexture)_app.Registry.Get(SimTextures.SolidMask);
            Assert.AreEqual(TextureDimension.Tex3D, mask.dimension);
            Assert.AreEqual(GridSpec.WindCellsY, mask.height);
            Assert.AreEqual(wz, mask.volumeDepth);
            var maskRequest = AsyncGPUReadback.Request(mask, 0, 0, wx, 0, GridSpec.WindCellsY, 0, wz, TextureFormat.R8);
            maskRequest.WaitForCompletion();
            Assert.IsFalse(maskRequest.hasError);
            int solid = 0, mismatches = 0;
            for (int z = 0; z < wz; z++)
            {
                NativeArray<byte> layer = maskRequest.GetData<byte>(z);
                for (int y = 0; y < GridSpec.WindCellsY; y++)
                {
                    float centerY = grid.WindBottomY + (y + 0.5f) * GridSpec.WindCellSizeM;
                    for (int x = 0; x < wx; x++)
                    {
                        float g = groundCpu[z * wx + x];
                        if (Mathf.Abs(centerY - g) < 0.02f) continue;   // too close to call in 32-bit float
                        // A pit cell is solid too: an air cell with a solid cell on all four sides (decision D27).
                        bool pit = x > 0 && x < wx - 1 && z > 0 && z < wz - 1
                            && centerY < groundCpu[z * wx + x - 1] && centerY < groundCpu[z * wx + x + 1]
                            && centerY < groundCpu[(z - 1) * wx + x] && centerY < groundCpu[(z + 1) * wx + x];
                        bool expected = centerY < g || pit;
                        bool actual = layer[y * wx + x] > 127;
                        if (expected != actual) mismatches++;
                        if (actual) solid++;
                    }
                }
            }
            Assert.AreEqual(0, mismatches, "Solid Mask does not match the CPU reference.");
            Assert.Greater(solid, 0, "The Solid Mask has no solid cell.");
        }

        [UnityTest]
        public IEnumerator TextureRegistry_CreatesEveryTexture()
        {
            Assert.IsTrue(_app.LoadScenario(TestScenario), _app.LastError);
            yield return null;
            TextureRegistry registry = _app.Registry;
            GridSpec grid = registry.Grid;
            foreach (var spec in TextureRegistry.Specs)
            {
                Texture t = registry.Get(spec.Name);
                Assert.IsNotNull(t, spec.Name);
                Assert.AreEqual(spec.Format, t.graphicsFormat, spec.Name);
                int width = spec.Grid == TextureRegistry.GridKind.Fire ? grid.FireCells : grid.WindCellsX;
                Assert.AreEqual(width, t.width, spec.Name);
                if (spec.RandomWrite) Assert.IsTrue(((RenderTexture)t).enableRandomWrite, spec.Name);
                if (spec.Copies == 2) Assert.AreNotSame(t, registry.GetWrite(spec.Name), spec.Name);
            }
            Assert.AreEqual(FilterMode.Point, registry.Get(SimTextures.Fuel).filterMode);
            Assert.AreEqual(FilterMode.Point, registry.Get(SimTextures.ArrivalTime).filterMode);

            Texture read = registry.Get(SimTextures.ArrivalTime), write = registry.GetWrite(SimTextures.ArrivalTime);
            registry.Swap(SimTextures.ArrivalTime);
            Assert.AreSame(write, registry.Get(SimTextures.ArrivalTime), "Swap must exchange the read copy and the write copy.");
            Assert.AreSame(read, registry.GetWrite(SimTextures.ArrivalTime));
            Assert.Greater(registry.TotalBytes, 0);

            // The full-size textures use about 1.4 GB: 1.2 GB, and 0.2 GB for the three textures of decision D31.
            Assert.IsTrue(_app.LoadScenario(DemoScenario), _app.LastError);
            yield return null;
            float megabytes = _app.Registry.TotalBytes / (1024f * 1024f);
            Debug.Log($"[FireCA test] Texture memory at the 4096 size: {megabytes:F0} MB.");
            Assert.That(megabytes, Is.InRange(900f, 1500f));
        }

        // ---- camera and Overlays ----

        [UnityTest]
        public IEnumerator Camera_HasFourPresets_AndStaysAboveTheTerrain()
        {
            var rig = UnityEngine.Object.FindFirstObjectByType<CameraRig>();
            Assert.IsNotNull(rig);
            yield return null;
            Assert.AreEqual(4, rig.Presets.Count);
            string[] names = { "Overview", "Canyon Mouth", "Chute", "Top Down" };
            float extent = _app.Scenario.Grid.ExtentM;
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(names[i], rig.Presets[i].Name);
                rig.ApplyPreset(i);
                yield return null;
                Vector3 p = rig.transform.position;
                if (p.x >= 0f && p.x <= extent && p.z >= 0f && p.z <= extent)
                    Assert.Greater(p.y, _app.SampleElevation(p.x, p.z), $"Preset {names[i]} is under the terrain.");
                // The preset looks at the Fire Grid.
                Vector3 look = rig.Presets[i].LookAt;
                Assert.That(look.x, Is.InRange(0f, extent));
                Assert.That(look.z, Is.InRange(0f, extent));
            }

            // Fly mode: a camera that moves under the ground returns to the surface.
            rig.SetMode(CameraRig.Mode.Fly);
            Vector3 under = new Vector3(extent * 0.25f, 0f, extent * 0.25f);
            under.y = _app.SampleElevation(under.x, under.z) - 100f;
            rig.transform.position = under;
            yield return null;
            yield return null;
            Assert.Greater(rig.transform.position.y, _app.SampleElevation(under.x, under.z), "The camera must stay above the terrain.");
            rig.SetMode(CameraRig.Mode.Orbit);
        }

        [UnityTest]
        public IEnumerator Overlays_FuelModelAndSlope_AreAvailable()
        {
            yield return null;
            Overlays overlays = _app.Overlays;
            Assert.AreEqual(Overlays.DefaultOpacity, overlays.Opacity);
            Assert.AreEqual(0.8f, Overlays.DefaultOpacity);

            OverlayInfo fuelModel = Overlays.Find("Fuel model"), slope = Overlays.Find("Slope");
            Assert.IsTrue(fuelModel.Available);
            Assert.IsTrue(slope.Available);
            Assert.AreEqual("percent", slope.Unit);
            Assert.AreEqual("0 to 120", slope.RangeText);

            overlays.Select(fuelModel);
            Assert.AreEqual(fuelModel.Id, Mathf.RoundToInt(Shader.GetGlobalVector("_FireCA_OverlayParams").x));
            overlays.Select(slope);
            Assert.AreEqual(slope.Id, Mathf.RoundToInt(Shader.GetGlobalVector("_FireCA_OverlayParams").x));
            overlays.SetOpacity(0.5f);
            Assert.AreEqual(0.5f, Shader.GetGlobalVector("_FireCA_OverlayParams").y, 1e-6f);
            overlays.Select(null);
            overlays.SetOpacity(Overlays.DefaultOpacity);
            Assert.AreEqual(0, Mathf.RoundToInt(Shader.GetGlobalVector("_FireCA_OverlayParams").x));
            Assert.AreEqual(256, overlays.ColorMap.width);
            Assert.AreEqual(1, overlays.ColorMap.height);
        }
    }
}
