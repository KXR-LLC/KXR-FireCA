using UnityEngine;

namespace FireCA
{
    /// <summary>Runs the compute kernels that write Slope, Normal, Ground Height, and Solid Mask.</summary>
    public static class DerivedLayers
    {
        static readonly int FireGridSize = Shader.PropertyToID("_FireGridSize");
        static readonly int FireCellSize = Shader.PropertyToID("_FireCellSize");
        static readonly int BlockSize = Shader.PropertyToID("_BlockSize");
        static readonly int WindGridSizeX = Shader.PropertyToID("_WindGridSizeX");
        static readonly int WindGridSizeY = Shader.PropertyToID("_WindGridSizeY");
        static readonly int WindGridSizeZ = Shader.PropertyToID("_WindGridSizeZ");
        static readonly int WindCellSize = Shader.PropertyToID("_WindCellSize");
        static readonly int WindBottomY = Shader.PropertyToID("_WindBottomY");

        public static void Run(ComputeShader cs, TextureRegistry registry)
        {
            GridSpec grid = registry.Grid;
            cs.SetInt(FireGridSize, grid.FireCells);
            cs.SetFloat(FireCellSize, GridSpec.FireCellSizeM);
            cs.SetInt(BlockSize, GridSpec.BlockSize);
            cs.SetInts("_BlockOrigin", 0, 0);
            cs.SetInt(WindGridSizeX, grid.WindCellsX);
            cs.SetInt(WindGridSizeY, GridSpec.WindCellsY);
            cs.SetInt(WindGridSizeZ, grid.WindCellsZ);
            cs.SetFloat(WindCellSize, GridSpec.WindCellSizeM);
            cs.SetFloat(WindBottomY, grid.WindBottomY);

            var elevation = registry.Get(SimTextures.Elevation);
            var slope = registry.Get(SimTextures.Slope);
            var normal = registry.Get(SimTextures.Normal);
            var groundHeight = registry.Get(SimTextures.GroundHeight);
            var solidMask = registry.Get(SimTextures.SolidMask);

            int fireGroups = Groups(grid.FireCells, 8);

            int k = cs.FindKernel("SlopeKernel");
            cs.SetTexture(k, "_Elevation", elevation);
            cs.SetTexture(k, "_SlopeOut", slope);
            cs.Dispatch(k, fireGroups, fireGroups, 1);

            k = cs.FindKernel("NormalKernel");
            cs.SetTexture(k, "_SlopeIn", slope);
            cs.SetTexture(k, "_NormalOut", normal);
            cs.Dispatch(k, fireGroups, fireGroups, 1);

            k = cs.FindKernel("GroundHeightKernel");
            cs.SetTexture(k, "_Elevation", elevation);
            cs.SetTexture(k, "_GroundHeightOut", groundHeight);
            cs.Dispatch(k, Groups(grid.WindCellsX, 8), Groups(grid.WindCellsZ, 8), 1);

            k = cs.FindKernel("SolidMaskKernel");
            cs.SetTexture(k, "_GroundHeightIn", groundHeight);
            cs.SetTexture(k, "_SolidMaskOut", solidMask);
            cs.Dispatch(k, Groups(grid.WindCellsX, 4), Groups(GridSpec.WindCellsY, 4), Groups(grid.WindCellsZ, 4));
        }

        /// <summary>
        /// Writes the Ground Height and the Solid Mask of a grid with its own cell size and its own origin, for the
        /// Wind Nest (docs/decisions.md, decision D32). The cell size must be a multiple of the Fire Grid cell size.
        /// </summary>
        public static void RunForGrid(ComputeShader cs, Texture elevation, int fireCells, in WindDomain domain,
            Texture groundHeight, Texture solidMask)
        {
            int block = Mathf.RoundToInt(domain.CellSize / GridSpec.FireCellSizeM);
            cs.SetInt(FireGridSize, fireCells);
            cs.SetFloat(FireCellSize, GridSpec.FireCellSizeM);
            cs.SetInt(BlockSize, block);
            cs.SetInts("_BlockOrigin", Mathf.RoundToInt(domain.OriginXZ.x / GridSpec.FireCellSizeM), Mathf.RoundToInt(domain.OriginXZ.y / GridSpec.FireCellSizeM));
            cs.SetInt(WindGridSizeX, domain.Nx);
            cs.SetInt(WindGridSizeY, domain.Ny);
            cs.SetInt(WindGridSizeZ, domain.Nz);
            cs.SetFloat(WindCellSize, domain.CellSize);
            cs.SetFloat(WindBottomY, domain.BottomY);

            int k = cs.FindKernel("GroundHeightKernel");
            cs.SetTexture(k, "_Elevation", elevation);
            cs.SetTexture(k, "_GroundHeightOut", groundHeight);
            cs.Dispatch(k, Groups(domain.Nx, 8), Groups(domain.Nz, 8), 1);

            k = cs.FindKernel("SolidMaskKernel");
            cs.SetTexture(k, "_GroundHeightIn", groundHeight);
            cs.SetTexture(k, "_SolidMaskOut", solidMask);
            cs.Dispatch(k, Groups(domain.Nx, 4), Groups(domain.Ny, 4), Groups(domain.Nz, 4));
        }

        static int Groups(int cells, int threads) => (cells + threads - 1) / threads;
    }
}
