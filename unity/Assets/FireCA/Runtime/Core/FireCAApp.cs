using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace FireCA
{
    /// <summary>Reads values from the command line of the player.</summary>
    public static class CommandLine
    {
        public static string Value(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }

    /// <summary>
    /// The root component of KXR-FireCA. Owns the texture registry and the simulation systems, loads a Scenario,
    /// builds the Terrain Mesh, and runs the Step Scheduler in each frame.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class FireCAApp : MonoBehaviour
    {
        public static FireCAApp Instance { get; private set; }

        [SerializeField] ComputeShader derivedLayersCompute;
        [SerializeField] ComputeShader weatherCompute;
        [SerializeField] ComputeShader spreadCompute;
        [SerializeField] ComputeShader windCompute;
        [SerializeField] Material terrainMaterial;
        [SerializeField] Light sunLight;
        [SerializeField] string startupScenario = "synthetic-canyon-01";

        public TextureRegistry Registry { get; } = new TextureRegistry();
        public Overlays Overlays { get; private set; }
        public TerrainMesh Terrain { get; } = new TerrainMesh();
        public SimClock Clock { get; } = new SimClock();
        public WeatherModel Weather { get; private set; }
        public SpreadSolver Spread { get; private set; }
        public WindSolver Wind { get; private set; }
        /// <summary>
        /// When false, the next Scenario load turns the Wind Solver off, and the Weather Model fills the Surface Wind
        /// with the background wind. The PlayMode fire tests and weather tests set false.
        /// </summary>
        public bool WindSolverEnabled { get; set; } = true;
        public StepScheduler Scheduler { get; private set; }

        /// <summary>The loaded Scenario, or null.</summary>
        public ScenarioFiles Scenario { get; private set; }
        /// <summary>The CPU copy of Ground Height. Row-major, row 0 is the south edge.</summary>
        public float[] GroundHeight { get; private set; }
        /// <summary>The time of the last Scenario load, in seconds.</summary>
        public float LastLoadSeconds { get; private set; }
        /// <summary>The text of the last load failure, or null.</summary>
        public string LastError { get; private set; }
        /// <summary>The validation check that rejected the last load, or null.</summary>
        public ScenarioCheck? LastFailedCheck { get; private set; }

        /// <summary>The Smoke Pass draws the smoke while true.</summary>
        public bool SmokeVisible { get; set; } = true;
        /// <summary>Multiplier on the smoke density in the Smoke Pass.</summary>
        public float SmokeDensity { get; set; } = 1f;
        public const float MaxSmokeDensity = 16f;
        /// <summary>The share of the light that the smoke absorbs, from 0 (white smoke) to 1 (black smoke).</summary>
        public float SmokeDarkness { get; set; } = 0.3f;
        /// <summary>The strength of the billows in the Smoke Pass, from 0 to 1.</summary>
        public float SmokeBillows { get; set; } = 0.5f;
        /// <summary>The fire coupling gain that the Control Panel sets, from 0 to 2. The value applies to each loaded Scenario.</summary>
        public const float MaxTurbulenceGain = 3f;
        /// <summary>
        /// Multiplier on the turbulence speed in the Wind Particles and the Smoke Pass (decision D31). The value 0
        /// turns the turbulence of the display off. The value does not change the Wind Solver or the fire.
        /// </summary>
        public float TurbulenceGain { get => _turbulenceGain; set => _turbulenceGain = Mathf.Clamp(value, 0f, MaxTurbulenceGain); }
        float _turbulenceGain = 1f;

        /// <summary>The share of the MacCormack correction in the advection of the Wind Solver, from 0 to 1 (decision D31).</summary>
        public float AdvectionSharpness
        {
            get => _advectionSharpness;
            set { _advectionSharpness = Mathf.Clamp01(value); if (Wind != null) Wind.AdvectionSharpness = _advectionSharpness; }
        }
        float _advectionSharpness = WindSolver.DefaultAdvectionSharpness;

        /// <summary>The vorticity confinement factor of the Wind Solver (decision D31). The value 0 turns the force off.</summary>
        public float VorticityConfinement
        {
            get => _vorticityConfinement;
            set { _vorticityConfinement = Mathf.Clamp(value, 0f, WindSolver.MaxVorticityConfinement); if (Wind != null) Wind.VorticityConfinement = _vorticityConfinement; }
        }
        float _vorticityConfinement = WindSolver.DefaultVorticityConfinement;

        /// <summary>The Wind Nest of the loaded Scenario (docs/decisions.md, decision D32). Not placed until a fire starts or the user places it.</summary>
        public WindNest Nest { get; private set; }

        /// <summary>When false, the app does not place the Wind Nest, and a placed Wind Nest is removed.</summary>
        public bool WindNestEnabled
        {
            get => _windNestEnabled;
            set { _windNestEnabled = value; if (!value && Nest != null && Nest.Active) Nest.Remove(); }
        }
        bool _windNestEnabled = true;

        /// <summary>The cell size of the Wind Nest, m. A change takes effect at the next placement.</summary>
        public float WindNestCellSizeM { get => _windNestCellSizeM; set => _windNestCellSizeM = WindNest.SnapCellSize(value); }
        float _windNestCellSizeM = WindNest.DefaultCellSizeM;

        /// <summary>
        /// Places the Wind Nest around a center (world X, Z), or moves it there. Returns false when the Wind Nest is
        /// off or the Wind Solver has not finished the spin-up.
        /// </summary>
        public bool PlaceWindNest(Vector2 centerXZ)
        {
            if (!WindNestEnabled || Nest == null || Wind == null || !Wind.Enabled || !Wind.SpunUp) return false;
            Nest.Place(centerXZ, _windNestCellSizeM);
            return true;
        }

        public float CouplingGain
        {
            get => _couplingGain;
            set { _couplingGain = Mathf.Clamp(value, 0f, 2f); if (Wind != null) Wind.CouplingGain = _couplingGain; }
        }
        float _couplingGain = 1f;

        /// <summary>While true, a click on the terrain ignites the cells at the hit point.</summary>
        public bool IgniteToolOn { get; set; }
        /// <summary>When false, Update does not run the Step Scheduler. The PlayMode fire tests drive the solver directly.</summary>
        public bool SchedulerEnabled { get; set; } = true;
        /// <summary>Returns true when the pointer is over the UI.</summary>
        public Func<Vector2, bool> PointerBlocked;

        /// <summary>Raised after a Scenario load succeeds or fails.</summary>
        public event Action ScenarioChanged;

        public ComputeShader DerivedLayersCompute { get => derivedLayersCompute; set => derivedLayersCompute = value; }
        public ComputeShader WeatherCompute { get => weatherCompute; set => weatherCompute = value; }
        public ComputeShader SpreadCompute { get => spreadCompute; set => spreadCompute = value; }
        public ComputeShader WindCompute { get => windCompute; set => windCompute = value; }
        public Material TerrainMaterial { get => terrainMaterial; set => terrainMaterial = value; }
        public Light SunLight { get => sunLight; set => sunLight = value; }
        public string StartupScenario { get => startupScenario; set => startupScenario = value; }

        void Awake()
        {
            Instance = this;
            // The simulation keeps running when the window does not have the focus.
            Application.runInBackground = true;
            Overlays = new Overlays();
            if (sunLight == null) sunLight = RenderSettings.sun;
            if (sunLight != null) _sunBaseIntensity = sunLight.intensity;
        }

        float _sunBaseIntensity = 100000f;

        void Start()
        {
            string id = CommandLine.Value("-scenario") ?? startupScenario;
            if (!string.IsNullOrEmpty(id)) LoadScenario(id);
        }

        void OnDestroy()
        {
            Spread?.Dispose();
            Nest?.Dispose();
            Wind?.Dispose();
            Terrain.Clear();
            Registry.Dispose();
            Overlays?.Dispose();
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (Scenario == null || Spread == null) return;
            if (SchedulerEnabled)
            {
                Scheduler.Tick(Time.unscaledDeltaTime);
                UpdateIgniteTool();
                // The Wind Nest goes around the first ignition. The user moves it from the Control Panel.
                if (WindNestEnabled && Nest != null && !Nest.Active && Spread.LastIgnitionXZ.HasValue)
                    PlaceWindNest(Spread.LastIgnitionXZ.Value);
            }
            Overlays.BindFire(Registry, (float)Clock.Seconds, Spread.SourceHoldS);
            Overlays.BindWeather(Registry, Weather);
            Overlays.BindWind(Registry, Nest);
        }

        void UpdateIgniteTool()
        {
            if (!IgniteToolOn) return;
            var mouse = Mouse.current;
            var camera = Camera.main;
            if (mouse == null || camera == null || !mouse.leftButton.wasPressedThisFrame) return;
            Vector2 pointer = mouse.position.ReadValue();
            if (PointerBlocked != null && PointerBlocked(pointer)) return;
            if (RaycastTerrain(camera.ScreenPointToRay(pointer), out Vector3 hit)) IgniteAt(hit.x, hit.z);
        }

        /// <summary>Ignites the burnable cells within 6 m of a world position, at the Sim Clock value.</summary>
        public void IgniteAt(float xM, float zM)
        {
            if (Spread == null) return;
            Spread.Ignite(new Vector2(xM, zM), SpreadSolver.IgniteToolRadiusM, Clock.Seconds);
        }

        /// <summary>Sets every cell to "not ignited".</summary>
        public void ClearFire()
        {
            Spread?.ClearFire();
            if (Wind != null && Wind.Enabled) Wind.ClearSmoke();
        }

        /// <summary>Loads the Scenario with the given identifier from StreamingAssets/Scenarios.</summary>
        public bool LoadScenario(string id) => LoadScenarioFolder(Path.Combine(ScenarioLoader.ScenariosRoot, id));

        /// <summary>Reloads the current Scenario and sets the Sim Clock to zero.</summary>
        public bool ResetScenario()
        {
            if (Scenario == null) return false;
            return Scenario.Folder != null ? LoadScenarioFolder(Scenario.Folder) : LoadScenarioFiles(Scenario);
        }

        public bool LoadScenarioFolder(string folder)
        {
            var watch = Stopwatch.StartNew();
            ScenarioFiles files;
            try
            {
                files = ScenarioLoader.Load(folder);
            }
            catch (ScenarioException e)
            {
                LastError = e.DisplayText;
                LastFailedCheck = e.Check;
                Debug.LogWarning($"[FireCA] {e.DisplayText}");
                ScenarioChanged?.Invoke();
                return false;
            }
            return LoadScenarioFiles(files, watch);
        }

        /// <summary>Loads a Scenario that is already in memory. The caller must have validated the Scenario.</summary>
        public bool LoadScenarioFiles(ScenarioFiles files, Stopwatch watch = null)
        {
            watch ??= Stopwatch.StartNew();
            Spread?.Dispose();
            Spread = null;
            Nest?.Dispose();
            Nest = null;
            Wind?.Dispose();
            Wind = null;

            Registry.Create(files.Grid);

            var elevation = (Texture2D)Registry.Get(SimTextures.Elevation);
            elevation.SetPixelData(files.Elevation, 0);
            elevation.Apply(false, true);
            var fuel = (Texture2D)Registry.Get(SimTextures.Fuel);
            fuel.SetPixelData(files.Fuel, 0);
            fuel.Apply(false, true);

            DerivedLayers.Run(derivedLayersCompute, Registry);

            Clock.Reset(files.Data.startSolarHour);
            Clock.Playing = false;
            Weather = new WeatherModel(weatherCompute, Registry)
            {
                SunLight = sunLight, SunBaseIntensity = _sunBaseIntensity, FillsSurfaceWind = !WindSolverEnabled,
            };
            Weather.Initialize(files);
            // The Wind Solver fills the velocity with the background wind. The spin-up runs in the Step Scheduler.
            Wind = new WindSolver(windCompute, Registry, Weather, files.Data) { Enabled = WindSolverEnabled, CouplingGain = _couplingGain, AdvectionSharpness = _advectionSharpness, VorticityConfinement = _vorticityConfinement };
            if (Wind.Enabled)
            {
                Wind.Initialize();
                Nest = new WindNest(windCompute, derivedLayersCompute, Registry, Weather, files, Wind);
                Wind.Nest = Nest;
            }
            Spread = new SpreadSolver(spreadCompute, Registry, Weather);
            Spread.Reset();
            Scheduler = new StepScheduler(Clock, Weather, Wind, Spread, files.Data, files.Grid);

            // The readback waits for the GPU, so the load time includes the compute passes.
            var request = AsyncGPUReadback.Request(Registry.Get(SimTextures.GroundHeight));
            request.WaitForCompletion();
            GroundHeight = request.hasError ? null : request.GetData<float>().ToArray();

            Scenario = files;
            Terrain.Build(files, terrainMaterial, transform);
            Overlays.Bind(Registry);
            Overlays.SetGroundImage(LoadGroundImage(files));
            Overlays.BindFire(Registry, 0f, Spread.SourceHoldS);
            Overlays.BindWeather(Registry, Weather);
            Overlays.BindWind(Registry, Nest);

            watch.Stop();
            LastLoadSeconds = (float)watch.Elapsed.TotalSeconds;
            LastError = null;
            LastFailedCheck = null;
            Debug.Log($"[FireCA] Loaded Scenario '{files.Id}' ({files.Grid.FireCells} cells per side) in {LastLoadSeconds:F2} s.");
            ScenarioChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Reads the Ground Image of a Scenario. Returns null when the Scenario has none or the file is not an image.
        /// The top row of the image is the north edge. Unity puts the bottom row of an image at v = 0, the south edge.
        /// </summary>
        static Texture2D LoadGroundImage(ScenarioFiles files)
        {
            if (string.IsNullOrEmpty(files.GroundImagePath)) return null;
            var image = new Texture2D(2, 2, TextureFormat.RGB24, true, false)
            {
                name = "Ground Image", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 8,
                hideFlags = HideFlags.HideAndDontSave,
            };
            if (!image.LoadImage(System.IO.File.ReadAllBytes(files.GroundImagePath), true))
            {
                Debug.LogWarning($"[FireCA] The Ground Image '{files.Data.groundImageFile}' is not a JPEG or PNG image.");
                Destroy(image);
                return null;
            }
            return image;
        }

        /// <summary>The ground elevation at a world position, from the CPU copy of Elevation. Bilinear.</summary>
        public float SampleElevation(float xM, float zM)
        {
            if (Scenario == null) return 0f;
            int n = Scenario.Grid.FireCells;
            float fx = Mathf.Clamp(xM / GridSpec.FireCellSizeM - 0.5f, 0f, n - 1);
            float fz = Mathf.Clamp(zM / GridSpec.FireCellSizeM - 0.5f, 0f, n - 1);
            int x0 = (int)fx, z0 = (int)fz;
            int x1 = Mathf.Min(x0 + 1, n - 1), z1 = Mathf.Min(z0 + 1, n - 1);
            float tx = fx - x0, tz = fz - z0;
            float[] e = Scenario.Elevation;
            float a = Mathf.Lerp(e[z0 * n + x0], e[z0 * n + x1], tx);
            float b = Mathf.Lerp(e[z1 * n + x0], e[z1 * n + x1], tx);
            return Mathf.Lerp(a, b, tz);
        }

        /// <summary>Finds the point where a ray meets the terrain, from the CPU copy of Elevation.</summary>
        public bool RaycastTerrain(Ray ray, out Vector3 hit)
        {
            hit = default;
            if (Scenario == null) return false;
            float extent = Scenario.Grid.ExtentM;
            const float step = 2f;
            float maxDistance = extent * 4f;
            bool wasAbove = false;
            float previous = 0f;
            for (float t = 0f; t < maxDistance; t += step)
            {
                Vector3 p = ray.GetPoint(t);
                bool inside = p.x >= 0f && p.x <= extent && p.z >= 0f && p.z <= extent;
                if (!inside)
                {
                    wasAbove = false;
                    if (p.y < Scenario.MinElevation && ray.direction.y <= 0f) return false;
                    continue;
                }
                bool above = p.y > SampleElevation(p.x, p.z);
                if (!above && wasAbove)
                {
                    float lo = previous, hi = t;
                    for (int i = 0; i < 10; i++)
                    {
                        float mid = 0.5f * (lo + hi);
                        Vector3 q = ray.GetPoint(mid);
                        if (q.y > SampleElevation(q.x, q.z)) lo = mid; else hi = mid;
                    }
                    hit = ray.GetPoint(hi);
                    hit.y = SampleElevation(hit.x, hit.z);
                    return true;
                }
                wasAbove = above;
                previous = t;
            }
            return false;
        }
    }
}
