using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FireCA
{
    /// <summary>Statistics of the Wind Grid, from one GPU reduction.</summary>
    public struct WindStats
    {
        /// <summary>The largest wind speed, m/s.</summary>
        public float PeakSpeed;
        /// <summary>The count of cells that hold a NaN value or an infinite value.</summary>
        public long NonFiniteCells;
        /// <summary>The mean absolute divergence over the air cells, 1/s.</summary>
        public double MeanAbsDivergence;
        public long AirCells;
        /// <summary>The lowest and the highest theta, K.</summary>
        public float ThetaMin, ThetaMax;
        /// <summary>The highest smoke density and the sum of the smoke density over the Wind Grid.</summary>
        public float SmokeMax;
        public double SmokeTotal;
    }

    /// <summary>
    /// The Wind Solver: the compute shader system that advances the 3D wind field on the Wind Grid
    /// (docs/03-weather-wind.md). One Wind step runs advect, stratify, buoyancy, ground sources, sponge, solid,
    /// divergence, pressure, project, and the Surface Wind write.
    /// </summary>
    public sealed class WindSolver : IDisposable
    {
        // Constants and tunables. The defaults follow the table in docs/03-weather-wind.md.
        public float DtWind = 2.0f;
        public float Gravity = 9.81f;
        public float Theta0 = 300f;
        public float RhoCp = 1105f;
        public float Z0 = 0.5f;
        public float ProfileCapM = 600f;
        public float SpongeCells = 12f;
        public float SpongeTopCells = 16f;
        public float SpongeTimeS = 20f;
        public float DragCoef = 0.02f;
        public float HeatingGain = 1.0f;
        /// <summary>Multiplier on fire heat, from 0 to 2. The value 0 turns fire coupling off.</summary>
        public float CouplingGain = 1.0f;
        public float FireConvFraction = 0.7f;
        public float ThetaMin = -15f;
        public float ThetaMax = 300f;
        public float SmokeRate = 0.002f;
        public float SmokeLifeS = 3600f;
        public int PressureIterations = 30;
        public float SorOmega = 1.6f;
        public float SampleHeightM = 48f;
        public int SpinUpSteps = 300;
        /// <summary>The share of the MacCormack correction of the advection, from 0 to 1 (decision D31). 0 is the semi-Lagrangian advection.</summary>
        public float AdvectionSharpness = DefaultAdvectionSharpness;
        /// <summary>The vorticity confinement factor, from 0 to MaxVorticityConfinement. 0 turns the force off.</summary>
        public float VorticityConfinement = DefaultVorticityConfinement;
        public const float DefaultAdvectionSharpness = 1f;
        public const float DefaultVorticityConfinement = 0f;
        public const float MaxVorticityConfinement = 0.06f;

        /// <summary>Wind Grid cell size over the cell size of this grid. 1 for the Wind Grid.</summary>
        public float SmokeSourceScale = 1f;
        /// <summary>
        /// The Wind Solver of the Wind Grid, when this instance runs the Wind Nest (decision D32). The relaxation
        /// zones then blend toward the state of the parent. Null for the Wind Grid.
        /// </summary>
        public WindSolver Parent;
        /// <summary>
        /// The Wind Nest of this Wind Grid, or null. The Surface Wind kernel takes the Surface Wind of the Wind Nest
        /// inside its box, and each Wind step runs the steps of the Wind Nest.
        /// </summary>
        public WindNest Nest;

        readonly ComputeShader _cs;
        readonly IWindTextures _registry;
        readonly WeatherModel _weather;
        readonly WindDomain _grid;
        /// <summary>The place and the size of the grid of this instance.</summary>
        public WindDomain Domain => _grid;
        readonly float _stabilityKPerM;
        readonly ComputeBuffer _statsBuffer;
        readonly int _init, _flags, _advect, _correct, _turbulence, _forces, _divergence, _pressure, _project, _surfaceWind, _stats;
        bool _disposed;

        /// <summary>When false, the Step Scheduler runs no Wind step, and the Weather Model fills the Surface Wind.</summary>
        public bool Enabled = true;

        /// <summary>The wind time, in seconds from the start of the Scenario. The spin-up does not advance the wind time.</summary>
        public double WindTime { get; private set; }
        public long StepCount { get; private set; }
        /// <summary>The count of spin-up steps that have run.</summary>
        public int SpinUpDone { get; private set; }
        public bool SpunUp => SpinUpDone >= SpinUpSteps;
        public float SpinUpProgress => SpinUpSteps <= 0 ? 1f : Mathf.Clamp01(SpinUpDone / (float)SpinUpSteps);

        public WindSolver(ComputeShader compute, TextureRegistry registry, WeatherModel weather, ScenarioData data)
            : this(compute, registry, weather, data, WindDomain.OfWindGrid(registry.Grid)) { }

        public WindSolver(ComputeShader compute, IWindTextures textures, WeatherModel weather, ScenarioData data, WindDomain domain)
        {
            _cs = compute;
            _registry = textures;
            _weather = weather;
            _grid = domain;
            _stabilityKPerM = data.weather.stabilityKPerM;
            _statsBuffer = new ComputeBuffer(2 * _grid.Nx * _grid.Nz, 4 * sizeof(float), ComputeBufferType.Structured);

            _init = _cs.FindKernel("InitKernel");
            _flags = _cs.FindKernel("FlagsKernel");
            _advect = _cs.FindKernel("AdvectKernel");
            _correct = _cs.FindKernel("CorrectKernel");
            _turbulence = _cs.FindKernel("TurbulenceKernel");
            _forces = _cs.FindKernel("ForcesKernel");
            _divergence = _cs.FindKernel("DivergenceKernel");
            _pressure = _cs.FindKernel("PressureKernel");
            _project = _cs.FindKernel("ProjectKernel");
            _surfaceWind = _cs.FindKernel("SurfaceWindKernel");
            _stats = _cs.FindKernel("StatsKernel");
        }

        int Gx => (_grid.Nx + 3) / 4;
        int Gy => (_grid.Ny + 3) / 4;
        int Gz => (_grid.Nz + 3) / 4;

        Texture Velocity => _registry.Get(SimTextures.Velocity);
        Texture VelocityWrite => _registry.GetWrite(SimTextures.Velocity);

        void SetUniforms()
        {
            _cs.SetInt("_WindGridSizeX", _grid.Nx);
            _cs.SetInt("_WindGridSizeY", _grid.Ny);
            _cs.SetInt("_WindGridSizeZ", _grid.Nz);
            _cs.SetFloat("_WindCellSize", _grid.CellSize);
            _cs.SetFloat("_WindBottomY", _grid.BottomY);
            _cs.SetFloat("_SmokeSourceScale", SmokeSourceScale);

            // The Wind Nest: the parent, and the place of this grid on the footprint of the Wind Grid.
            _cs.SetInt("_IsNest", Parent != null ? 1 : 0);
            if (Parent != null)
            {
                WindDomain parent = Parent._grid;
                Vector3 parentExtent = parent.Extent;
                _cs.SetVector("_FootprintTransform", new Vector4(
                    _grid.Nx * _grid.CellSize / parentExtent.x, _grid.Nz * _grid.CellSize / parentExtent.z,
                    _grid.OriginXZ.x / parentExtent.x, _grid.OriginXZ.y / parentExtent.z));
                _cs.SetVector("_ParentOffset", new Vector4(_grid.OriginXZ.x - parent.OriginXZ.x, _grid.BottomY - parent.BottomY, _grid.OriginXZ.y - parent.OriginXZ.y, 0f));
                _cs.SetVector("_ParentExtent", parentExtent);
                _cs.SetFloat("_ParentCellSize", parent.CellSize);
            }
            else
            {
                _cs.SetVector("_FootprintTransform", new Vector4(1f, 1f, 0f, 0f));
                _cs.SetVector("_ParentOffset", Vector4.zero);
                _cs.SetVector("_ParentExtent", _grid.Extent);
                _cs.SetFloat("_ParentCellSize", _grid.CellSize);
            }
            bool nest = Nest != null && Nest.Active;
            _cs.SetInt("_NestActive", nest ? 1 : 0);
            if (nest)
            {
                WindDomain box = Nest.Domain;
                _cs.SetVector("_NestBox", new Vector4(box.OriginXZ.x, box.OriginXZ.y, box.Nx * box.CellSize, box.Nz * box.CellSize));
                _cs.SetFloat("_NestRampM", Nest.RampM);
            }
            _cs.SetFloat("_DtWind", DtWind);
            _cs.SetFloat("_Gravity", Gravity);
            _cs.SetFloat("_Theta0", Theta0);
            _cs.SetFloat("_RhoCp", RhoCp);
            _cs.SetFloat("_Z0", Z0);
            _cs.SetFloat("_ProfileCapM", ProfileCapM);
            _cs.SetFloat("_SpongeCells", SpongeCells);
            _cs.SetFloat("_SpongeTopCells", SpongeTopCells);
            _cs.SetFloat("_SpongeTimeS", SpongeTimeS);
            _cs.SetFloat("_DragCoef", DragCoef);
            _cs.SetFloat("_HeatingGain", HeatingGain);
            _cs.SetFloat("_CouplingGain", CouplingGain);
            _cs.SetFloat("_FireConvFraction", FireConvFraction);
            _cs.SetFloat("_ThetaMin", ThetaMin);
            _cs.SetFloat("_ThetaMax", ThetaMax);
            _cs.SetFloat("_SmokeRate", SmokeRate);
            _cs.SetFloat("_SmokeLifeS", SmokeLifeS);
            _cs.SetFloat("_SorOmega", SorOmega);
            _cs.SetFloat("_SampleHeightM", SampleHeightM);
            _cs.SetFloat("_StabilityKPerM", _stabilityKPerM);
            _cs.SetFloat("_AdvectionSharpness", Mathf.Clamp01(AdvectionSharpness));
            _cs.SetFloat("_VorticityConfinement", Mathf.Clamp(VorticityConfinement, 0f, MaxVorticityConfinement));
            Vector2 wind = _weather.BackgroundWind;
            _cs.SetVector("_BackgroundWind", new Vector4(wind.x, wind.y, 0f, 0f));
        }

        void BindCommon(int kernel)
        {
            _cs.SetTexture(kernel, "_SolidMask", _registry.Get(SimTextures.SolidMask));
            _cs.SetTexture(kernel, "_GroundHeight", _registry.Get(SimTextures.GroundHeight));
        }

        // The textures of the parent. The Wind Grid has no parent and binds its own textures, which no kernel then reads.
        void BindParent(int kernel)
        {
            IWindTextures source = Parent != null ? Parent._registry : _registry;
            _cs.SetTexture(kernel, "_ParentVelocity", source.Get(SimTextures.Velocity));
            _cs.SetTexture(kernel, "_ParentSmoke", source.Get(SimTextures.Smoke));
        }

        /// <summary>
        /// Builds the cell flags, fills the velocity with background(h), and clears theta, the smoke, and the pressure.
        /// The spin-up runs after this call: SpinUp runs every step at once, and the Step Scheduler runs a few steps per frame.
        /// </summary>
        public void Initialize()
        {
            SetUniforms();
            BindCommon(_flags);
            _cs.SetTexture(_flags, "_FlagsOut", _registry.Get(SimTextures.WindFlags));
            _cs.Dispatch(_flags, Gx, Gy, Gz);

            // Both copies of Velocity and Smoke start with the same content.
            for (int pass = 0; pass < 2; pass++)
            {
                BindCommon(_init);
                BindParent(_init);
                _cs.SetTexture(_init, "_VelocityOut", pass == 0 ? Velocity : VelocityWrite);
                _cs.SetTexture(_init, "_SmokeOut", pass == 0 ? _registry.Get(SimTextures.Smoke) : _registry.GetWrite(SimTextures.Smoke));
                _cs.SetTexture(_init, "_Pressure", _registry.Get(SimTextures.Pressure));
                _cs.Dispatch(_init, Gx, Gy, Gz);
            }
            WindTime = 0.0;
            StepCount = 0;
            SpinUpDone = 0;
            WriteSurfaceWind();
            WriteTurbulence();
        }

        /// <summary>Runs the remaining spin-up steps at once. The spin-up does not advance the wind time.</summary>
        public void SpinUp()
        {
            while (!SpunUp) SpinUpStep();
        }

        /// <summary>Runs one spin-up step.</summary>
        public void SpinUpStep()
        {
            if (SpunUp) return;
            RunStep();
            SpinUpDone++;
        }

        /// <summary>Runs one Wind step and advances the wind time by dtWind.</summary>
        public void Step()
        {
            RunStep();
            WindTime += DtWind;
            StepCount++;
            if (Nest != null && Nest.Active) Nest.StepAfterParent();
        }

        /// <summary>Sets the wind time. The Step Scheduler calls this when the Sim Clock restarts.</summary>
        public void SetWindTime(double seconds) => WindTime = seconds;

        void RunStep()
        {
            SetUniforms();
            AdvectAndForces();
            ComputeDivergence();
            SolvePressureAndProject();
            WriteSurfaceWind();
            WriteTurbulence();
        }

        // Steps 1 to 6. The result is in the read copy of Velocity.
        void AdvectAndForces()
        {
            // 1. Advect: read copy to write copy.
            BindCommon(_advect);
            BindParent(_advect);
            _cs.SetTexture(_advect, "_VelocityRead", Velocity);
            _cs.SetTexture(_advect, "_VelocityOut", VelocityWrite);
            _cs.SetTexture(_advect, "_SmokeRead", _registry.Get(SimTextures.Smoke));
            _cs.SetTexture(_advect, "_SmokeOut", _registry.GetWrite(SimTextures.Smoke));
            _cs.SetTexture(_advect, "_HeatFluxCoarse", _registry.Get(SimTextures.HeatFluxCoarse));
            _cs.Dispatch(_advect, Gx, Gy, Gz);

            if (AdvectionSharpness > 0f)
            {
                // 1b. Correct: the read copies and the write copies to the scratch textures. The scratch textures
                // then become the read copies.
                BindCommon(_correct);
                BindParent(_correct);
                _cs.SetTexture(_correct, "_VelocityRead", Velocity);
                _cs.SetTexture(_correct, "_VelocityHat", VelocityWrite);
                _cs.SetTexture(_correct, "_SmokeRead", _registry.Get(SimTextures.Smoke));
                _cs.SetTexture(_correct, "_SmokeHat", _registry.GetWrite(SimTextures.Smoke));
                _cs.SetTexture(_correct, "_HeatFluxCoarse", _registry.Get(SimTextures.HeatFluxCoarse));
                _cs.SetTexture(_correct, "_VelocityOut", _registry.Get(SimTextures.VelocityScratch));
                _cs.SetTexture(_correct, "_SmokeOut", _registry.Get(SimTextures.SmokeScratch));
                _cs.Dispatch(_correct, Gx, Gy, Gz);
                _registry.Exchange(SimTextures.Velocity, SimTextures.VelocityScratch);
                _registry.Exchange(SimTextures.Smoke, SimTextures.SmokeScratch);
            }
            else
            {
                _registry.Swap(SimTextures.Velocity);
                _registry.Swap(SimTextures.Smoke);
            }

            // 2 to 6. Stratify, buoyancy, vorticity confinement, ground sources, sponge, solid.
            BindCommon(_forces);
            BindParent(_forces);
            _cs.SetTexture(_forces, "_TurbulenceRead", _registry.Get(SimTextures.Turbulence));
            _cs.SetTexture(_forces, "_VelocityRead", Velocity);
            _cs.SetTexture(_forces, "_VelocityOut", VelocityWrite);
            _cs.SetTexture(_forces, "_SurfaceHeating", _registry.Get(SimTextures.SurfaceHeating));
            _cs.SetTexture(_forces, "_HeatFluxCoarse", _registry.Get(SimTextures.HeatFluxCoarse));
            _cs.Dispatch(_forces, Gx, Gy, Gz);
            _registry.Swap(SimTextures.Velocity);
        }

        // Step 7. Writes the Divergence texture from the read copy of Velocity.
        void ComputeDivergence()
        {
            BindCommon(_divergence);
            _cs.SetTexture(_divergence, "_VelocityRead", Velocity);
            _cs.SetTexture(_divergence, "_DivergenceOut", _registry.Get(SimTextures.Divergence));
            _cs.Dispatch(_divergence, Gx, Gy, Gz);
        }

        // Steps 8 and 9.
        void SolvePressureAndProject()
        {
            _cs.SetTexture(_pressure, "_FlagsRead", _registry.Get(SimTextures.WindFlags));
            _cs.SetTexture(_pressure, "_DivergenceRead", _registry.Get(SimTextures.Divergence));
            _cs.SetTexture(_pressure, "_Pressure", _registry.Get(SimTextures.Pressure));
            for (int iteration = 0; iteration < PressureIterations; iteration++)
            {
                _cs.SetInt("_Parity", 0);
                _cs.Dispatch(_pressure, Gx, Gy, Gz);
                _cs.SetInt("_Parity", 1);
                _cs.Dispatch(_pressure, Gx, Gy, Gz);
            }

            _cs.SetTexture(_project, "_FlagsRead", _registry.Get(SimTextures.WindFlags));
            _cs.SetTexture(_project, "_PressureRead", _registry.Get(SimTextures.Pressure));
            _cs.SetTexture(_project, "_VelocityRead", Velocity);
            _cs.SetTexture(_project, "_VelocityOut", VelocityWrite);
            _cs.Dispatch(_project, Gx, Gy, Gz);
            _registry.Swap(SimTextures.Velocity);
        }

        /// <summary>Step 10. Writes the Surface Wind from the read copy of Velocity.</summary>
        public void WriteSurfaceWind()
        {
            SetUniforms();
            BindCommon(_surfaceWind);
            _cs.SetTexture(_surfaceWind, "_VelocityRead", Velocity);
            _cs.SetTexture(_surfaceWind, "_SurfaceWindOut", _registry.Get(SimTextures.SurfaceWind));
            _cs.SetTexture(_surfaceWind, "_NestSurfaceWind", Nest != null && Nest.Active ? Nest.SurfaceWind : Texture2D.blackTexture);
            _cs.Dispatch(_surfaceWind, (_grid.Nx + 7) / 8, (_grid.Nz + 7) / 8, 1);
        }

        /// <summary>Step 11. Writes the Turbulence texture from the read copy of Velocity.</summary>
        public void WriteTurbulence()
        {
            SetUniforms();
            BindCommon(_turbulence);
            _cs.SetTexture(_turbulence, "_VelocityRead", Velocity);
            _cs.SetTexture(_turbulence, "_TurbulenceOut", _registry.Get(SimTextures.Turbulence));
            _cs.Dispatch(_turbulence, Gx, Gy, Gz);
        }

        /// <summary>Reads the statistics of the read copy of Velocity. The call waits for the GPU.</summary>
        public WindStats ReadStats()
        {
            SetUniforms();
            BindCommon(_stats);
            _cs.SetTexture(_stats, "_VelocityRead", Velocity);
            _cs.SetTexture(_stats, "_SmokeRead", _registry.Get(SimTextures.Smoke));
            _cs.SetBuffer(_stats, "_StatsOut", _statsBuffer);
            _cs.Dispatch(_stats, (_grid.Nx + 7) / 8, (_grid.Nz + 7) / 8, 1);
            var request = AsyncGPUReadback.Request(_statsBuffer);
            request.WaitForCompletion();
            var stats = new WindStats();
            if (request.hasError) return stats;
            NativeArray<Vector4> columns = request.GetData<Vector4>();
            double divergence = 0.0, air = 0.0, bad = 0.0;
            for (int i = 0; i < columns.Length; i += 2)
            {
                stats.PeakSpeed = Mathf.Max(stats.PeakSpeed, columns[i].x);
                bad += columns[i].y;
                divergence += columns[i].z;
                air += columns[i].w;
                stats.ThetaMin = Mathf.Min(stats.ThetaMin, columns[i + 1].x);
                stats.ThetaMax = Mathf.Max(stats.ThetaMax, columns[i + 1].y);
                stats.SmokeMax = Mathf.Max(stats.SmokeMax, columns[i + 1].z);
                stats.SmokeTotal += columns[i + 1].w;
            }
            stats.NonFiniteCells = (long)Math.Round(bad);
            stats.AirCells = (long)Math.Round(air);
            stats.MeanAbsDivergence = air > 0.0 ? divergence / air : 0.0;
            return stats;
        }

        /// <summary>
        /// Runs one Wind step and reports the mean absolute divergence over the air cells before the pressure kernel
        /// and after the project kernel. The acceptance test of the projection uses this.
        /// </summary>
        public void StepWithDivergenceReport(out double before, out double after)
        {
            SetUniforms();
            AdvectAndForces();
            ComputeDivergence();
            before = ReadStats().MeanAbsDivergence;
            SolvePressureAndProject();
            after = ReadStats().MeanAbsDivergence;
            WriteSurfaceWind();
            WriteTurbulence();
            WindTime += DtWind;
            StepCount++;
        }

        /// <summary>Clears both copies of the Smoke texture.</summary>
        public void ClearSmoke()
        {
            SetUniforms();
            int k = _cs.FindKernel("ClearSmokeKernel");
            _cs.SetTexture(k, "_SmokeOut", _registry.Get(SimTextures.Smoke));
            _cs.Dispatch(k, Gx, Gy, Gz);
            _cs.SetTexture(k, "_SmokeOut", _registry.GetWrite(SimTextures.Smoke));
            _cs.Dispatch(k, Gx, Gy, Gz);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _statsBuffer.Release();
        }
    }
}
