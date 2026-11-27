using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FireCA
{
    /// <summary>
    /// The Vegetation: trees, shrubs, and dead wood on the Terrain Mesh, placed from the Fuel layer
    /// (docs/decisions.md, decision D33). A compute kernel picks the Plants to draw and the level of detail of each
    /// one. Three draw calls render the Plants. Two more draw the flames and the smoke of the burning Plants.
    /// The Vegetation is a display. The Spread Solver and the Wind Solver do not read it.
    /// </summary>
    public sealed class Vegetation : MonoBehaviour
    {
        public const float MinDrawDistanceM = 500f;
        public const float MaxDrawDistanceM = 12000f;
        /// <summary>Flames and smoke are drawn up to this distance from the camera, m.</summary>
        public const float EffectDistanceM = 3500f;
        /// <summary>A Plant gives off smoke for this time after its flames, in simulated seconds.</summary>
        public const float SmolderS = 900f;
        /// <summary>The smallest height on the screen of each level of detail, pixels. The last value starts the thinning.</summary>
        public static readonly Vector4 LodPixels = new Vector4(170f, 34f, 1.0f, 3.0f);
        const int FireQuads = 4;
        // Draw commands: levels 0 to 2, flames, smoke.
        const int CommandCount = 5;

        [SerializeField] ComputeShader vegetationCompute;
        [SerializeField] Material plantMaterial;
        [SerializeField] Material fireMaterial;
        [SerializeField] TextAsset plantModelData;
        [SerializeField] Texture2D leafAtlas;

        public ComputeShader VegetationCompute { get => vegetationCompute; set => vegetationCompute = value; }
        public Material PlantMaterial { get => plantMaterial; set => plantMaterial = value; }
        public Material FireMaterial { get => fireMaterial; set => fireMaterial = value; }
        /// <summary>The file of Plant Models that tools/blender/plant_models.py writes.</summary>
        public TextAsset PlantModelData { get => plantModelData; set => plantModelData = value; }
        /// <summary>The Leaf Atlas that tools/blender/plant_models.py writes.</summary>
        public Texture2D LeafAtlas { get => leafAtlas; set => leafAtlas = value; }

        public bool Visible { get; set; } = true;
        /// <summary>When false, the burning Plants have no flames and no smoke of their own.</summary>
        public bool EffectsVisible { get; set; } = true;
        /// <summary>The share of the Plants to draw, 0 to 1.</summary>
        public float Density { get; set; } = 1f;
        public float DrawDistanceM { get; set; } = 9000f;
        public bool CastShadows { get; set; } = true;
        /// <summary>Multiplier on the bend of the Plants in the wind.</summary>
        public float Sway { get; set; } = 1f;

        /// <summary>The count of Plants of the loaded Scenario.</summary>
        public int PlantCount { get; private set; }
        /// <summary>Plants drawn in a recent frame at levels of detail 0, 1, and 2.</summary>
        public int[] DrawnPerLod { get; } = new int[PlantModels.LodCount];
        /// <summary>Plants with flames or smoke in a recent frame.</summary>
        public int BurningDrawn { get; private set; }
        public int DrawnTotal => DrawnPerLod[0] + DrawnPerLod[1] + DrawnPerLod[2];
        /// <summary>GPU memory of the buffers of the Vegetation, bytes.</summary>
        public long TotalBytes { get; private set; }
        public PlantModels.Set Models { get; private set; }

        FireCAApp _app;
        ScenarioFiles _scenario;
        GraphicsBuffer _plants, _vertices, _args, _burning;
        readonly GraphicsBuffer[] _lods = new GraphicsBuffer[PlantModels.LodCount];
        readonly MaterialPropertyBlock[] _blocks = new MaterialPropertyBlock[CommandCount];
        Material _plantMaterial, _flameMaterial, _smokeMaterial;
        int _kernel;
        readonly Vector4[] _planes = new Vector4[6];
        readonly Plane[] _frustum = new Plane[6];
        bool _readbackPending;
        int _frame;

        void Start()
        {
            _app = FireCAApp.Instance;
            if (vegetationCompute != null) _kernel = vegetationCompute.FindKernel("CullKernel");
            if (plantMaterial != null) _plantMaterial = new Material(plantMaterial) { hideFlags = HideFlags.HideAndDontSave };
            if (fireMaterial != null)
            {
                _flameMaterial = new Material(fireMaterial) { hideFlags = HideFlags.HideAndDontSave };
                _flameMaterial.SetFloat("_Mode", 0f);
                _smokeMaterial = new Material(fireMaterial) { hideFlags = HideFlags.HideAndDontSave };
                _smokeMaterial.SetFloat("_Mode", 1f);
            }
            for (int i = 0; i < CommandCount; i++) _blocks[i] = new MaterialPropertyBlock();
        }

        void OnDestroy()
        {
            Release();
            if (_plantMaterial != null) Destroy(_plantMaterial);
            if (_flameMaterial != null) Destroy(_flameMaterial);
            if (_smokeMaterial != null) Destroy(_smokeMaterial);
        }

        void Release()
        {
            _plants?.Release(); _plants = null;
            _vertices?.Release(); _vertices = null;
            _args?.Release(); _args = null;
            _burning?.Release(); _burning = null;
            for (int i = 0; i < _lods.Length; i++) { _lods[i]?.Release(); _lods[i] = null; }
            PlantCount = 0;
            TotalBytes = 0;
        }

        /// <summary>Places the Plants of a Scenario and creates the buffers.</summary>
        void Rebuild(ScenarioFiles scenario)
        {
            Release();
            _scenario = scenario;
            if (scenario == null) return;

            List<PlantInstance> plants = VegetationRules.Place(scenario.Fuel, scenario.Elevation, scenario.Grid.FireCells);
            PlantCount = plants.Count;
            if (PlantCount == 0) return;
            if (Models == null) Models = PlantModels.Load(plantModelData.bytes);

            _plants = new GraphicsBuffer(GraphicsBuffer.Target.Structured, PlantCount, PlantInstance.Stride);
            _plants.SetData(plants);
            _vertices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Models.VertexCount, PlantModels.VertexStride);
            _vertices.SetData(Models.Vertices);
            for (int i = 0; i < _lods.Length; i++)
                _lods[i] = new GraphicsBuffer(GraphicsBuffer.Target.Append, PlantCount, sizeof(uint));
            _burning = new GraphicsBuffer(GraphicsBuffer.Target.Append, PlantCount, sizeof(uint));

            _args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, CommandCount, GraphicsBuffer.IndirectDrawArgs.size);
            var args = new GraphicsBuffer.IndirectDrawArgs[CommandCount];
            for (int lod = 0; lod < PlantModels.LodCount; lod++)
                args[lod].vertexCountPerInstance = (uint)Models.VerticesPerModel[lod];
            args[3].vertexCountPerInstance = FireQuads * 6;
            args[4].vertexCountPerInstance = FireQuads * 6;
            _args.SetData(args);

            TotalBytes = (long)PlantCount * PlantInstance.Stride + (long)Models.VertexCount * PlantModels.VertexStride
                + 4L * PlantCount * sizeof(uint) + CommandCount * GraphicsBuffer.IndirectDrawArgs.size;
        }

        /// <summary>Reads the counts of the last frame from the GPU. The call waits for the GPU.</summary>
        public void ReadCountsNow()
        {
            if (_args == null) return;
            var args = new GraphicsBuffer.IndirectDrawArgs[CommandCount];
            _args.GetData(args);
            for (int lod = 0; lod < PlantModels.LodCount; lod++) DrawnPerLod[lod] = (int)args[lod].instanceCount;
            BurningDrawn = (int)args[3].instanceCount;
        }

        void RequestCounts()
        {
            if (_readbackPending || _args == null) return;
            _readbackPending = true;
            AsyncGPUReadback.Request(_args, request =>
            {
                _readbackPending = false;
                if (request.hasError || _args == null) return;
                var data = request.GetData<uint>();
                if (data.Length < CommandCount * 4) return;
                for (int lod = 0; lod < PlantModels.LodCount; lod++) DrawnPerLod[lod] = (int)data[lod * 4 + 1];
                BurningDrawn = (int)data[3 * 4 + 1];
            });
        }

        void LateUpdate()
        {
            if (_app == null || vegetationCompute == null || _plantMaterial == null || plantModelData == null || leafAtlas == null) return;
            if (_app.Scenario != _scenario) Rebuild(_app.Scenario);
            Camera cam = Camera.main;
            if (!Visible || _scenario == null || PlantCount == 0 || _app.Spread == null || cam == null)
            {
                for (int i = 0; i < DrawnPerLod.Length; i++) DrawnPerLod[i] = 0;
                BurningDrawn = 0;
                return;
            }

            // 1. Pick the Plants to draw.
            GridSpec grid = _scenario.Grid;
            TextureRegistry registry = _app.Registry;
            ComputeShader cs = vegetationCompute;
            GeometryUtility.CalculateFrustumPlanes(cam, _frustum);
            for (int i = 0; i < 6; i++)
                _planes[i] = new Vector4(_frustum[i].normal.x, _frustum[i].normal.y, _frustum[i].normal.z, _frustum[i].distance);
            for (int i = 0; i < _lods.Length; i++) _lods[i].SetCounterValue(0);
            _burning.SetCounterValue(0);
            cs.SetInt("_PlantCount", PlantCount);
            cs.SetVectorArray("_Planes", _planes);
            cs.SetVector("_CameraPosition", cam.transform.position);
            cs.SetFloat("_PixelsPerMeter", 0.5f * cam.pixelHeight * cam.projectionMatrix[1, 1]);
            cs.SetVector("_LodPixels", LodPixels);
            cs.SetFloat("_MaxDistance", Mathf.Clamp(DrawDistanceM, MinDrawDistanceM, MaxDrawDistanceM));
            cs.SetFloat("_Density", Mathf.Clamp01(Density));
            cs.SetFloat("_EffectDistance", EffectsVisible ? EffectDistanceM : 0f);
            cs.SetFloat("_SmolderS", SmolderS);
            cs.SetVector("_FireCA_GridParams", Shader.GetGlobalVector("_FireCA_GridParams"));
            cs.SetVector("_FireCA_FireParams", Shader.GetGlobalVector("_FireCA_FireParams"));
            cs.SetTexture(_kernel, "_FireCA_ArrivalTime", registry.Get(SimTextures.ArrivalTime));
            cs.SetTexture(_kernel, "_FireCA_SpreadB", registry.Get(SimTextures.SpreadB));
            cs.SetBuffer(_kernel, "_Plants", _plants);
            cs.SetBuffer(_kernel, "_Lod0", _lods[0]);
            cs.SetBuffer(_kernel, "_Lod1", _lods[1]);
            cs.SetBuffer(_kernel, "_Lod2", _lods[2]);
            cs.SetBuffer(_kernel, "_Burning", _burning);
            cs.Dispatch(_kernel, (PlantCount + 255) / 256, 1, 1);
            int argsStride = GraphicsBuffer.IndirectDrawArgs.size;
            for (int lod = 0; lod < _lods.Length; lod++)
                GraphicsBuffer.CopyCount(_lods[lod], _args, lod * argsStride + sizeof(uint));
            GraphicsBuffer.CopyCount(_burning, _args, 3 * argsStride + sizeof(uint));
            GraphicsBuffer.CopyCount(_burning, _args, 4 * argsStride + sizeof(uint));
            if (++_frame % 20 == 0) RequestCounts();

            // 2. The light on the Plants: the sun, and the sky as in the Smoke Pass (decision D30).
            Vector3 sun = _app.Weather.SunDirection;
            Light light = _app.SunLight;
            float day = Mathf.Clamp01(sun.y * 4f);
            Color sunColor = light != null ? light.color.linear * light.intensity * Mathf.Clamp01(sun.y * 10f) : Color.black;
            Color sky = Color.Lerp(SmokePass.NightSkyColor, new Color(3500f, 4200f, 5200f), day * day);
            var sunDirection = new Vector4(sun.x, Mathf.Max(sun.y, 0.02f), sun.z, 0f);

            float extent = grid.ExtentM;
            var bounds = new Bounds(new Vector3(extent * 0.5f, grid.WindBottomY + 2048f, extent * 0.5f), new Vector3(extent, 4096f, extent) * 1.2f);

            // 3. Draw the Plants, one call per level of detail.
            for (int lod = 0; lod < PlantModels.LodCount; lod++)
            {
                MaterialPropertyBlock block = _blocks[lod];
                block.SetBuffer("_Plants", _plants);
                block.SetBuffer("_Visible", _lods[lod]);
                block.SetBuffer("_PlantVertices", _vertices);
                block.SetTexture("_LeafAtlas", leafAtlas);
                block.SetInt("_LodStart", Models.LodStart[lod]);
                block.SetInt("_LodVertices", Models.VerticesPerModel[lod]);
                block.SetFloat("_Detail", lod == 0 ? 1f : lod == 1 ? 0.55f : 0f);
                block.SetFloat("_VegSway", Sway);
                block.SetVector("_VegSunDirection", sunDirection);
                block.SetVector("_VegSunColor", sunColor);
                block.SetVector("_VegSkyColor", sky);
                var parameters = new RenderParams(_plantMaterial)
                {
                    worldBounds = bounds,
                    matProps = block,
                    // The far level casts no shadows. Its Plants are too small on the screen to show them.
                    shadowCastingMode = CastShadows && lod < 2 ? ShadowCastingMode.On : ShadowCastingMode.Off,
                    receiveShadows = false,
                };
                Graphics.RenderPrimitivesIndirect(parameters, MeshTopology.Triangles, _args, 1, lod);
            }

            // 4. Draw the flames and the smoke of the burning Plants.
            if (!EffectsVisible || _flameMaterial == null) return;
            for (int k = 0; k < 2; k++)
            {
                MaterialPropertyBlock block = _blocks[3 + k];
                block.SetBuffer("_Plants", _plants);
                block.SetBuffer("_Visible", _burning);
                block.SetFloat("_SmolderS", SmolderS);
                block.SetVector("_VegSunDirection", sunDirection);
                block.SetVector("_VegSunColor", sunColor);
                block.SetVector("_VegSkyColor", sky);
                var parameters = new RenderParams(k == 0 ? _flameMaterial : _smokeMaterial)
                {
                    worldBounds = bounds,
                    matProps = block,
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = false,
                };
                Graphics.RenderPrimitivesIndirect(parameters, MeshTopology.Triangles, _args, 1, 3 + k);
            }
        }
    }
}
