using System;

namespace FireCA
{
    /// <summary>Sizes of the Fire Grid and the Wind Grid for one Scenario.</summary>
    public readonly struct GridSpec
    {
        public const float FireCellSizeM = 2f;
        public const float WindCellSizeM = 32f;
        /// <summary>Fire Grid cells per Wind Grid column, on X and on Z.</summary>
        public const int BlockSize = 16;
        public const int WindCellsY = 128;

        /// <summary>Fire Grid cells per side.</summary>
        public readonly int FireCells;
        /// <summary>World Y of the bottom face of the Wind Grid, m.</summary>
        public readonly float WindBottomY;

        public GridSpec(int fireCells, float minElevationM)
        {
            FireCells = fireCells;
            WindBottomY = WindBottomFor(minElevationM);
        }

        public int WindCellsX => FireCells / BlockSize;
        public int WindCellsZ => FireCells / BlockSize;
        public float ExtentM => FireCells * FireCellSizeM;
        public long FireCellTotal => (long)FireCells * FireCells;
        public long WindCellTotal => (long)WindCellsX * WindCellsY * WindCellsZ;

        public static bool IsValidFireSize(int n) => n == 512 || n == 1024 || n == 2048 || n == 4096;

        /// <summary>The lowest elevation, rounded down to a multiple of the Wind Grid cell size.</summary>
        public static float WindBottomFor(float minElevationM) =>
            (float)(Math.Floor(minElevationM / WindCellSizeM) * WindCellSizeM);
    }
}
