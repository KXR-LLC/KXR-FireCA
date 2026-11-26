using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace FireCA
{
    /// <summary>
    /// The textures of the Wind Nest. A name that the Wind Nest does not own comes from the texture registry:
    /// Surface Heating and Heat Flux Coarse.
    /// </summary>
    public sealed class NestTextures : IWindTextures, IDisposable
    {
        readonly Dictionary<string, Texture[]> _textures = new Dictionary<string, Texture[]>();
        readonly TextureRegistry _registry;

        /// <summary>Total GPU memory of the textures of the Wind Nest, bytes.</summary>
        public long TotalBytes { get; private set; }

        public NestTextures(TextureRegistry registry, in WindDomain domain)
        {
            _registry = registry;
            Add3D(SimTextures.Velocity, GraphicsFormat.R32G32B32A32_SFloat, 2, domain, FilterMode.Bilinear);
            Add3D(SimTextures.VelocityScratch, GraphicsFormat.R32G32B32A32_SFloat, 1, domain, FilterMode.Bilinear);
            Add3D(SimTextures.Smoke, GraphicsFormat.R16_SFloat, 2, domain, FilterMode.Bilinear);
            Add3D(SimTextures.SmokeScratch, GraphicsFormat.R16_SFloat, 1, domain, FilterMode.Bilinear);
            Add3D(SimTextures.Pressure, GraphicsFormat.R32_SFloat, 1, domain, FilterMode.Bilinear);
            Add3D(SimTextures.Divergence, GraphicsFormat.R16_SFloat, 1, domain, FilterMode.Bilinear);
            Add3D(SimTextures.SolidMask, GraphicsFormat.R8_UNorm, 1, domain, FilterMode.Bilinear);
            Add3D(SimTextures.WindFlags, GraphicsFormat.R8_UNorm, 1, domain, FilterMode.Point);
            Add3D(SimTextures.Turbulence, GraphicsFormat.R16G16B16A16_SFloat, 1, domain, FilterMode.Bilinear);
            Add2D(SimTextures.GroundHeight, GraphicsFormat.R32_SFloat, domain);
            Add2D(SimTextures.SurfaceWind, GraphicsFormat.R16G16_SFloat, domain);
        }

        void Add3D(string name, GraphicsFormat format, int copies, in WindDomain d, FilterMode filter)
        {
            var list = new Texture[copies];
            for (int c = 0; c < copies; c++)
            {
                var desc = new RenderTextureDescriptor(d.Nx, d.Ny, format, 0, 1)
                {
                    enableRandomWrite = true, msaaSamples = 1, useMipMap = false, autoGenerateMips = false,
                    dimension = TextureDimension.Tex3D, volumeDepth = d.Nz,
                };
                list[c] = Make(desc, $"Wind Nest {name}{(copies > 1 ? (c == 0 ? " (read)" : " (write)") : "")}", filter);
                TotalBytes += d.Cells * GraphicsFormatUtility.GetBlockSize(format);
            }
            _textures[name] = list;
        }

        void Add2D(string name, GraphicsFormat format, in WindDomain d)
        {
            var desc = new RenderTextureDescriptor(d.Nx, d.Nz, format, 0, 1)
            {
                enableRandomWrite = true, msaaSamples = 1, useMipMap = false, autoGenerateMips = false,
            };
            _textures[name] = new[] { Make(desc, $"Wind Nest {name}", FilterMode.Bilinear) };
            TotalBytes += (long)d.Nx * d.Nz * GraphicsFormatUtility.GetBlockSize(format);
        }

        static Texture Make(RenderTextureDescriptor desc, string label, FilterMode filter)
        {
            var rt = new RenderTexture(desc)
            {
                name = label, filterMode = filter, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            return rt;
        }

        public Texture Get(string name) => _textures.TryGetValue(name, out var list) ? list[0] : _registry.Get(name);

        public Texture GetWrite(string name)
        {
            var list = _textures[name];
            if (list.Length < 2) throw new InvalidOperationException($"'{name}' is not a two-copy texture of the Wind Nest.");
            return list[1];
        }

        public void Swap(string name)
        {
            var list = _textures[name];
            (list[0], list[1]) = (list[1], list[0]);
        }

        public void Exchange(string a, string b)
        {
            Texture[] first = _textures[a], second = _textures[b];
            (first[0], second[0]) = (second[0], first[0]);
        }

        public void Dispose()
        {
            foreach (var list in _textures.Values)
            {
                foreach (var tex in list)
                {
                    if (tex == null) continue;
                    if (tex is RenderTexture rt) rt.Release();
                    if (Application.isPlaying) UnityEngine.Object.Destroy(tex);
                    else UnityEngine.Object.DestroyImmediate(tex);
                }
            }
            _textures.Clear();
            TotalBytes = 0;
        }
    }

    /// <summary>
    /// The Wind Nest: a second grid of the Wind Solver with smaller cells, over a box inside the Wind Grid
    /// (docs/decisions.md, decision D32). The Wind Grid feeds the Wind Nest through the relaxation zones at the faces
    /// of the box. The Wind Nest does not change the Wind Grid. The Surface Wind takes the Surface Wind of the
    /// Wind Nest inside the box, so the fire sees the Wind Nest.
    /// </summary>
    public sealed class WindNest : IDisposable
    {
        public const int MaxCellsXZ = 192;
        public const int CellsY = 128;
        public const float DefaultCellSizeM = 12f;
        public const float MinCellSizeM = 8f;
        public const float MaxCellSizeM = 16f;
        /// <summary>Relaxation time at the outer face of the Wind Nest, s.</summary>
        public const float SpongeTimeS = 6f;

        readonly ComputeShader _windCompute, _derivedCompute;
        readonly TextureRegistry _registry;
        readonly WeatherModel _weather;
        readonly ScenarioFiles _files;
        readonly WindSolver _parent;
        NestTextures _textures;

        /// <summary>Steps of the Wind Nest per Wind step. The step of the Wind Nest is dtWind over this value.</summary>
        public int Substeps = 2;

        public bool Active { get; private set; }
        public WindDomain Domain { get; private set; }
        /// <summary>The Wind Solver instance that runs the grid of the Wind Nest.</summary>
        public WindSolver Solver { get; private set; }
        public long TotalBytes => _textures != null ? _textures.TotalBytes : 0;
        /// <summary>The count of placements. A reader of the textures binds again when the value changes.</summary>
        public int Placements { get; private set; }

        /// <summary>The width of the zone at the faces of the box where the display and the Surface Wind blend, m.</summary>
        public float RampM => Solver != null ? Solver.SpongeCells * Domain.CellSize : 0f;

        public Texture Velocity => _textures.Get(SimTextures.Velocity);
        public Texture Smoke => _textures.Get(SimTextures.Smoke);
        public Texture Turbulence => _textures.Get(SimTextures.Turbulence);
        public Texture SurfaceWind => _textures.Get(SimTextures.SurfaceWind);
        public Texture SolidMask => _textures.Get(SimTextures.SolidMask);
        public Texture GroundHeight => _textures.Get(SimTextures.GroundHeight);

        public WindNest(ComputeShader windCompute, ComputeShader derivedCompute, TextureRegistry registry, WeatherModel weather,
            ScenarioFiles files, WindSolver parent)
        {
            _windCompute = windCompute;
            _derivedCompute = derivedCompute;
            _registry = registry;
            _weather = weather;
            _files = files;
            _parent = parent;
        }

        /// <summary>The cell size, rounded to a multiple of the Fire Grid cell size inside the allowed range.</summary>
        public static float SnapCellSize(float meters)
        {
            float step = 2f * GridSpec.FireCellSizeM;
            return Mathf.Clamp(Mathf.Round(meters / step) * step, MinCellSizeM, MaxCellSizeM);
        }

        /// <summary>The domain of a Wind Nest with a center and a cell size, inside the Fire Grid of a Scenario.</summary>
        public static WindDomain DomainFor(ScenarioFiles files, Vector2 centerXZ, float cellSizeM)
        {
            float cell = SnapCellSize(cellSizeM);
            float extent = files.Grid.ExtentM;
            int cellsXZ = Mathf.Min(MaxCellsXZ, Mathf.FloorToInt(extent / cell) / 8 * 8);
            float size = cellsXZ * cell;
            float Snap(float center) => Mathf.Clamp(Mathf.Round((center - 0.5f * size) / cell) * cell, 0f, Mathf.Floor((extent - size) / cell) * cell);
            var origin = new Vector2(Snap(centerXZ.x), Snap(centerXZ.y));

            // The bottom face: the lowest elevation in the box, rounded down to a multiple of the cell size.
            int n = files.Grid.FireCells;
            int i0 = Mathf.Clamp(Mathf.FloorToInt(origin.x / GridSpec.FireCellSizeM), 0, n - 1), i1 = Mathf.Clamp(Mathf.CeilToInt((origin.x + size) / GridSpec.FireCellSizeM), 1, n);
            int j0 = Mathf.Clamp(Mathf.FloorToInt(origin.y / GridSpec.FireCellSizeM), 0, n - 1), j1 = Mathf.Clamp(Mathf.CeilToInt((origin.y + size) / GridSpec.FireCellSizeM), 1, n);
            float lowest = float.PositiveInfinity;
            for (int j = j0; j < j1; j += 2)
                for (int i = i0; i < i1; i += 2)
                    lowest = Mathf.Min(lowest, files.Elevation[j * n + i]);
            float bottom = Mathf.Floor((lowest - 1f) / cell) * cell;
            return new WindDomain(cellsXZ, CellsY, cellsXZ, cell, origin, bottom);
        }

        /// <summary>
        /// Places the Wind Nest around a center and starts it from the state of the Wind Grid. A second call moves
        /// the Wind Nest, and the Wind Nest starts again from the state of the Wind Grid.
        /// </summary>
        public void Place(Vector2 centerXZ, float cellSizeM)
        {
            WindDomain domain = DomainFor(_files, centerXZ, cellSizeM);
            bool sameSize = _textures != null && domain.Nx == Domain.Nx && Mathf.Approximately(domain.CellSize, Domain.CellSize);
            if (!sameSize)
            {
                _textures?.Dispose();
                _textures = new NestTextures(_registry, domain);
            }
            Solver?.Dispose();
            Domain = domain;

            DerivedLayers.RunForGrid(_derivedCompute, _registry.Get(SimTextures.Elevation), _files.Grid.FireCells, domain,
                _textures.Get(SimTextures.GroundHeight), _textures.Get(SimTextures.SolidMask));

            Solver = new WindSolver(_windCompute, _textures, _weather, _files.Data, domain)
            {
                Parent = _parent,
                SpinUpSteps = 0,
                SpongeTimeS = SpongeTimeS,
                SmokeSourceScale = GridSpec.WindCellSizeM / domain.CellSize,
            };
            SyncTunables();
            Solver.Initialize();
            Active = true;
            Placements++;
        }

        /// <summary>Stops the Wind Nest and releases its textures.</summary>
        public void Remove()
        {
            Active = false;
            Solver?.Dispose();
            Solver = null;
            _textures?.Dispose();
            _textures = null;
            Placements++;
        }

        // The Wind Nest uses the tunables of the Wind Grid.
        void SyncTunables()
        {
            Solver.DtWind = _parent.DtWind / Mathf.Max(1, Substeps);
            Solver.CouplingGain = _parent.CouplingGain;
            Solver.HeatingGain = _parent.HeatingGain;
            Solver.DragCoef = _parent.DragCoef;
            Solver.AdvectionSharpness = _parent.AdvectionSharpness;
            Solver.VorticityConfinement = _parent.VorticityConfinement;
            Solver.PressureIterations = _parent.PressureIterations;
        }

        /// <summary>Runs the steps of the Wind Nest for one Wind step. The Wind Solver of the Wind Grid calls this.</summary>
        public void StepAfterParent()
        {
            if (!Active) return;
            SyncTunables();
            for (int s = 0; s < Mathf.Max(1, Substeps); s++) Solver.Step();
        }

        public void Dispose() => Remove();
    }
}
