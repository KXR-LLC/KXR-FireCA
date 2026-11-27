using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace FireCA.Tests
{
    /// <summary>
    /// EditMode tests of milestone M8: the placement rules and the Plant Models of the Vegetation
    /// (docs/05-milestones.md and docs/decisions.md, decision D33).
    /// </summary>
    public sealed class VegetationRulesTests
    {
        const int Cells = 512;
        // One band of 32 cells for each fuel code of the list.
        static readonly byte[] Codes = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, FuelCodes.Urban, FuelCodes.Water, FuelCodes.Barren };

        static byte[] BandedFuel()
        {
            var fuel = new byte[Cells * Cells];
            for (int z = 0; z < Cells; z++)
                for (int x = 0; x < Cells; x++)
                    fuel[z * Cells + x] = Codes[x / 32];
            return fuel;
        }

        static float[] SlopedElevation()
        {
            var elevation = new float[Cells * Cells];
            for (int z = 0; z < Cells; z++)
                for (int x = 0; x < Cells; x++)
                    elevation[z * Cells + x] = 1000f + 0.5f * z;
            return elevation;
        }

        static byte CodeAt(PlantInstance plant) => Codes[(int)(plant.Position.x / GridSpec.FireCellSizeM) / 32];

        [Test]
        public void Criterion1_PlantsStandOnlyOnFuelCodesWithPlantModels()
        {
            List<PlantInstance> plants = VegetationRules.Place(BandedFuel(), SlopedElevation(), Cells);
            Assert.Greater(plants.Count, 1000);
            var perCode = new Dictionary<byte, int>();
            foreach (PlantInstance plant in plants)
            {
                byte code = CodeAt(plant);
                Assert.IsNotNull(VegetationRules.For(code), $"A Plant stands on fuel code {code}.");
                perCode[code] = perCode.TryGetValue(code, out int count) ? count + 1 : 1;
            }
            // Grass models and codes that do not burn have no Plants. Every other fuel model has Plants.
            foreach (byte code in Codes)
            {
                bool expected = code >= 2 && code <= 13 && code != 3;
                Assert.AreEqual(expected, perCode.ContainsKey(code), $"fuel code {code}");
            }
            Debug.Log($"[FireCA test] Vegetation: {plants.Count} Plants on a 512 grid with 16 fuel bands.");
        }

        [Test]
        public void Criterion2_EachFuelCodeHasTwoOrThreePlantModels()
        {
            List<PlantInstance> plants = VegetationRules.Place(BandedFuel(), SlopedElevation(), Cells);
            for (int code = 1; code <= FuelCodes.FuelModelCount; code++)
            {
                FuelVegetation rule = VegetationRules.For(code);
                if (rule == null) continue;
                Assert.That(rule.Plants.Length, Is.InRange(2, 3), $"fuel code {code}");
                var seen = new HashSet<int>();
                foreach (PlantInstance plant in plants)
                    if (CodeAt(plant) == code) seen.Add((int)plant.Model);
                Assert.AreEqual(rule.Plants.Length, seen.Count, $"Plant Models that the placement used on fuel code {code}");
            }
        }

        [Test]
        public void Criterion3_SizeAndRotationVaryInsideTheRanges()
        {
            List<PlantInstance> plants = VegetationRules.Place(BandedFuel(), SlopedElevation(), Cells);
            double sin = 0.0, cos = 0.0;
            float minWidth = float.MaxValue, maxWidth = float.MinValue;
            foreach (PlantInstance plant in plants)
            {
                FuelVegetation rule = VegetationRules.For(CodeAt(plant));
                bool inRange = false;
                foreach (PlantRule r in rule.Plants)
                    if ((int)r.Model == (int)plant.Model && plant.HeightM >= r.MinHeightM && plant.HeightM <= r.MaxHeightM) inRange = true;
                Assert.IsTrue(inRange, $"height {plant.HeightM} m of model {plant.Model}");
                Assert.That(plant.WidthScale, Is.InRange(VegetationRules.MinWidthScale, VegetationRules.MaxWidthScale));
                Assert.That(plant.Yaw, Is.InRange(0f, 2f * Mathf.PI));
                Assert.That(plant.Seed, Is.InRange(0f, 1f));
                sin += Mathf.Sin(plant.Yaw);
                cos += Mathf.Cos(plant.Yaw);
                minWidth = Mathf.Min(minWidth, plant.WidthScale);
                maxWidth = Mathf.Max(maxWidth, plant.WidthScale);
            }
            // The rotations have no preferred direction, and the widths use most of the range.
            Assert.Less(System.Math.Sqrt(sin * sin + cos * cos) / plants.Count, 0.05);
            Assert.Less(minWidth, VegetationRules.MinWidthScale + 0.05f);
            Assert.Greater(maxWidth, VegetationRules.MaxWidthScale - 0.05f);
        }

        [Test]
        public void Criterion4_PlacementIsTheSameOnEveryRun_AndPlantsStandOnTheGround()
        {
            byte[] fuel = BandedFuel();
            float[] elevation = SlopedElevation();
            List<PlantInstance> first = VegetationRules.Place(fuel, elevation, Cells);
            List<PlantInstance> second = VegetationRules.Place(fuel, elevation, Cells);
            Assert.AreEqual(first.Count, second.Count);
            for (int i = 0; i < first.Count; i += 37)
            {
                Assert.AreEqual(first[i].Position, second[i].Position);
                Assert.AreEqual(first[i].Model, second[i].Model);
                // The elevation of the test rises 0.5 m per cell on Z, from the center of cell 0.
                float expected = 1000f + 0.5f * Mathf.Clamp(first[i].Position.z / GridSpec.FireCellSizeM - 0.5f, 0f, Cells - 1f);
                Assert.AreEqual(expected, first[i].Position.y, 0.05f);
            }
        }

        [Test]
        public void Criterion5_EveryPlantModelHasThreeLevelsOfDetail()
        {
            var data = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(PlantModels.DataPath);
            Assert.IsNotNull(data, $"{PlantModels.DataPath} is missing. Run tools/blender/plant_models.py.");
            PlantModels.Set set = PlantModels.Load(data.bytes);
            for (int m = 0; m < VegetationRules.ModelCount; m++)
            {
                int full = set.Triangles[m, 0], mid = set.Triangles[m, 1], far = set.Triangles[m, 2];
                Assert.Greater(far, 0, $"model {(PlantModelId)m}");
                Assert.Greater(mid, far, $"model {(PlantModelId)m}");
                Assert.Greater(full, mid, $"model {(PlantModelId)m}");
                Assert.LessOrEqual(far, 30, $"model {(PlantModelId)m}");
                Debug.Log($"[FireCA test] Plant Model {(PlantModelId)m}: {full}, {mid}, {far} triangles.");
            }
            Assert.AreEqual(set.Vertices.Length, PlantModels.VertexFloats * VegetationRules.ModelCount
                * (set.VerticesPerModel[0] + set.VerticesPerModel[1] + set.VerticesPerModel[2]));
        }

        [Test]
        public void Criterion6_PlantStateFollowsTheAgeOfTheFire()
        {
            Assert.AreEqual(PlantState.Normal, VegetationRules.StateOf(PlantModelId.ConiferA, -1f, 20f));
            Assert.AreEqual(PlantState.Burning, VegetationRules.StateOf(PlantModelId.ConiferA, 0f, 20f));
            Assert.AreEqual(PlantState.Burning, VegetationRules.StateOf(PlantModelId.ConiferA, 149f, 20f));
            Assert.AreEqual(PlantState.Burnt, VegetationRules.StateOf(PlantModelId.ConiferA, 151f, 20f));
            // A residence time above the time of the Plant Model keeps the Plant burning.
            Assert.AreEqual(PlantState.Burning, VegetationRules.StateOf(PlantModelId.ShrubC, 100f, 200f));
            Assert.AreEqual(PlantState.Burnt, VegetationRules.StateOf(PlantModelId.ShrubC, 100f, 20f));
        }
    }
}
