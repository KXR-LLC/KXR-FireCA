using System;
using System.Collections.Generic;
using UnityEngine;

namespace FireCA
{
    /// <summary>The Plant Models of the Vegetation (docs/decisions.md, decision D33).</summary>
    public enum PlantModelId
    {
        ConiferA = 0,   // fir: a narrow crown to the ground
        ConiferB = 1,   // pine: a bare trunk and a crown of clumps
        ConiferC = 2,   // young conifer: short and wide
        HardwoodA = 3,  // round crown
        HardwoodB = 4,  // tall oval crown
        ShrubA = 5,     // tall dome shrub
        ShrubB = 6,     // low spreading shrub
        ShrubC = 7,     // small tufted shrub
        LogA = 8,       // crossed logs
        LogB = 9,       // log pile with a stump
        Snag = 10,      // standing dead trunk
    }

    /// <summary>The display state of a Plant.</summary>
    public enum PlantState { Normal = 0, Burning = 1, Burnt = 2 }

    /// <summary>One Plant on the GPU. The layout matches the struct Plant of Vegetation.compute.</summary>
    public struct PlantInstance
    {
        public Vector3 Position;    // world position of the base, m
        public float HeightM;       // height of the Plant, m
        public float Yaw;           // rotation about the vertical axis, rad
        public float WidthScale;    // multiplier on the width of the Plant Model
        public float Model;         // PlantModelId
        public float Seed;          // 0 to 1, for color variation and thinning

        public const int Stride = 8 * sizeof(float);
    }

    /// <summary>One Plant Model of a fuel code, with its share and its height range.</summary>
    public readonly struct PlantRule
    {
        public readonly PlantModelId Model;
        public readonly float Weight;
        public readonly float MinHeightM, MaxHeightM;

        public PlantRule(PlantModelId model, float weight, float minHeightM, float maxHeightM)
        {
            Model = model;
            Weight = weight;
            MinHeightM = minHeightM;
            MaxHeightM = maxHeightM;
        }
    }

    /// <summary>The Vegetation of one fuel code: the share of the sites that hold a Plant, and the Plant Models.</summary>
    public sealed class FuelVegetation
    {
        public readonly float Fill;
        public readonly PlantRule[] Plants;

        public FuelVegetation(float fill, params PlantRule[] plants)
        {
            Fill = fill;
            Plants = plants;
        }
    }

    /// <summary>
    /// The rules that place the Vegetation from the Fuel layer (docs/decisions.md, decision D33). The Vegetation is
    /// a display of the fuel. The Spread Solver does not read it.
    /// </summary>
    public static class VegetationRules
    {
        public const int ModelCount = 11;
        /// <summary>The spacing of the sites, m. One site holds at most one Plant.</summary>
        public const float SitePitchM = 4f;
        public const float MinWidthScale = 0.8f;
        public const float MaxWidthScale = 1.25f;
        /// <summary>The size of the clumps and the gaps in the Vegetation, m.</summary>
        public const float ClumpSizeM = 55f;

        /// <summary>
        /// The time that a Plant Model shows flames, s. The time of a Plant is the larger of this value and the
        /// residence time of its cell. The array Vegetation_BurnSeconds of Vegetation.hlsl holds the same values.
        /// </summary>
        public static readonly float[] BurnSeconds = { 150f, 150f, 90f, 150f, 150f, 50f, 40f, 30f, 420f, 480f, 300f };

        static readonly FuelVegetation[] Table = BuildTable();

        static PlantRule R(PlantModelId model, float weight, float min, float max) => new PlantRule(model, weight, min, max);

