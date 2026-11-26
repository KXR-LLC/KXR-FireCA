using System;
using UnityEngine;

namespace FireCA
{
    /// <summary>One data layer drawn in color on the Terrain Mesh.</summary>
    public sealed class OverlayInfo
    {
        public readonly int Id;
        public readonly string Name;
        public readonly string Unit;
        public readonly string RangeText;
        public readonly bool Categorical;
        /// <summary>False until the milestone that builds the source texture.</summary>
        public readonly bool Available;

        public OverlayInfo(int id, string name, string unit, string rangeText, bool categorical, bool available)
        {
            Id = id; Name = name; Unit = unit; RangeText = rangeText; Categorical = categorical; Available = available;
        }
    }

    /// <summary>The Overlays, the fuel colors, and the shader globals of the terrain shader.</summary>
    public sealed class Overlays : IDisposable
    {
        public const string NoneName = "None";

        // The identifiers match the constants in FireCATerrain.hlsl.
        public static readonly OverlayInfo[] Catalog =
        {
            new OverlayInfo(1, "Fuel model", "fuel code", "13 fuel models", true, true),
            new OverlayInfo(2, "Fuel moisture", "percent", "0 to 25", false, true),
            new OverlayInfo(3, "Rate of spread", "m/s", "0.001 to 5, logarithmic", false, true),
            new OverlayInfo(4, "Flame length", "m", "0 to 20", false, true),
            new OverlayInfo(5, "Arrival time", "min", "bands of 10 simulated minutes", false, true),
            new OverlayInfo(6, "Surface wind", "m/s", "0 to 20", false, true),
            new OverlayInfo(7, "Air temperature", "deg C", "tempMinC - 10 to tempMaxC + 5", false, true),
            new OverlayInfo(8, "Relative humidity", "percent", "0 to 100", false, true),
            new OverlayInfo(9, "Insolation", "W/m2", "0 to 1100", false, true),
            new OverlayInfo(10, "Slope", "percent", "0 to 120", false, true),
        };

        static readonly int FuelId = Shader.PropertyToID("_FireCA_Fuel");
        static readonly int NormalId = Shader.PropertyToID("_FireCA_Normal");
        static readonly int SlopeId = Shader.PropertyToID("_FireCA_Slope");
        static readonly int ColorMapId = Shader.PropertyToID("_FireCA_ColorMap");
        static readonly int FuelPaletteId = Shader.PropertyToID("_FireCA_FuelPalette");
        static readonly int GridParamsId = Shader.PropertyToID("_FireCA_GridParams");
        static readonly int OverlayParamsId = Shader.PropertyToID("_FireCA_OverlayParams");

        public const float DefaultOpacity = 0.8f;

        /// <summary>The perceptual color map of every scalar Overlay, 256 x 1.</summary>
        public Texture2D ColorMap { get; }
        /// <summary>Row 0 holds the natural fuel colors. Row 1 holds the categorical palette. The fuel code is the column.</summary>
        public Texture2D FuelPalette { get; }

        /// <summary>The selected Overlay, or null for none.</summary>
        public OverlayInfo Selected { get; private set; }
        public float Opacity { get; private set; } = DefaultOpacity;

        public const float DefaultFuelColorSmoothingM = 24f;
        public const float MaxFuelColorSmoothingM = 60f;
        static readonly int TerrainParamsId = Shader.PropertyToID("_FireCA_TerrainParams");

        /// <summary>
        /// The width of the fuel color smoothing, m. The value 0 shows the fuel color of each Fire Grid cell.
        /// The smoothing changes the fuel color layer only (docs/decisions.md, decision D28).
        /// </summary>
        public float FuelColorSmoothingM { get; private set; } = DefaultFuelColorSmoothingM;

        static readonly int GroundImageId = Shader.PropertyToID("_FireCA_GroundImage");

        /// <summary>The Ground Image of the loaded Scenario, or null (docs/decisions.md, decision D29).</summary>
        public Texture2D GroundImage { get; private set; }
        /// <summary>The share of the Ground Image in the ground color, from 0 to 1. The rest is the fuel color.</summary>
        public float GroundImageOpacity { get; private set; } = 1f;

        /// <summary>Takes the Ground Image of a Scenario and destroys the Ground Image of the Scenario before.</summary>
        public void SetGroundImage(Texture2D image)
        {
            if (GroundImage != null && GroundImage != image) UnityEngine.Object.Destroy(GroundImage);
            GroundImage = image;
            Shader.SetGlobalTexture(GroundImageId, image != null ? image : Texture2D.blackTexture);
            Apply();
        }

