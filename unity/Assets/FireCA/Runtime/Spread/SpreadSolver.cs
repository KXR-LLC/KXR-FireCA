using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FireCA
{
    /// <summary>
    /// The Spread Solver: the compute shader system that computes fire behavior and moves the fire across the Fire Grid.
    /// One Fire step runs the behavior pass, the arrival pass, the heat flux pass, and the Active Mask update.
    /// </summary>
    public sealed class SpreadSolver : IDisposable
    {
        public const float NotIgnited = 1.0e30f;
        public const float MinDtS = 0.05f;
        public const float MaxDtS = 30f;
        /// <summary>The radius of the ignite tool, m.</summary>
        public const float IgniteToolRadiusM = 6f;

        static readonly int FireGridSizeId = Shader.PropertyToID("_FireGridSize");
        static readonly int FireCellSizeId = Shader.PropertyToID("_FireCellSize");
        static readonly int BlockSizeId = Shader.PropertyToID("_BlockSize");
        static readonly int FootprintSizeXId = Shader.PropertyToID("_FootprintSizeX");
        static readonly int FootprintSizeZId = Shader.PropertyToID("_FootprintSizeZ");
        static readonly int NowId = Shader.PropertyToID("_Now");
        static readonly int DtId = Shader.PropertyToID("_Dt");
        static readonly int SourceHoldId = Shader.PropertyToID("_SourceHoldS");
        static readonly int MaskHoldId = Shader.PropertyToID("_MaskHoldS");
        static readonly int SmolderFractionId = Shader.PropertyToID("_SmolderFraction");
        static readonly int SmolderDecayId = Shader.PropertyToID("_SmolderDecayS");
        static readonly int M10Id = Shader.PropertyToID("_M10");
        static readonly int M100Id = Shader.PropertyToID("_M100");
        static readonly int MLiveId = Shader.PropertyToID("_MLive");
        static readonly int FullGridId = Shader.PropertyToID("_FullGrid");
        static readonly int IgniteCircleId = Shader.PropertyToID("_IgniteCircle");
        static readonly int IgniteOriginId = Shader.PropertyToID("_IgniteOrigin");
        static readonly int CheckSizeId = Shader.PropertyToID("_CheckSize");

        readonly ComputeShader _cs;
        readonly TextureRegistry _registry;
        readonly WeatherModel _weather;
        readonly GridSpec _grid;
        readonly ComputeBuffer _fuelBuffer, _reduceBuffer, _countBuffer;
        readonly int _heatFluxCoarse;
        readonly int _behavior, _arrival, _heatFlux, _blockStats, _activeMask, _reduce, _ignite, _clear, _countNonFinite;

        bool _hasIgnition;
        int _ignitionSerial;
        bool _disposed;

        /// <summary>A cell that ignited more than this many seconds ago does not ignite a neighbor.</summary>
        public float SourceHoldS = 1800f;
        public float SmolderFraction = 0.05f;
        public float SmolderDecayS = 600f;
        /// <summary>Multiplier on the computed Fire step. The step independence test sets 0.25.</summary>
        public float StepScale = 1f;
        /// <summary>When true, every Fire step waits for the GPU reduction. The tests set true for a repeatable run.</summary>
        public bool SynchronousReadback;

        /// <summary>The fire time, in seconds from the start of the Scenario. The fire time can lead the Sim Clock by one Fire step.</summary>
        public double FireTime { get; private set; }
        public float LastDt { get; private set; }
        /// <summary>The largest head rate of spread in the active blocks, m/s. The newest value that is available.</summary>
        public float RMax { get; private set; }
        public int ActiveBlocks { get; private set; }
        public long IgnitedCells { get; private set; }
        public long FlamingCells { get; private set; }
        public long StepCount { get; private set; }
        public double BurnedAreaHa => IgnitedCells * (double)(GridSpec.FireCellSizeM * GridSpec.FireCellSizeM) / 10000.0;

        /// <summary>
        /// A block stays in the Active Mask for two Fire steps of the largest size after its last cell stops being a source,
        /// so the heat flux pass writes zero for a burned-out cell before the block leaves the Active Mask (decision D10).
        /// </summary>
        public float MaskHoldS => SourceHoldS + 2f * MaxDtS;

        /// <summary>False when no cell is ignited, or when every ignited cell is burned out.</summary>
        public bool HasFire => _hasIgnition;

        public SpreadSolver(ComputeShader compute, TextureRegistry registry, WeatherModel weather)
        {
            if (GridSpec.BlockSize != 16)
                throw new InvalidOperationException("The cell kernels of Spread.compute use thread groups of 16 x 16 cells.");
            _cs = compute;
            _registry = registry;
            _weather = weather;
            _grid = registry.Grid;

            _fuelBuffer = new ComputeBuffer(FuelTable.EntryCount, FuelConstantsGpu.Stride, ComputeBufferType.Structured);
            _fuelBuffer.SetData(FuelTable.BuildGpuTable());
            _reduceBuffer = new ComputeBuffer(_grid.WindCellsZ, 4 * sizeof(float), ComputeBufferType.Structured);
            _countBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);

            _behavior = _cs.FindKernel("BehaviorKernel");
            _arrival = _cs.FindKernel("ArrivalKernel");
            _heatFlux = _cs.FindKernel("HeatFluxKernel");
            _heatFluxCoarse = _cs.FindKernel("HeatFluxCoarseKernel");
            _blockStats = _cs.FindKernel("BlockStatsKernel");
            _activeMask = _cs.FindKernel("ActiveMaskKernel");
            _reduce = _cs.FindKernel("ReduceKernel");
            _ignite = _cs.FindKernel("IgniteKernel");
            _clear = _cs.FindKernel("ClearKernel");
            _countNonFinite = _cs.FindKernel("CountNonFiniteKernel");
        }

        int CellGroups => _grid.FireCells / GridSpec.BlockSize;

        void SetUniforms(double now, float dt, bool fullGrid)
        {
            _cs.SetInt(FireGridSizeId, _grid.FireCells);
            _cs.SetFloat(FireCellSizeId, GridSpec.FireCellSizeM);
            _cs.SetInt(BlockSizeId, GridSpec.BlockSize);
            _cs.SetInt(FootprintSizeXId, _grid.WindCellsX);
            _cs.SetInt(FootprintSizeZId, _grid.WindCellsZ);
            _cs.SetFloat(NowId, (float)now);
            _cs.SetFloat(DtId, dt);
            _cs.SetFloat(SourceHoldId, SourceHoldS);
            _cs.SetFloat(MaskHoldId, MaskHoldS);
            _cs.SetFloat(SmolderFractionId, SmolderFraction);
            _cs.SetFloat(SmolderDecayId, SmolderDecayS);
            _cs.SetFloat(M10Id, _weather.M10);
            _cs.SetFloat(M100Id, _weather.M100);
            _cs.SetFloat(MLiveId, _weather.MLive);
            _cs.SetInt(FullGridId, fullGrid ? 1 : 0);
        }

        /// <summary>Clears the fire, runs the behavior pass on the full Fire Grid, and sets the fire time to zero.</summary>
        public void Reset()
        {
            FireTime = 0.0;
            StepCount = 0;
            ClearFire();
            RunBehavior(true);
        }

        /// <summary>Sets the fire time. The Step Scheduler calls this while no cell burns.</summary>
        public void SetFireTime(double seconds) => FireTime = seconds;

        /// <summary>Runs the behavior pass: on the full Fire Grid, or on the active blocks.</summary>
        public void RunBehavior(bool fullGrid)
        {
            SetUniforms(FireTime, 0f, fullGrid);
            _cs.SetBuffer(_behavior, "_FuelTable", _fuelBuffer);
            _cs.SetTexture(_behavior, "_Fuel", _registry.Get(SimTextures.Fuel));
            _cs.SetTexture(_behavior, "_Slope", _registry.Get(SimTextures.Slope));
            _cs.SetTexture(_behavior, "_Moisture1h", _registry.Get(SimTextures.Moisture1h));
            _cs.SetTexture(_behavior, "_SurfaceWind", _registry.Get(SimTextures.SurfaceWind));
            _cs.SetTexture(_behavior, "_ActiveMaskRead", _registry.Get(SimTextures.ActiveMask));
            _cs.SetTexture(_behavior, "_SpreadAOut", _registry.Get(SimTextures.SpreadA));
            _cs.SetTexture(_behavior, "_SpreadBOut", _registry.Get(SimTextures.SpreadB));
            _cs.Dispatch(_behavior, CellGroups, CellGroups, 1);
        }

        /// <summary>The Fire step that the newest largest rate of spread allows, s.</summary>
        public float ComputeDt()
        {
            float dt = RMax > 0f ? GridSpec.FireCellSizeM / (1.25f * RMax) : MaxDtS;
            return Mathf.Clamp(dt, MinDtS, MaxDtS) * StepScale;
        }

        /// <summary>Runs one Fire step. Returns false, and runs nothing, when no cell burns.</summary>
        public bool Step(float maxDt = float.PositiveInfinity)
        {
            if (!_hasIgnition) return false;
            float dt = Mathf.Min(ComputeDt(), maxDt);
            if (dt <= 0f) return false;

            RunBehavior(false);
            SetUniforms(FireTime, dt, false);

            // Arrival pass: read copy to write copy, then swap.
            _cs.SetTexture(_arrival, "_Fuel", _registry.Get(SimTextures.Fuel));
            _cs.SetTexture(_arrival, "_Elevation", _registry.Get(SimTextures.Elevation));
            _cs.SetTexture(_arrival, "_SpreadARead", _registry.Get(SimTextures.SpreadA));
            _cs.SetTexture(_arrival, "_ActiveMaskRead", _registry.Get(SimTextures.ActiveMask));
            _cs.SetTexture(_arrival, "_ArrivalRead", _registry.Get(SimTextures.ArrivalTime));
            _cs.SetTexture(_arrival, "_ArrivalOut", _registry.GetWrite(SimTextures.ArrivalTime));
            _cs.Dispatch(_arrival, CellGroups, CellGroups, 1);
            _registry.Swap(SimTextures.ArrivalTime);

            // Heat flux pass.
            _cs.SetTexture(_heatFlux, "_ActiveMaskRead", _registry.Get(SimTextures.ActiveMask));
            _cs.SetTexture(_heatFlux, "_ArrivalRead", _registry.Get(SimTextures.ArrivalTime));
            _cs.SetTexture(_heatFlux, "_SpreadBRead", _registry.Get(SimTextures.SpreadB));
            _cs.SetTexture(_heatFlux, "_HeatFluxOut", _registry.Get(SimTextures.HeatFlux));
            _cs.Dispatch(_heatFlux, CellGroups, CellGroups, 1);
            WriteHeatFluxCoarse(false);

            UpdateActiveMask(false);
            FireTime += dt;
            LastDt = dt;
            StepCount++;
            RequestStats(SynchronousReadback);
            return true;
        }

        /// <summary>Runs Fire steps until the fire time reaches the time. The last Fire step ends on the time.</summary>
        public void RunUntil(double seconds)
        {
            while (FireTime < seconds - 1e-6)
            {
                if (!Step((float)(seconds - FireTime)))
                {
                    FireTime = seconds;
                    break;
                }
            }
        }

        // Heat Flux Coarse: the mean of the Heat Flux over each block. The Wind Solver reads the texture.
        void WriteHeatFluxCoarse(bool fullGrid)
        {
            _cs.SetInt(FullGridId, fullGrid ? 1 : 0);
            _cs.SetTexture(_heatFluxCoarse, "_ActiveMaskRead", _registry.Get(SimTextures.ActiveMask));
            _cs.SetTexture(_heatFluxCoarse, "_HeatFluxRead", _registry.Get(SimTextures.HeatFlux));
            _cs.SetTexture(_heatFluxCoarse, "_HeatFluxCoarseOut", _registry.Get(SimTextures.HeatFluxCoarse));
            _cs.Dispatch(_heatFluxCoarse, (_grid.WindCellsX + 7) / 8, (_grid.WindCellsZ + 7) / 8, 1);
            _cs.SetInt(FullGridId, 0);
        }

        // Block statistics, then the Active Mask, then the reduction. The uniforms _Now and _Dt must be set.
        void UpdateActiveMask(bool fullScan)
        {
            _cs.SetInt(FullGridId, fullScan ? 1 : 0);
            _cs.SetTexture(_blockStats, "_ActiveMaskRead", _registry.Get(SimTextures.ActiveMask));
            _cs.SetTexture(_blockStats, "_ArrivalRead", _registry.Get(SimTextures.ArrivalTime));
            _cs.SetTexture(_blockStats, "_SpreadARead", _registry.Get(SimTextures.SpreadA));
            _cs.SetTexture(_blockStats, "_SpreadBRead", _registry.Get(SimTextures.SpreadB));
            _cs.SetTexture(_blockStats, "_BlockStats", _registry.Get(SimTextures.BlockStats));
            int gx = (_grid.WindCellsX + 7) / 8, gz = (_grid.WindCellsZ + 7) / 8;
            _cs.Dispatch(_blockStats, gx, gz, 1);

            _cs.SetTexture(_activeMask, "_BlockStatsRead", _registry.Get(SimTextures.BlockStats));
            _cs.SetTexture(_activeMask, "_ActiveMaskOut", _registry.Get(SimTextures.ActiveMask));
            _cs.Dispatch(_activeMask, gx, gz, 1);

            _cs.SetTexture(_reduce, "_BlockStatsRead", _registry.Get(SimTextures.BlockStats));
            _cs.SetTexture(_reduce, "_ActiveMaskRead", _registry.Get(SimTextures.ActiveMask));
            _cs.SetBuffer(_reduce, "_ReduceOut", _reduceBuffer);
            _cs.Dispatch(_reduce, (_grid.WindCellsZ + 63) / 64, 1, 1);
            _cs.SetInt(FullGridId, 0);
        }

        void RequestStats(bool synchronous)
        {
            int serial = _ignitionSerial;
            if (synchronous)
            {
                var request = AsyncGPUReadback.Request(_reduceBuffer);
                request.WaitForCompletion();
                ApplyStats(request, serial);
            }
            else
            {
                AsyncGPUReadback.Request(_reduceBuffer, request => ApplyStats(request, serial));
            }
        }

        void ApplyStats(AsyncGPUReadbackRequest request, int serial)
        {
            if (_disposed || request.hasError || serial != _ignitionSerial) return;
            NativeArray<Vector4> rows = request.GetData<Vector4>();
            float active = 0f, rMax = 0f;
            double ignited = 0.0, flaming = 0.0;
            for (int i = 0; i < rows.Length; i++)
            {
                active += rows[i].x;
                rMax = Mathf.Max(rMax, rows[i].y);
                ignited += rows[i].z;
                flaming += rows[i].w;
            }
            ActiveBlocks = Mathf.RoundToInt(active);
            RMax = rMax;
            IgnitedCells = (long)Math.Round(ignited);
            FlamingCells = (long)Math.Round(flaming);
            // Every ignited cell is burned out: the fire is over until the next ignition.
            if (ActiveBlocks == 0) _hasIgnition = false;
        }

        /// <summary>Sets the Arrival Time of every burnable cell in the circle that is not ignited.</summary>
        public void Ignite(Vector2 centerXZ, float radiusM, double time)
        {
            float cell = GridSpec.FireCellSizeM;
            int reach = Mathf.CeilToInt(radiusM / cell) + 1;
            int ox = Mathf.FloorToInt(centerXZ.x / cell) - reach, oz = Mathf.FloorToInt(centerXZ.y / cell) - reach;
            int size = 2 * reach + 1;

            SetUniforms(FireTime, 0f, false);
            _cs.SetVector(IgniteCircleId, new Vector4(centerXZ.x, centerXZ.y, radiusM, (float)time));
            _cs.SetInts(IgniteOriginId, ox, oz);
            _cs.SetTexture(_ignite, "_Fuel", _registry.Get(SimTextures.Fuel));
            _cs.SetTexture(_ignite, "_ArrivalOut", _registry.Get(SimTextures.ArrivalTime));
            _cs.SetTexture(_ignite, "_ArrivalOutB", _registry.GetWrite(SimTextures.ArrivalTime));
            _cs.Dispatch(_ignite, (size + 7) / 8, (size + 7) / 8, 1);

            // The Active Mask updates before the next Fire step. The readback waits, so the next Fire step has a valid size.
            _hasIgnition = true;
            _ignitionSerial++;
            UpdateActiveMask(true);
            RequestStats(true);
        }

        /// <summary>Ignites the Fire Grid cell (i, j).</summary>
        public void IgniteCell(int i, int j, double time) =>
            Ignite(new Vector2((i + 0.5f) * GridSpec.FireCellSizeM, (j + 0.5f) * GridSpec.FireCellSizeM), 0.1f, time);

        /// <summary>Sets the Arrival Time of every cell to "not ignited" and clears the Heat Flux.</summary>
        public void ClearFire()
        {
            SetUniforms(FireTime, 0f, true);
            _cs.SetTexture(_clear, "_ArrivalOut", _registry.Get(SimTextures.ArrivalTime));
            _cs.SetTexture(_clear, "_ArrivalOutB", _registry.GetWrite(SimTextures.ArrivalTime));
            _cs.SetTexture(_clear, "_HeatFluxOut", _registry.Get(SimTextures.HeatFlux));
            _cs.Dispatch(_clear, CellGroups, CellGroups, 1);
            WriteHeatFluxCoarse(true);
            _hasIgnition = false;
            _ignitionSerial++;
            UpdateActiveMask(true);
            RequestStats(true);
        }

        /// <summary>
        /// Benchmark support: runs the arrival pass with every block active, into the write copy, with no swap.
        /// The write copy does not match the read copy after this call, so the caller must reload the Scenario.
        /// </summary>
        public void BenchmarkArrivalAllBlocks()
        {
            SetUniforms(FireTime, 1f, true);
            _cs.SetTexture(_arrival, "_Fuel", _registry.Get(SimTextures.Fuel));
            _cs.SetTexture(_arrival, "_Elevation", _registry.Get(SimTextures.Elevation));
            _cs.SetTexture(_arrival, "_SpreadARead", _registry.Get(SimTextures.SpreadA));
            _cs.SetTexture(_arrival, "_ActiveMaskRead", _registry.Get(SimTextures.ActiveMask));
            _cs.SetTexture(_arrival, "_ArrivalRead", _registry.Get(SimTextures.ArrivalTime));
            _cs.SetTexture(_arrival, "_ArrivalOut", _registry.GetWrite(SimTextures.ArrivalTime));
            _cs.Dispatch(_arrival, CellGroups, CellGroups, 1);
            _cs.SetInt(FullGridId, 0);
        }

        /// <summary>Counts the pixels of a 2D texture that hold a NaN value or an infinite value.</summary>
        public long CountNonFinite(Texture texture)
        {
            _countBuffer.SetData(new uint[] { 0 });
            _cs.SetInts(CheckSizeId, texture.width, texture.height);
            _cs.SetTexture(_countNonFinite, "_CheckTexture", texture);
            _cs.SetBuffer(_countNonFinite, "_NonFiniteCount", _countBuffer);
            _cs.Dispatch(_countNonFinite, (texture.width + 7) / 8, (texture.height + 7) / 8, 1);
            var result = new uint[1];
            _countBuffer.GetData(result);
            return result[0];
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            AsyncGPUReadback.WaitAllRequests();
            _fuelBuffer.Release();
            _reduceBuffer.Release();
            _countBuffer.Release();
        }
    }
}
