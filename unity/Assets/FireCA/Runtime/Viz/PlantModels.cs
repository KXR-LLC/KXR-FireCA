using System;
using UnityEngine;

namespace FireCA
{
    /// <summary>
    /// Reads the Plant Models of the Vegetation from the file that the Blender script writes
    /// (tools/blender/plant_models.py, docs/decisions.md, decision D34). A Plant Model has a height of 1 and stands
    /// on the origin. Each Plant Model has three levels of detail. Every Plant Model has the same vertex count at
    /// one level of detail, so one draw call renders all Plant Models of that level.
    /// </summary>
    public static class PlantModels
    {
        public const int LodCount = 3;
        /// <summary>Floats per vertex: position (3), part, normal (3), shade, Leaf Atlas uv (2), flutter, leaf seed.</summary>
        public const int VertexFloats = 12;
        public const int VertexStride = VertexFloats * sizeof(float);
        public const string DataPath = "Assets/FireCA/Models/PlantModels.bytes";
        public const string LeafAtlasPath = "Assets/FireCA/Models/LeafAtlas.png";

        /// <summary>The part of a Plant Model that a vertex belongs to.</summary>
        public const float PartBark = 0f, PartFoliage = 1f, PartDeadWood = 2f;

        const int FileVersion = 1;
        const int HeaderBytes = 20;

        /// <summary>The vertex data of all Plant Models at all levels of detail.</summary>
        public sealed class Set
        {
            /// <summary>Vertices of level 0 for model 0, model 1, and so on, then level 1, then level 2.</summary>
            public float[] Vertices;
            /// <summary>Vertices per Plant Model at each level of detail.</summary>
            public int[] VerticesPerModel = new int[LodCount];
            /// <summary>The first vertex of each level of detail in Vertices.</summary>
            public int[] LodStart = new int[LodCount];
            /// <summary>Triangles of each Plant Model at each level of detail, before padding. Index: model, level.</summary>
            public int[,] Triangles = new int[VegetationRules.ModelCount, LodCount];
            public int VertexCount => Vertices.Length / VertexFloats;
        }

        /// <summary>
        /// Reads all Plant Models at all levels of detail and pads them to one vertex count per level.
        /// The file holds the text "FCPM", then the version, the model count, the level count, and the floats per
        /// vertex as 32-bit integers. For each level and each model, the file then holds the vertex count and the
        /// vertices. The byte order is little-endian.
        /// </summary>
        public static Set Load(byte[] data)
        {
            if (data == null || data.Length < HeaderBytes || data[0] != (byte)'F' || data[1] != (byte)'C' || data[2] != (byte)'P' || data[3] != (byte)'M')
                throw new ArgumentException("The data is not a Plant Model file.");
            int version = BitConverter.ToInt32(data, 4), modelCount = BitConverter.ToInt32(data, 8);
            int lodCount = BitConverter.ToInt32(data, 12), vertexFloats = BitConverter.ToInt32(data, 16);
            if (version != FileVersion || modelCount != VegetationRules.ModelCount || lodCount != LodCount || vertexFloats != VertexFloats)
                throw new ArgumentException($"The Plant Model file has version {version}, {modelCount} models, {lodCount} levels, " +
                    $"and {vertexFloats} floats per vertex. Run tools/blender/plant_models.py again.");

            var set = new Set();
            var offsets = new int[modelCount, lodCount];
            var counts = new int[modelCount, lodCount];
            int at = HeaderBytes;
            for (int lod = 0; lod < lodCount; lod++)
            {
                int most = 0;
                for (int m = 0; m < modelCount; m++)
                {
                    int vertices = BitConverter.ToInt32(data, at);
                    at += sizeof(int);
                    offsets[m, lod] = at;
                    counts[m, lod] = vertices;
                    set.Triangles[m, lod] = vertices / 3;
                    most = Mathf.Max(most, vertices);
                    at += vertices * VertexStride;
                    if (at > data.Length) throw new ArgumentException("The Plant Model file is too short.");
                }
                set.VerticesPerModel[lod] = most;
            }
            int total = 0;
            for (int lod = 0; lod < lodCount; lod++)
            {
                set.LodStart[lod] = total;
                total += set.VerticesPerModel[lod] * modelCount;
            }
            // The padding is zero vertices. A triangle of three equal points draws nothing.
            set.Vertices = new float[total * VertexFloats];
            for (int lod = 0; lod < lodCount; lod++)
                for (int m = 0; m < modelCount; m++)
                    Buffer.BlockCopy(data, offsets[m, lod], set.Vertices,
                        (set.LodStart[lod] + m * set.VerticesPerModel[lod]) * VertexStride, counts[m, lod] * VertexStride);
            return set;
        }
    }
}
