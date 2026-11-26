using UnityEngine;
using UnityEngine.Rendering;

namespace FireCA
{
    /// <summary>
    /// Wind Particles: GPU particles that trace the wind field (docs/04-visualization.md).
    /// A compute kernel moves the particles with the Velocity of the Wind Grid. A procedural draw renders each particle
    /// as a streak. See docs/decisions.md, decision D21, for the reason that the effect is not a VFX Graph asset.
    /// </summary>
    public sealed class WindParticles : MonoBehaviour
    {
        public const int DefaultCount = 2000000;
        public const int MinCount = 100000;
        public const int MaxCount = 4000000;
        const int Stride = 8 * sizeof(float);

        [SerializeField] ComputeShader particleCompute;
        [SerializeField] Material particleMaterial;

        /// <summary>Multiplier on the particle motion. The Sim Clock time scale does not change the particle speed.</summary>
        public float ParticleSpeedScale = 30f;
        /// <summary>A particle is removed after this many seconds.</summary>
        public float LifeS = 20f;
        public float MinHeightM = 10f;
        public float MaxHeightM = 400f;
        /// <summary>Brightness of one streak. The streaks add up where many particles overlap.</summary>
        public float Brightness = 0.28f;
        public const float MaxSizeM = 40f;
        /// <summary>The width of one streak, m. At 0, a streak is 1 pixel wide.</summary>
        public float SizeM { get; private set; }

        public void SetSize(float meters) => SizeM = Mathf.Clamp(meters, 0f, MaxSizeM);

        public bool Visible { get; set; } = true;
        public int Count { get; private set; } = DefaultCount;

        public ComputeShader ParticleCompute { get => particleCompute; set => particleCompute = value; }
        public Material ParticleMaterial { get => particleMaterial; set => particleMaterial = value; }

        FireCAApp _app;
        ComputeBuffer _particles;
        Material _material;
        int _kernel;
        uint _frame;

        void Start()
        {
            _app = FireCAApp.Instance;
            if (particleMaterial != null) _material = new Material(particleMaterial) { hideFlags = HideFlags.HideAndDontSave };
            if (particleCompute != null) _kernel = particleCompute.FindKernel("UpdateKernel");
        }

        void OnDestroy()
        {
            _particles?.Release();
            _particles = null;
            if (_material != null) Destroy(_material);
        }

        public void SetCount(int count)
        {
            Count = Mathf.Clamp(count, MinCount, MaxCount);
        }

        void EnsureBuffer()
        {
            if (_particles != null && _particles.count == Count) return;
            _particles?.Release();
            _particles = new ComputeBuffer(Count, Stride, ComputeBufferType.Structured);
            // A zero particle has never spawned. The update kernel spawns the particle.
            _particles.SetData(new float[Count * 8]);
        }

        void LateUpdate()
        {
            if (!Visible || _app == null || _app.Scenario == null || _app.Wind == null || !_app.Wind.Enabled) return;
            if (particleCompute == null || _material == null) return;
            EnsureBuffer();

            GridSpec grid = _app.Scenario.Grid;
            TextureRegistry registry = _app.Registry;
            ComputeShader cs = particleCompute;
            cs.SetInt("_ParticleCount", Count);
            cs.SetFloat("_DeltaTime", Mathf.Min(Time.unscaledDeltaTime, 0.1f));
            cs.SetFloat("_SpeedScale", ParticleSpeedScale);
            cs.SetFloat("_LifeS", LifeS);
            cs.SetFloat("_MinHeightM", MinHeightM);
            cs.SetFloat("_MaxHeightM", MaxHeightM);
            cs.SetInt("_Seed", (int)(++_frame * 747796405u));
            cs.SetFloat("_TurbulenceGain", _app.TurbulenceGain);
            cs.SetFloat("_TurbulenceTime", Time.unscaledTime);
            cs.SetTexture(_kernel, "_TurbulenceRead", registry.Get(SimTextures.Turbulence));
            WindNest nest = _app.Nest;
            bool nested = nest != null && nest.Active;
            cs.SetTexture(_kernel, "_NestVelocity", nested ? nest.Velocity : registry.Get(SimTextures.Velocity));
            cs.SetTexture(_kernel, "_NestTurbulence", nested ? nest.Turbulence : registry.Get(SimTextures.Turbulence));
            cs.SetVector("_NestGrid", nested ? new Vector4(nest.Domain.Nx, nest.Domain.Ny, nest.Domain.Nz, nest.Domain.CellSize) : Vector4.zero);
            cs.SetVector("_NestOrigin", nested ? new Vector4(nest.Domain.OriginXZ.x, nest.Domain.BottomY, nest.Domain.OriginXZ.y, nest.RampM) : Vector4.zero);
            cs.SetInt("_WindGridSizeX", grid.WindCellsX);
            cs.SetInt("_WindGridSizeY", GridSpec.WindCellsY);
            cs.SetInt("_WindGridSizeZ", grid.WindCellsZ);
            cs.SetFloat("_WindCellSize", GridSpec.WindCellSizeM);
            cs.SetFloat("_WindBottomY", grid.WindBottomY);
            cs.SetTexture(_kernel, "_VelocityRead", registry.Get(SimTextures.Velocity));
            cs.SetTexture(_kernel, "_SolidMask", registry.Get(SimTextures.SolidMask));
            cs.SetTexture(_kernel, "_GroundHeight", registry.Get(SimTextures.GroundHeight));
            cs.SetBuffer(_kernel, "_Particles", _particles);
            cs.Dispatch(_kernel, (Count + 255) / 256, 1, 1);

            _material.SetBuffer("_Particles", _particles);
            _material.SetFloat("_SpeedScale", ParticleSpeedScale);
            _material.SetFloat("_LifeS", LifeS);
            _material.SetFloat("_Brightness", Brightness);
            _material.SetFloat("_SizeM", SizeM);
            float extent = grid.ExtentM;
            var bounds = new Bounds(new Vector3(extent * 0.5f, grid.WindBottomY + 2048f, extent * 0.5f), new Vector3(extent, 4096f, extent) * 1.2f);
            var parameters = new RenderParams(_material)
            {
                worldBounds = bounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderPrimitives(parameters, MeshTopology.Triangles, Count * 6);
        }
    }
}