        public void SetGroundImageOpacity(float opacity)
        {
            GroundImageOpacity = Mathf.Clamp01(opacity);
            Apply();
        }

        public void SetFuelColorSmoothing(float meters)
        {
            FuelColorSmoothingM = Mathf.Clamp(meters, 0f, MaxFuelColorSmoothingM);
            Apply();
        }

        public Overlays()
        {
            ColorMap = BuildColorMap();
            FuelPalette = BuildFuelPalette();
            Shader.SetGlobalTexture(ColorMapId, ColorMap);
            Shader.SetGlobalTexture(FuelPaletteId, FuelPalette);
            Shader.SetGlobalVector(GridParamsId, Vector4.zero);
            Shader.SetGlobalTexture(GroundImageId, Texture2D.blackTexture);
            Apply();
        }

        /// <summary>Binds the textures of a loaded Scenario to the terrain shader.</summary>
        public void Bind(TextureRegistry registry)
        {
            GridSpec grid = registry.Grid;
            Shader.SetGlobalTexture(FuelId, registry.Get(SimTextures.Fuel));
            Shader.SetGlobalTexture(NormalId, registry.Get(SimTextures.Normal));
            Shader.SetGlobalTexture(SlopeId, registry.Get(SimTextures.Slope));
            Shader.SetGlobalVector(GridParamsId,
                new Vector4(grid.FireCells, GridSpec.FireCellSizeM, 1f / grid.ExtentM, 0f));
            Apply();
        }

        static readonly int ArrivalTimeId = Shader.PropertyToID("_FireCA_ArrivalTime");
        static readonly int SpreadAId = Shader.PropertyToID("_FireCA_SpreadA");
        static readonly int SpreadBId = Shader.PropertyToID("_FireCA_SpreadB");
        static readonly int FireParamsId = Shader.PropertyToID("_FireCA_FireParams");

        /// <summary>A smoldering cell emits a dim red that fades to zero over this many seconds.</summary>
        public float EmberFadeS = 240f;

        /// <summary>
        /// Binds the fire textures and the Sim Clock to the terrain shader. The read copy of the Arrival Time changes
        /// after each Fire step, so the app calls this in each frame.
        /// </summary>
        public void BindFire(TextureRegistry registry, float simClockSeconds, float sourceHoldS)
        {
            Shader.SetGlobalTexture(ArrivalTimeId, registry.Get(SimTextures.ArrivalTime));
            Shader.SetGlobalTexture(SpreadAId, registry.Get(SimTextures.SpreadA));
            Shader.SetGlobalTexture(SpreadBId, registry.Get(SimTextures.SpreadB));
            Shader.SetGlobalVector(FireParamsId, new Vector4(simClockSeconds, sourceHoldS, EmberFadeS, 0f));
        }

        static readonly int ElevationId = Shader.PropertyToID("_FireCA_Elevation");
        static readonly int Moisture1hId = Shader.PropertyToID("_FireCA_Moisture1h");
        static readonly int InsolationId = Shader.PropertyToID("_FireCA_Insolation");
        static readonly int WeatherAId = Shader.PropertyToID("_FireCA_WeatherA");
        static readonly int WeatherBId = Shader.PropertyToID("_FireCA_WeatherB");

        /// <summary>The lowest and the highest value of the Air temperature Overlay, deg C.</summary>
        public static Vector2 AirTemperatureRange(WeatherModel weather) =>
            new Vector2(weather.TempMinC - 10f, weather.TempMaxC + 5f);

        /// <summary>Binds the weather textures and the weather inputs to the terrain shader.</summary>
        public void BindWeather(TextureRegistry registry, WeatherModel weather)
        {
            Shader.SetGlobalTexture(ElevationId, registry.Get(SimTextures.Elevation));
            Shader.SetGlobalTexture(Moisture1hId, registry.Get(SimTextures.Moisture1h));
            Shader.SetGlobalTexture(InsolationId, registry.Get(SimTextures.Insolation));
            Shader.SetGlobalVector(WeatherAId, weather.WeatherA);
            Shader.SetGlobalVector(WeatherBId, weather.WeatherB);
        }

