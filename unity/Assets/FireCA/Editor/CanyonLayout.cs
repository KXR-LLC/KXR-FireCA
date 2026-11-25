using System;
using UnityEngine;

namespace FireCA.Editor
{
    /// <summary>
    /// The analytic shape of the synthetic canyon. Every coordinate is in full-scale meters (8192 m grid),
    /// unless a member name says World. The editor tests read the feature positions from this class.
    /// </summary>
    public sealed class CanyonLayout
    {
        public const double FullExtentM = 8192.0;
        public const double FullGridCells = 4096.0;
        /// <summary>The elevation that stays fixed when a smaller grid scales the canyon.</summary>
        public const double ScaleDatumM = 1200.0;
        /// <summary>The creek channel is 6 m wide in world meters, at every grid size.</summary>
        public const double CreekHalfWidthWorldM = 3.0;
        public const double RiparianFullM = 40.0;

        struct Carve
        {
            public double Ox, Oz, Dx, Dz;       // origin and unit direction (upslope)
            public double Grade, Inner, W0, WGrow, MaxLen;
            public bool IsChute;
        }

        public struct Sample
        {
            /// <summary>Elevation without noise, full-scale m.</summary>
            public double BaseElevation;
            /// <summary>0 on the canyon floor, 1 on the rim.</summary>
            public double RelativeHeight;
            /// <summary>Distance from the canyon axis, full-scale m.</summary>
            public double AxisDistance;
            public bool InChute;
            /// <summary>1 on the chute floor, 0 outside the chute.</summary>
            public double ChuteCore;
        }

        public readonly CanyonGeneratorSettings Settings;
        /// <summary>World meters per full-scale meter.</summary>
        public readonly double Scale;

        readonly double _k, _cx;
        readonly Carve[] _carves;
        readonly double _saddleX, _saddleZ, _saddleTx, _saddleTz, _saddleNx, _saddleNz;

        public CanyonLayout(CanyonGeneratorSettings settings)
        {
            Settings = settings;
            Scale = settings.gridSize / FullGridCells;
            _k = 2.0 * Math.PI / settings.axisWavelengthM;
            _cx = FullExtentM * 0.5;

            var s = settings;
            _carves = new Carve[s.drainages.Length + 1];
            for (int i = 0; i < s.drainages.Length; i++)
            {
                var d = s.drainages[i];
                double side = d.eastWall ? 1.0 : -1.0;
                AxisFrame(d.zM, out double ax, out double tx, out double tz, out double nx, out double nz);
                double hw = FloorHalfWidth(d.zM);
                double a = d.angleDeg * Math.PI / 180.0;
                double bx = side * nx, bz = side * nz;
                _carves[i] = new Carve
                {
                    Ox = ax + bx * hw, Oz = d.zM + bz * hw,
                    Dx = bx * Math.Cos(a) - bz * Math.Sin(a), Dz = bx * Math.Sin(a) + bz * Math.Cos(a),
                    Grade = d.grade, Inner = 12.0, W0 = 200.0, WGrow = 0.25, MaxLen = 2400.0, IsChute = false,
                };
            }
            {
                AxisFrame(s.chuteZM, out double ax, out _, out _, out double nx, out double nz);
                double hw = FloorHalfWidth(s.chuteZM);
                _carves[_carves.Length - 1] = new Carve
                {
                    Ox = ax + nx * hw, Oz = s.chuteZM + nz * hw, Dx = nx, Dz = nz,
                    Grade = s.chuteGrade, Inner = s.chuteFloorWidthM * 0.5, W0 = s.chuteWidthM * 0.5, WGrow = 0.0,
                    MaxLen = 1200.0, IsChute = true,
                };
            }
            {
                AxisFrame(s.saddleZM, out double ax, out _saddleTx, out _saddleTz, out _saddleNx, out _saddleNz);
                double reach = FloorHalfWidth(s.saddleZM) + WallWidth(s.saddleZM);
                _saddleX = ax - _saddleNx * reach;
                _saddleZ = s.saddleZM - _saddleNz * reach;
            }
        }

        // ---- canyon axis ----

        public double AxisX(double t) => _cx + Settings.axisAmplitudeM * Math.Cos(_k * (t - Settings.axisFirstBendZM));
        double AxisDx(double t) => -Settings.axisAmplitudeM * _k * Math.Sin(_k * (t - Settings.axisFirstBendZM));
        double AxisDxx(double t) => -Settings.axisAmplitudeM * _k * _k * Math.Cos(_k * (t - Settings.axisFirstBendZM));

        /// <summary>The axis point at parameter t, the unit tangent (toward north), and the unit normal (toward east).</summary>
        public void AxisFrame(double t, out double ax, out double tx, out double tz, out double nx, out double nz)
        {
            ax = AxisX(t);
            double dx = AxisDx(t);
            double len = Math.Sqrt(1.0 + dx * dx);
            tx = dx / len; tz = 1.0 / len;
            nx = 1.0 / len; nz = -dx / len;
        }

        /// <summary>The closest point of the axis. signedDistance is positive on the east side.</summary>
        public void Closest(double x, double z, out double t, out double signedDistance)
        {
            t = z;
            for (int i = 0; i < 6; i++)
            {
                double ax = AxisX(t), dx = AxisDx(t);
                double g = (ax - x) * dx + (t - z);
                double gp = dx * dx + (ax - x) * AxisDxx(t) + 1.0;
                if (gp < 0.25) gp = 0.25;
                t -= g / gp;
            }
            double ex = x - AxisX(t), ez = z - t;
            double d = Math.Sqrt(ex * ex + ez * ez);
            signedDistance = ex >= 0.0 ? d : -d;
        }