        static FuelVegetation[] BuildTable()
        {
            var t = new FuelVegetation[FuelCodes.FuelModelCount + 1];
            // 1 and 3 are grass models. Small plants and grasses have no Plant Models.
            t[2] = new FuelVegetation(0.07f, R(PlantModelId.ConiferB, 0.5f, 10f, 20f), R(PlantModelId.HardwoodA, 0.3f, 6f, 11f), R(PlantModelId.ConiferC, 0.2f, 3f, 7f));
            t[4] = new FuelVegetation(0.60f, R(PlantModelId.ShrubA, 0.5f, 1.8f, 3.2f), R(PlantModelId.ShrubB, 0.3f, 1.2f, 2.2f), R(PlantModelId.ShrubC, 0.2f, 0.8f, 1.5f));
            t[5] = new FuelVegetation(0.45f, R(PlantModelId.ShrubB, 0.5f, 0.7f, 1.4f), R(PlantModelId.ShrubC, 0.35f, 0.5f, 1.0f), R(PlantModelId.ShrubA, 0.15f, 1.0f, 1.8f));
            t[6] = new FuelVegetation(0.45f, R(PlantModelId.ShrubA, 0.5f, 0.9f, 1.7f), R(PlantModelId.ShrubB, 0.5f, 0.7f, 1.4f));
            t[7] = new FuelVegetation(0.45f, R(PlantModelId.ShrubA, 0.5f, 1.0f, 1.9f), R(PlantModelId.ShrubB, 0.4f, 0.8f, 1.5f), R(PlantModelId.ConiferB, 0.1f, 9f, 16f));
            t[8] = new FuelVegetation(0.36f, R(PlantModelId.ConiferA, 0.5f, 14f, 26f), R(PlantModelId.ConiferB, 0.3f, 12f, 22f), R(PlantModelId.ConiferC, 0.2f, 4f, 9f));
            t[9] = new FuelVegetation(0.30f, R(PlantModelId.HardwoodA, 0.5f, 9f, 16f), R(PlantModelId.HardwoodB, 0.35f, 12f, 20f), R(PlantModelId.ConiferB, 0.15f, 12f, 20f));
            t[10] = new FuelVegetation(0.45f, R(PlantModelId.ConiferA, 0.45f, 16f, 30f), R(PlantModelId.ConiferC, 0.25f, 3f, 9f), R(PlantModelId.ShrubB, 0.30f, 0.8f, 1.6f));
            t[11] = new FuelVegetation(0.25f, R(PlantModelId.LogA, 0.5f, 0.4f, 0.8f), R(PlantModelId.ShrubC, 0.3f, 0.5f, 1.0f), R(PlantModelId.Snag, 0.2f, 5f, 12f));
            t[12] = new FuelVegetation(0.35f, R(PlantModelId.LogA, 0.45f, 0.5f, 0.9f), R(PlantModelId.LogB, 0.4f, 0.6f, 1.1f), R(PlantModelId.Snag, 0.15f, 5f, 12f));
            t[13] = new FuelVegetation(0.50f, R(PlantModelId.LogA, 0.4f, 0.6f, 1.0f), R(PlantModelId.LogB, 0.5f, 0.7f, 1.3f), R(PlantModelId.Snag, 0.1f, 6f, 14f));
            return t;
        }

        /// <summary>The Vegetation of a fuel code, or null when the fuel code has no Plant Models.</summary>
        public static FuelVegetation For(int fuelCode) => fuelCode >= 0 && fuelCode < Table.Length ? Table[fuelCode] : null;

        /// <summary>The display state of a Plant from the age of the fire in its cell.</summary>
        /// <param name="ageS">Sim Clock minus the Arrival Time of the cell, s. Negative when the cell is not ignited.</param>
        /// <param name="residenceS">The residence time of the cell, s.</param>
        public static PlantState StateOf(PlantModelId model, float ageS, float residenceS)
        {
            if (ageS < 0f) return PlantState.Normal;
            return ageS < Mathf.Max(residenceS, BurnSeconds[(int)model]) ? PlantState.Burning : PlantState.Burnt;
        }

        // PCG hash. The placement must give the same Plants on every computer.
        static uint Pcg(uint v)
        {
            uint state = v * 747796405u + 2891336453u;
            uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
            return (word >> 22) ^ word;
        }

