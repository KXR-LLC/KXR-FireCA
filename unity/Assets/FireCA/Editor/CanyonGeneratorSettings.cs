using System;
using UnityEngine;

namespace FireCA.Editor
{
    /// <summary>
    /// The settings asset of the Canyon Generator. Every length is in meters on the full-size grid of 8192 m.
    /// A smaller grid is a uniform scale model of the full-size canyon (docs/decisions.md, decision D2).
    /// </summary>
    [CreateAssetMenu(menuName = "FireCA/Canyon Generator Settings", fileName = "CanyonGeneratorSettings")]
    public sealed class CanyonGeneratorSettings : ScriptableObject
    {
        [Serializable]
        public struct SideDrainage
        {
            public bool eastWall;
            [Tooltip("Position of the drainage mouth along the canyon axis, m from the south edge.")]
            public float zM;
            [Tooltip("Rotation of the drainage away from the wall normal, degrees.")]
            public float angleDeg;
            [Tooltip("Slope of the drainage floor, rise over run.")]
            public float grade;
        }

        [Header("Scenario")]
        public string scenarioName = "synthetic-canyon-01";
        public int gridSize = 4096;
        public int seed = 1;

        [Header("Main canyon")]
        public float axisAmplitudeM = 400f;
        public float axisWavelengthM = 6000f;
        [Tooltip("Position of the first bend of the canyon axis, m from the south edge.")]
        public float axisFirstBendZM = 1100f;
        public float floorElevationSouthM = 1240f;
        public float floorDropM = 150f;
        public float rimElevationM = 1900f;
        public float floorWidthM = 250f;
        public float wallWidthM = 1000f;
        public float wallExponent = 1.2f;
        [Tooltip("Slope of the ground behind the rim, rise over run.")]
        public float outerSlope = 0.10f;
        public float outerRunM = 1200f;

        [Header("Narrow section")]
        public float narrowCenterZM = 5800f;
        public float narrowLengthM = 500f;
        public float narrowRampM = 300f;
        public float narrowFloorWidthM = 60f;
        public float narrowWallWidthM = 700f;

        [Header("Chute (east wall, faces west)")]
        public float chuteZM = 4100f;
        public float chuteGrade = 0.74f;
        public float chuteFloorWidthM = 20f;
        public float chuteWidthM = 200f;

        [Header("Saddle (west rim)")]
        public float saddleZM = 2600f;
        public float saddleDepthM = 110f;
        public float saddleAlongM = 200f;
        public float saddleAcrossM = 900f;

        [Header("Side drainages")]
        public SideDrainage[] drainages =
        {
            new SideDrainage { eastWall = false, zM = 1500f, angleDeg = 12f, grade = 0.5f },
            new SideDrainage { eastWall = false, zM = 3500f, angleDeg = -10f, grade = 0.5f },
            new SideDrainage { eastWall = false, zM = 6600f, angleDeg = 15f, grade = 0.5f },
            new SideDrainage { eastWall = true, zM = 2400f, angleDeg = -12f, grade = 0.5f },
            new SideDrainage { eastWall = true, zM = 7300f, angleDeg = 10f, grade = 0.5f },
        };

        [Header("Detail")]
        [Tooltip("Largest height of the fractal noise detail, m. The value must be under 40.")]
        public float noiseAmplitudeM = 24f;
        public float noiseWavelengthM = 500f;
        public float fuelPatchWavelengthM = 320f;
    }
}