        // ---- cross-section ----

        static double Smoothstep(double a, double b, double x)
        {
            double u = Math.Min(1.0, Math.Max(0.0, (x - a) / (b - a)));
            return u * u * (3.0 - 2.0 * u);
        }

        /// <summary>1 inside the narrow section, 0 away from the narrow section.</summary>
        public double Narrow(double t)
        {
            double half = Settings.narrowLengthM * 0.5;
            return 1.0 - Smoothstep(half, half + Settings.narrowRampM, Math.Abs(t - Settings.narrowCenterZM));
        }

        public double FloorHalfWidth(double t) =>
            0.5 * (Settings.floorWidthM + (Settings.narrowFloorWidthM - Settings.floorWidthM) * Narrow(t));

        public double WallWidth(double t) =>
            Settings.wallWidthM + (Settings.narrowWallWidthM - Settings.wallWidthM) * Narrow(t);

        /// <summary>Elevation of the canyon floor at axis parameter t, full-scale m.</summary>
        public double FloorElevation(double t) =>
            Settings.floorElevationSouthM - Settings.floorDropM * t / FullExtentM;

        public Sample Evaluate(double x, double z)
        {
            Closest(x, z, out double t, out double sd);
            double d = Math.Abs(sd);
            double hw = FloorHalfWidth(t), w = WallWidth(t);
            double floor = FloorElevation(t);
            double relief = Settings.rimElevationM - floor;

            double h;
            bool outside = false;
            if (d <= hw)
            {
                h = 0.0;
            }
            else
            {
                double u = (d - hw) / w;
                if (u < 1.0)
                {
                    h = relief * (1.0 - Math.Pow(1.0 - u, Settings.wallExponent));
                }
                else
                {
                    outside = true;
                    h = relief - Settings.outerSlope * Math.Min(d - hw - w, Settings.outerRunM);
                }
            }

            // Saddle: a dip on the west rim. Narrow along the rim, wide across the rim.
            {
                double px = x - _saddleX, pz = z - _saddleZ;
                double along = px * _saddleTx + pz * _saddleTz;
                double across = px * _saddleNx + pz * _saddleNz;
                double e = along * along / (2.0 * Settings.saddleAlongM * Settings.saddleAlongM)
                    + across * across / (2.0 * Settings.saddleAcrossM * Settings.saddleAcrossM);
                if (e < 12.0 && h > 0.0) h = Math.Max(0.0, h - Settings.saddleDepthM * Math.Exp(-e));
            }

            // Side drainages and the chute: each one cuts a groove with a floor of constant grade.
            bool carved = false, inChute = false;
            double chuteCore = 0.0;
            for (int i = 0; i < _carves.Length; i++)
            {
                Carve c = _carves[i];
                double px = x - c.Ox, pz = z - c.Oz;
                double s = px * c.Dx + pz * c.Dz;
                if (s < -20.0 || s > c.MaxLen) continue;
                double across = Math.Abs(px * c.Dz - pz * c.Dx);
                double sPos = Math.Max(s, 0.0);
                double width = c.W0 + c.WGrow * sPos;
                if (across >= width) continue;
                double weight = Smoothstep(c.Inner, width, across);
                double groove = c.Grade * sPos;
                double cut = groove + (h - groove) * weight;
                if (cut < h - 0.5)
                {
                    carved = true;
                    if (c.IsChute)
                    {
                        inChute = true;
                        chuteCore = 1.0 - weight;
                    }
                }
                if (cut < h) h = cut;
            }

            double rel = outside && !carved ? 1.0 : Math.Min(1.0, Math.Max(0.0, h / relief));
            return new Sample
            {
                BaseElevation = floor + h,
                RelativeHeight = rel,
                AxisDistance = d,
                InChute = inChute,
                ChuteCore = chuteCore,
            };
        }

        // ---- feature positions, full-scale m ----

        /// <summary>The base of the chute: the point where the chute meets the canyon floor.</summary>
        public Vector2 ChuteBase => new Vector2((float)_carves[_carves.Length - 1].Ox, (float)_carves[_carves.Length - 1].Oz);
        /// <summary>The unit direction of the chute, pointing upslope.</summary>
        public Vector2 ChuteDirection => new Vector2((float)_carves[_carves.Length - 1].Dx, (float)_carves[_carves.Length - 1].Dz);
        public Vector2 SaddlePoint => new Vector2((float)_saddleX, (float)_saddleZ);
        /// <summary>The unit direction along the rim at the saddle.</summary>
        public Vector2 SaddleAlong => new Vector2((float)_saddleTx, (float)_saddleTz);
        /// <summary>The unit direction across the rim at the saddle, pointing toward the canyon.</summary>
        public Vector2 SaddleAcross => new Vector2((float)_saddleNx, (float)_saddleNz);

        /// <summary>The default ignition: on the canyon floor, 200 m from the base of the chute.</summary>
        public Vector2 DefaultIgnition
        {
            get
            {
                // South of the chute base and between the creek and the east wall.
                Vector2 b = ChuteBase;
                Vector2 dir = new Vector2(-0.28f, -0.96f).normalized;
                return b + dir * 200f;
            }
        }

        // ---- world conversion ----

        public double ToWorldElevation(double fullElevation) => ScaleDatumM + Scale * (fullElevation - ScaleDatumM);
        public float ToWorld(double fullLength) => (float)(fullLength * Scale);

        public Vector3 WorldPoint(double x, double fullElevation, double z) =>
            new Vector3((float)(x * Scale), (float)ToWorldElevation(fullElevation), (float)(z * Scale));
    }
}
