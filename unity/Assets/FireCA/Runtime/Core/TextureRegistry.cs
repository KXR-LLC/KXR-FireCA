using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace FireCA
{
    /// <summary>The normative names of the simulation textures (docs/01-data-layers.md).</summary>
    public static class SimTextures
    {
        // Fire Grid
        public const string Elevation = "Elevation";
        public const string Fuel = "Fuel";
        public const string Slope = "Slope";
        public const string Normal = "Normal";
        public const string Insolation = "Insolation";
        public const string Moisture1h = "Moisture 1h";
        public const string SpreadA = "Spread A";
        public const string SpreadB = "Spread B";
        public const string ArrivalTime = "Arrival Time";
        public const string HeatFlux = "Heat Flux";
        // Wind footprint
        public const string GroundHeight = "Ground Height";
        public const string SurfaceHeating = "Surface Heating";
        public const string HeatFluxCoarse = "Heat Flux Coarse";
        public const string SurfaceWind = "Surface Wind";
        public const string ActiveMask = "Active Mask";
        /// <summary>Per-block statistics of the Spread Solver. Not in docs/01-data-layers.md (docs/decisions.md, decision D10).</summary>
        public const string BlockStats = "Block Stats";
        // Wind Grid
        public const string Velocity = "Velocity";
        public const string Smoke = "Smoke";
        public const string Pressure = "Pressure";
        public const string Divergence = "Divergence";
        public const string SolidMask = "Solid Mask";
        /// <summary>Per-cell neighbor flags of the pressure solve. Not in docs/01-data-layers.md (docs/decisions.md, decision D19).</summary>
        public const string WindFlags = "Wind Flags";
        /// <summary>The target of the correct kernel of the advection (docs/decisions.md, decision D31).</summary>
        public const string VelocityScratch = "Velocity Scratch";
        public const string SmokeScratch = "Smoke Scratch";
        /// <summary>Vorticity (1/s) and turbulence speed (m/s) of each Wind Grid cell (decision D31).</summary>
        public const string Turbulence = "Turbulence";
    }

    /// <summary>The texture registry. Creates and owns every simulation texture.</summary>
    public sealed class TextureRegistry : IDisposable, IWindTextures
    {
        public enum GridKind { Fire, Footprint, Wind }

        public readonly struct Spec
        {
            public readonly string Name;
            public readonly GraphicsFormat Format;
            public readonly GridKind Grid;
            public readonly int Copies;
            public readonly bool RandomWrite;
            public readonly FilterMode Filter;

            public Spec(string name, GraphicsFormat format, GridKind grid, int copies = 1,
                bool randomWrite = true, FilterMode filter = FilterMode.Bilinear)
            {
                Name = name; Format = format; Grid = grid; Copies = copies;
                RandomWrite = randomWrite; Filter = filter;
            }
        }

        public static readonly Spec[] Specs =
        {
            new Spec(SimTextures.Elevation, GraphicsFormat.R32_SFloat, GridKind.Fire, randomWrite: false),
            // Fuel is R8_UNorm, not R8_UInt: Direct3D 12 cannot create an R8_UInt Texture2D (docs/decisions.md, decision D4).
            // A shader reads the fuel code with FireCA_LoadFuelCode in FireCACommon.hlsl.
            new Spec(SimTextures.Fuel, GraphicsFormat.R8_UNorm, GridKind.Fire, randomWrite: false, filter: FilterMode.Point),
            new Spec(SimTextures.Slope, GraphicsFormat.R16G16_SFloat, GridKind.Fire),
            new Spec(SimTextures.Normal, GraphicsFormat.R16G16_SFloat, GridKind.Fire),
            new Spec(SimTextures.Insolation, GraphicsFormat.R16_SFloat, GridKind.Fire),
            new Spec(SimTextures.Moisture1h, GraphicsFormat.R16_SFloat, GridKind.Fire),
            new Spec(SimTextures.SpreadA, GraphicsFormat.R32G32B32A32_SFloat, GridKind.Fire),
            new Spec(SimTextures.SpreadB, GraphicsFormat.R16G16B16A16_SFloat, GridKind.Fire),
            new Spec(SimTextures.ArrivalTime, GraphicsFormat.R32_SFloat, GridKind.Fire, copies: 2, filter: FilterMode.Point),
            new Spec(SimTextures.HeatFlux, GraphicsFormat.R16_SFloat, GridKind.Fire),

            new Spec(SimTextures.GroundHeight, GraphicsFormat.R32_SFloat, GridKind.Footprint),
            new Spec(SimTextures.SurfaceHeating, GraphicsFormat.R16_SFloat, GridKind.Footprint),
            new Spec(SimTextures.HeatFluxCoarse, GraphicsFormat.R16_SFloat, GridKind.Footprint),
            new Spec(SimTextures.SurfaceWind, GraphicsFormat.R16G16_SFloat, GridKind.Footprint),
            new Spec(SimTextures.ActiveMask, GraphicsFormat.R8_UNorm, GridKind.Footprint, filter: FilterMode.Point),
            new Spec(SimTextures.BlockStats, GraphicsFormat.R32G32B32A32_SFloat, GridKind.Footprint, filter: FilterMode.Point),

            new Spec(SimTextures.Velocity, GraphicsFormat.R32G32B32A32_SFloat, GridKind.Wind, copies: 2),
            new Spec(SimTextures.Smoke, GraphicsFormat.R16_SFloat, GridKind.Wind, copies: 2),
            new Spec(SimTextures.Pressure, GraphicsFormat.R32_SFloat, GridKind.Wind, copies: 2),
            new Spec(SimTextures.Divergence, GraphicsFormat.R16_SFloat, GridKind.Wind),
            new Spec(SimTextures.SolidMask, GraphicsFormat.R8_UNorm, GridKind.Wind),
            new Spec(SimTextures.WindFlags, GraphicsFormat.R8_UNorm, GridKind.Wind, filter: FilterMode.Point),
            new Spec(SimTextures.VelocityScratch, GraphicsFormat.R32G32B32A32_SFloat, GridKind.Wind),
            new Spec(SimTextures.SmokeScratch, GraphicsFormat.R16_SFloat, GridKind.Wind),
            new Spec(SimTextures.Turbulence, GraphicsFormat.R16G16B16A16_SFloat, GridKind.Wind),
        };

        // Index 0 is the read copy. Index 1 is the write copy of a two-copy texture.
        readonly Dictionary<string, Texture[]> _textures = new Dictionary<string, Texture[]>();

        public GridSpec Grid { get; private set; }
        /// <summary>Total GPU memory of the simulation textures, bytes.</summary>
        public long TotalBytes { get; private set; }

        public void Create(GridSpec grid)
        {
            Dispose();
            Grid = grid;
            foreach (var spec in Specs)
            {
                var copies = new Texture[spec.Copies];
                for (int c = 0; c < spec.Copies; c++)
                {
                    string label = spec.Copies > 1 ? $"{spec.Name} ({(c == 0 ? "read" : "write")})" : spec.Name;
                    copies[c] = spec.RandomWrite ? CreateRenderTexture(spec, grid, label) : CreateTexture2D(spec, grid, label);
                    TotalBytes += BytesOf(spec, grid);
                }
                _textures[spec.Name] = copies;
            }
        }

        static Texture CreateTexture2D(in Spec spec, GridSpec grid, string label)
        {
            var tex = new Texture2D(grid.FireCells, grid.FireCells, spec.Format,
                TextureCreationFlags.DontInitializePixels)
            {
                name = label,
                filterMode = spec.Filter,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            return tex;
        }

        static Texture CreateRenderTexture(in Spec spec, GridSpec grid, string label)
        {
            int w, h, d;
            switch (spec.Grid)
            {
                case GridKind.Fire: w = h = grid.FireCells; d = 1; break;
                case GridKind.Footprint: w = grid.WindCellsX; h = grid.WindCellsZ; d = 1; break;
                default: w = grid.WindCellsX; h = GridSpec.WindCellsY; d = grid.WindCellsZ; break;
            }
            var desc = new RenderTextureDescriptor(w, h, spec.Format, 0, 1)
            {
                enableRandomWrite = true,
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
            };
            if (spec.Grid == GridKind.Wind)
            {
                desc.dimension = TextureDimension.Tex3D;
                desc.volumeDepth = d;
            }
            var rt = new RenderTexture(desc)
            {
                name = label,
                filterMode = spec.Filter,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            return rt;
        }

        static long BytesOf(in Spec spec, GridSpec grid)
        {
            long cells = spec.Grid == GridKind.Fire ? grid.FireCellTotal
                : spec.Grid == GridKind.Footprint ? (long)grid.WindCellsX * grid.WindCellsZ
                : grid.WindCellTotal;
            return cells * GraphicsFormatUtility.GetBlockSize(spec.Format);
        }

        public bool IsCreated => _textures.Count > 0;

        /// <summary>The texture with the given name. For a two-copy texture, the read copy.</summary>
        public Texture Get(string name) => _textures[name][0];

        /// <summary>The write copy of a two-copy texture.</summary>
        public Texture GetWrite(string name)
        {
            var copies = _textures[name];
            if (copies.Length < 2) throw new InvalidOperationException($"'{name}' is not a two-copy texture.");
            return copies[1];
        }

        /// <summary>Swaps the read copy and the write copy of a two-copy texture.</summary>
        public void Swap(string name)
        {
            var copies = _textures[name];
            if (copies.Length < 2) throw new InvalidOperationException($"'{name}' is not a two-copy texture.");
            (copies[0], copies[1]) = (copies[1], copies[0]);
        }

        /// <summary>Exchanges the read copies of two textures with the same format and the same size.</summary>
        public void Exchange(string a, string b)
        {
            Texture[] first = _textures[a], second = _textures[b];
            if (first[0].graphicsFormat != second[0].graphicsFormat || first[0].width != second[0].width)
                throw new InvalidOperationException($"'{a}' and '{b}' do not have the same format and size.");
            (first[0], second[0]) = (second[0], first[0]);
        }

        public void Dispose()
        {
            foreach (var copies in _textures.Values)
            {
                foreach (var tex in copies)
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
}
