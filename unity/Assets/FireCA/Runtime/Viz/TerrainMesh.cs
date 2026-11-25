using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FireCA
{
    /// <summary>
    /// The Terrain Mesh. Built on the CPU from the CPU copy of Elevation, with a vertex spacing of 8 m,
    /// in tiles of 128 x 128 quads. The mesh has no computed vertex normals. The terrain shader reads the Normal texture.
    /// </summary>
    public sealed class TerrainMesh
    {
        public const float VertexSpacingM = 8f;
        public const int TileQuads = 128;

        readonly List<Mesh> _meshes = new List<Mesh>();
        public GameObject Root { get; private set; }
        public int TileCount => _meshes.Count;
        public long TriangleCount { get; private set; }

        public void Build(ScenarioFiles scenario, Material material, Transform parent)
        {
            Clear();
            GridSpec grid = scenario.Grid;
            int n = grid.FireCells;
            int step = Mathf.RoundToInt(VertexSpacingM / GridSpec.FireCellSizeM); // Fire Grid cells per quad
            int quads = n / step;                                                  // quads per side
            int tiles = Mathf.Max(1, quads / TileQuads);
            int tileQuads = quads / tiles;
            int tileVerts = tileQuads + 1;
            float[] elevation = scenario.Elevation;

            Root = new GameObject("Terrain Mesh");
            Root.transform.SetParent(parent, false);

            // One index buffer serves every tile.
            var indices = new ushort[tileQuads * tileQuads * 6];
            int t = 0;
            for (int z = 0; z < tileQuads; z++)
            {
                for (int x = 0; x < tileQuads; x++)
                {
                    int v00 = z * tileVerts + x, v10 = v00 + 1, v01 = v00 + tileVerts, v11 = v01 + 1;
                    indices[t++] = (ushort)v00; indices[t++] = (ushort)v01; indices[t++] = (ushort)v11;
                    indices[t++] = (ushort)v00; indices[t++] = (ushort)v11; indices[t++] = (ushort)v10;
                }
            }

            var vertices = new Vector3[tileVerts * tileVerts];
            var normals = new Vector3[vertices.Length];
            var tangents = new Vector4[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                normals[i] = Vector3.up;                 // constant, not computed from the terrain
                tangents[i] = new Vector4(1f, 0f, 0f, 1f);
            }

            for (int tz = 0; tz < tiles; tz++)
            {
                for (int tx = 0; tx < tiles; tx++)
                {
                    float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
                    for (int z = 0; z < tileVerts; z++)
                    {
                        int cz = (tz * tileQuads + z) * step;      // Fire Grid corner index
                        for (int x = 0; x < tileVerts; x++)
                        {
                            int cx = (tx * tileQuads + x) * step;
                            float h = CornerHeight(elevation, n, cx, cz);
                            if (h < minY) minY = h;
                            if (h > maxY) maxY = h;
                            vertices[z * tileVerts + x] = new Vector3(cx * GridSpec.FireCellSizeM, h, cz * GridSpec.FireCellSizeM);
                        }
                    }

                    var mesh = new Mesh { name = $"Terrain Tile {tx},{tz}", hideFlags = HideFlags.HideAndDontSave };
                    mesh.indexFormat = IndexFormat.UInt16;
                    mesh.SetVertices(vertices);
                    mesh.SetNormals(normals);
                    mesh.SetTangents(tangents);
                    mesh.SetIndices(indices, MeshTopology.Triangles, 0, false);
                    float x0 = tx * tileQuads * VertexSpacingM, z0 = tz * tileQuads * VertexSpacingM;
                    float size = tileQuads * VertexSpacingM;
                    var bounds = new Bounds();
                    bounds.SetMinMax(new Vector3(x0, minY, z0), new Vector3(x0 + size, maxY, z0 + size));
                    mesh.bounds = bounds;
                    mesh.UploadMeshData(true);
                    _meshes.Add(mesh);
                    TriangleCount += indices.Length / 3;

                    var go = new GameObject(mesh.name);
                    go.transform.SetParent(Root.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderer.shadowCastingMode = ShadowCastingMode.On;
                    renderer.receiveShadows = true;
                    renderer.lightProbeUsage = LightProbeUsage.Off;
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                }
            }
        }

        // The height at the corner shared by four Fire Grid cells: the mean of the cells that exist.
        static float CornerHeight(float[] elevation, int n, int cx, int cz)
        {
            int x0 = Mathf.Max(cx - 1, 0), x1 = Mathf.Min(cx, n - 1);
            int z0 = Mathf.Max(cz - 1, 0), z1 = Mathf.Min(cz, n - 1);
            return 0.25f * (elevation[z0 * n + x0] + elevation[z0 * n + x1] + elevation[z1 * n + x0] + elevation[z1 * n + x1]);
        }

        public void Clear()
        {
            foreach (var mesh in _meshes)
            {
                if (mesh == null) continue;
                if (Application.isPlaying) Object.Destroy(mesh); else Object.DestroyImmediate(mesh);
            }
            _meshes.Clear();
            TriangleCount = 0;
            if (Root != null)
            {
                if (Application.isPlaying) Object.Destroy(Root); else Object.DestroyImmediate(Root);
                Root = null;
            }
        }
    }
}