        static float Hash01(uint x, uint y, uint k) => (Pcg(x + Pcg(y + Pcg(k))) >> 8) * (1f / 16777216f);

        static float ClumpNoise(float x, float z)
        {
            float fx = x / ClumpSizeM, fz = z / ClumpSizeM;
            int ix = Mathf.FloorToInt(fx), iz = Mathf.FloorToInt(fz);
            float tx = fx - ix, tz = fz - iz;
            tx = tx * tx * (3f - 2f * tx);
            tz = tz * tz * (3f - 2f * tz);
            float a = Hash01((uint)ix, (uint)iz, 91u), b = Hash01((uint)ix + 1u, (uint)iz, 91u);
            float c = Hash01((uint)ix, (uint)iz + 1u, 91u), d = Hash01((uint)ix + 1u, (uint)iz + 1u, 91u);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
        }

        static float Elevation(float[] elevation, int n, float xM, float zM)
        {
            float fx = Mathf.Clamp(xM / GridSpec.FireCellSizeM - 0.5f, 0f, n - 1.001f);
            float fz = Mathf.Clamp(zM / GridSpec.FireCellSizeM - 0.5f, 0f, n - 1.001f);
            int ix = (int)fx, iz = (int)fz;
            float tx = fx - ix, tz = fz - iz;
            float a = elevation[iz * n + ix], b = elevation[iz * n + ix + 1];
            float c = elevation[(iz + 1) * n + ix], d = elevation[(iz + 1) * n + ix + 1];
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
        }

        /// <summary>
        /// Places the Plants of a Scenario. Each site of 4 m x 4 m holds at most one Plant, at a random position in
        /// the site. The fuel code of the cell under the position gives the Plant Models. The result depends on the
        /// Fuel layer and the Elevation layer only.
        /// </summary>
        public static List<PlantInstance> Place(byte[] fuel, float[] elevation, int fireCells)
        {
            int cellsPerSite = Mathf.RoundToInt(SitePitchM / GridSpec.FireCellSizeM);
            int sites = fireCells / cellsPerSite;
            var plants = new List<PlantInstance>(sites * sites / 4);
            float extent = fireCells * GridSpec.FireCellSizeM;
            for (int j = 0; j < sites; j++)
            {
                for (int i = 0; i < sites; i++)
                {
                    uint ui = (uint)i, uj = (uint)j;
                    float x = Mathf.Min((i + Hash01(ui, uj, 1u)) * SitePitchM, extent - 0.01f);
                    float z = Mathf.Min((j + Hash01(ui, uj, 2u)) * SitePitchM, extent - 0.01f);
                    int cx = (int)(x / GridSpec.FireCellSizeM), cz = (int)(z / GridSpec.FireCellSizeM);
                    FuelVegetation rule = For(fuel[cz * fireCells + cx]);
                    if (rule == null) continue;
                    float fill = rule.Fill * (0.45f + 1.1f * ClumpNoise(x, z));
                    if (Hash01(ui, uj, 3u) >= fill) continue;

                    float pick = Hash01(ui, uj, 4u);
                    PlantRule plant = rule.Plants[rule.Plants.Length - 1];
                    float sum = 0f;
                    for (int k = 0; k < rule.Plants.Length; k++)
                    {
                        sum += rule.Plants[k].Weight;
                        if (pick < sum) { plant = rule.Plants[k]; break; }
                    }
                    plants.Add(new PlantInstance
                    {
                        Position = new Vector3(x, Elevation(elevation, fireCells, x, z), z),
                        HeightM = Mathf.Lerp(plant.MinHeightM, plant.MaxHeightM, Hash01(ui, uj, 5u)),
                        Yaw = Hash01(ui, uj, 6u) * 2f * Mathf.PI,
                        WidthScale = Mathf.Lerp(MinWidthScale, MaxWidthScale, Hash01(ui, uj, 7u)),
                        Model = (float)plant.Model,
                        Seed = Hash01(ui, uj, 8u),
                    });
                }
            }
            return plants;
        }
    }
}
