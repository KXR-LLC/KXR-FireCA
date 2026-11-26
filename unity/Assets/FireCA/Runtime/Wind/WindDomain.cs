using UnityEngine;

namespace FireCA
{
    /// <summary>
    /// The place and the size of one grid of the Wind Solver: the Wind Grid, or the grid of the Wind Nest
    /// (docs/decisions.md, decision D32).
    /// </summary>
    public readonly struct WindDomain
    {
        /// <summary>Cells on X, Y, and Z.</summary>
        public readonly int Nx, Ny, Nz;
        /// <summary>The cell size, m.</summary>
        public readonly float CellSize;
        /// <summary>World X and Z of the corner of cell (0, 0, 0), m.</summary>
        public readonly Vector2 OriginXZ;
        /// <summary>World Y of the bottom face, m.</summary>
        public readonly float BottomY;

        public WindDomain(int nx, int ny, int nz, float cellSize, Vector2 originXZ, float bottomY)
        {
            Nx = nx; Ny = ny; Nz = nz; CellSize = cellSize; OriginXZ = originXZ; BottomY = bottomY;
        }

        /// <summary>The domain of the Wind Grid of a Scenario.</summary>
        public static WindDomain OfWindGrid(GridSpec grid) =>
            new WindDomain(grid.WindCellsX, GridSpec.WindCellsY, grid.WindCellsZ, GridSpec.WindCellSizeM, Vector2.zero, grid.WindBottomY);

        public Vector3 Extent => new Vector3(Nx, Ny, Nz) * CellSize;
        public long Cells => (long)Nx * Ny * Nz;
    }

    /// <summary>The textures of one grid of the Wind Solver, by the names of <see cref="SimTextures"/>.</summary>
    public interface IWindTextures
    {
        Texture Get(string name);
        Texture GetWrite(string name);
        void Swap(string name);
        void Exchange(string a, string b);
    }
}