        static readonly int SurfaceWindId = Shader.PropertyToID("_FireCA_SurfaceWind");
        static readonly int VelocityId = Shader.PropertyToID("_FireCA_Velocity");
        static readonly int SmokeId = Shader.PropertyToID("_FireCA_Smoke");
        static readonly int TurbulenceId = Shader.PropertyToID("_FireCA_Turbulence");
        static readonly int SolidMaskId = Shader.PropertyToID("_FireCA_SolidMask");
        static readonly int GroundHeightId = Shader.PropertyToID("_FireCA_GroundHeight");
        static readonly int WindGridId = Shader.PropertyToID("_FireCA_WindGrid");
        static readonly int HeatFluxCoarseId = Shader.PropertyToID("_FireCA_HeatFluxCoarse");
        static readonly int WindBottomYId = Shader.PropertyToID("_FireCA_WindBottomY");

        /// <summary>
        /// Binds the wind textures to the terrain shader and the slice plane shader. The read copies of Velocity and
        /// Smoke change after each Wind step, so the app calls this in each frame.
        /// </summary>
        static readonly int NestVelocityId = Shader.PropertyToID("_FireCA_NestVelocity");
        static readonly int NestSmokeId = Shader.PropertyToID("_FireCA_NestSmoke");
        static readonly int NestTurbulenceId = Shader.PropertyToID("_FireCA_NestTurbulence");
        static readonly int NestGridId = Shader.PropertyToID("_FireCA_NestGrid");
        static readonly int NestOriginId = Shader.PropertyToID("_FireCA_NestOrigin");

        /// <summary>
        /// The Wind Nest of the display (docs/decisions.md, decision D32). _FireCA_NestGrid: cells on X, Y, Z, and the
        /// cell size. _FireCA_NestOrigin: the world position of the corner of cell (0, 0, 0), and the width of the
        /// blend zone at the faces. A cell size of 0 means that no Wind Nest is placed.
        /// </summary>
        void BindNest(TextureRegistry registry, WindNest nest)
        {
            bool active = nest != null && nest.Active;
            Shader.SetGlobalTexture(NestVelocityId, active ? nest.Velocity : registry.Get(SimTextures.Velocity));
            Shader.SetGlobalTexture(NestSmokeId, active ? nest.Smoke : registry.Get(SimTextures.Smoke));
            Shader.SetGlobalTexture(NestTurbulenceId, active ? nest.Turbulence : registry.Get(SimTextures.Turbulence));
            if (active)
            {
                WindDomain d = nest.Domain;
                Shader.SetGlobalVector(NestGridId, new Vector4(d.Nx, d.Ny, d.Nz, d.CellSize));
                Shader.SetGlobalVector(NestOriginId, new Vector4(d.OriginXZ.x, d.BottomY, d.OriginXZ.y, nest.RampM));
            }
            else
            {
                Shader.SetGlobalVector(NestGridId, Vector4.zero);
                Shader.SetGlobalVector(NestOriginId, Vector4.zero);
            }
        }

        public void BindWind(TextureRegistry registry, WindNest nest = null)
        {
            BindNest(registry, nest);
            GridSpec grid = registry.Grid;
            Shader.SetGlobalTexture(SurfaceWindId, registry.Get(SimTextures.SurfaceWind));
            Shader.SetGlobalTexture(VelocityId, registry.Get(SimTextures.Velocity));
            Shader.SetGlobalTexture(SmokeId, registry.Get(SimTextures.Smoke));
            Shader.SetGlobalTexture(TurbulenceId, registry.Get(SimTextures.Turbulence));
            Shader.SetGlobalTexture(SolidMaskId, registry.Get(SimTextures.SolidMask));
            Shader.SetGlobalTexture(GroundHeightId, registry.Get(SimTextures.GroundHeight));
            Shader.SetGlobalTexture(HeatFluxCoarseId, registry.Get(SimTextures.HeatFluxCoarse));
            Shader.SetGlobalVector(WindGridId, new Vector4(grid.WindCellsX, GridSpec.WindCellsY, grid.WindCellsZ, GridSpec.WindCellSizeM));
            Shader.SetGlobalFloat(WindBottomYId, grid.WindBottomY);
        }

        public void Unbind() => Shader.SetGlobalVector(GridParamsId, Vector4.zero);

        public void Select(OverlayInfo overlay)
        {
            Selected = overlay != null && overlay.Available ? overlay : null;
            Apply();
        }

        public void SetOpacity(float opacity)
        {
            Opacity = Mathf.Clamp01(opacity);
            Apply();
        }

        void Apply()
        {
            Shader.SetGlobalVector(OverlayParamsId, new Vector4(Selected != null ? Selected.Id : 0, Opacity, 0f, 0f));
            Shader.SetGlobalVector(TerrainParamsId, new Vector4(FuelColorSmoothingM, GroundImage != null ? GroundImageOpacity : 0f, 0f, 0f));
        }

        public static OverlayInfo Find(string name)
        {
            foreach (var o in Catalog) if (o.Name == name) return o;
            return null;
        }

        // ---- color tables ----

        /// <summary>The viridis color map (polynomial fit), t from 0 to 1, sRGB.</summary>
        public static Color Viridis(float t)
        {
            t = Mathf.Clamp01(t);
            Vector3 c0 = new Vector3(0.2777273272234177f, 0.005407344544966578f, 0.3340998053353061f);
            Vector3 c1 = new Vector3(0.1050930431085774f, 1.404613529898575f, 1.384590162594685f);
            Vector3 c2 = new Vector3(-0.3308618287255563f, 0.214847559468213f, 0.09509516302823659f);
            Vector3 c3 = new Vector3(-4.634230498983486f, -5.799100973351585f, -19.33244095627987f);
            Vector3 c4 = new Vector3(6.228269936347081f, 14.17993336680509f, 56.69055260068105f);
            Vector3 c5 = new Vector3(4.776384997670288f, -13.74514537774601f, -65.35303263337234f);
            Vector3 c6 = new Vector3(-5.435455855934631f, 4.645852612178535f, 26.3124352495832f);
            Vector3 c = c0 + t * (c1 + t * (c2 + t * (c3 + t * (c4 + t * (c5 + t * c6)))));
            return new Color(Mathf.Clamp01(c.x), Mathf.Clamp01(c.y), Mathf.Clamp01(c.z), 1f);
        }

        static readonly Color32[] CategoricalModels =
        {
            Hex(0xFFE066), Hex(0xA8D65C), Hex(0xF5A623), Hex(0xD0021B), Hex(0xF26BA0), Hex(0x9B59B6), Hex(0x5B3A9E),
            Hex(0x1B7F3B), Hex(0x3CC4B0), Hex(0x0B5A44), Hex(0x4A90E2), Hex(0x2C5AA0), Hex(0x7B4A12),
        };

        static readonly Color32[] NaturalModels =
        {
            Hex(0xC2B36B), Hex(0xA3A860), Hex(0xC9A94E), Hex(0x5E6B3A), Hex(0x7C8448), Hex(0x8C7A52), Hex(0x6F7F45),
            Hex(0x3F5A36), Hex(0x5F7A3C), Hex(0x2F4A2E), Hex(0x8A7B5C), Hex(0x7A6A4E), Hex(0x695A42),
        };

        public static readonly Color32 CategoricalNotBurnable = Hex(0x808080);

        static Color32 Hex(int rgb) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);

        /// <summary>The color of a fuel code in the Fuel model Overlay.</summary>
        public static Color32 CategoricalColor(int code) =>
            FuelCodes.IsBurnable(code) ? CategoricalModels[code - 1] : CategoricalNotBurnable;

        /// <summary>The natural color of a fuel code.</summary>
        public static Color32 NaturalColor(int code)
        {
            if (FuelCodes.IsBurnable(code)) return NaturalModels[code - 1];
            switch (code)
            {
                case FuelCodes.Urban: return Hex(0x8A8A8A);
                case FuelCodes.SnowOrIce: return Hex(0xF0F4F8);
                case FuelCodes.Agriculture: return Hex(0xB7C25E);
                case FuelCodes.Water: return Hex(0x2F5F8A);
                case FuelCodes.Barren: return Hex(0x8E857A);
                default: return Hex(0x555555);
            }
        }

        static Texture2D BuildColorMap()
        {
            var tex = new Texture2D(256, 1, TextureFormat.RGBA32, false, false)
            {
                name = "FireCA Color Map",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var pixels = new Color32[256];
            for (int i = 0; i < 256; i++) pixels[i] = Viridis(i / 255f);
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        static Texture2D BuildFuelPalette()
        {
            var tex = new Texture2D(256, 2, TextureFormat.RGBA32, false, false)
            {
                name = "FireCA Fuel Palette",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var pixels = new Color32[512];
            for (int code = 0; code < 256; code++)
            {
                pixels[code] = NaturalColor(code);
                pixels[256 + code] = CategoricalColor(code);
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        public void Dispose()
        {
            Unbind();
            Destroy(ColorMap);
            Destroy(FuelPalette);
        }

        static void Destroy(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
