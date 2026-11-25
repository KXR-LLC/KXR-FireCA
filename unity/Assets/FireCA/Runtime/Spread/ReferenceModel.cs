using System;

namespace FireCA
{
    /// <summary>The fire behavior of one cell, in SI units.</summary>
    public struct FireBehavior
    {
        /// <summary>No-wind, no-slope rate of spread, m/s.</summary>
        public double R0Mps;
        /// <summary>Head rate of spread, m/s.</summary>
        public double RHeadMps;
        /// <summary>Head direction, degrees clockwise from north. 0 when the fire has no head.</summary>
        public double HeadDeg;
        public double EffWindMps;
        public double LengthToBreadth;
        public double Eccentricity;
        public double ReactionKwM2;
        public double ResidenceS;
        public double FlameLengthM;

        /// <summary>The rate of spread at an angle from the head direction, with the ignition point at the rear focus.</summary>
        public double RateAtAngle(double thetaDeg) =>
            RHeadMps * (1.0 - Eccentricity) / (1.0 - Eccentricity * Math.Cos(thetaDeg * Math.PI / 180.0));
    }

    /// <summary>
    /// The Reference Model: the CPU implementation of the Rothermel equations of docs/02-fire-spread.md.
    /// The core runs in the native units of the model (ft, lb, min, Btu) and converts to SI at the boundary.
    /// The class has no Unity dependency. tools/rothermel_reference.py is the numeric oracle.
    /// </summary>
    public static class ReferenceModel
    {
        public const double FtPerMinToMps = 0.00508;
        public const double BtuFt2MinToKwM2 = 0.189276;
        public const double FtToM = 0.3048;
        public const double MphToFtPerMin = 88.0;
        public const double MpsToFtPerMin = 196.850394;

        /// <summary>The moisture damping coefficient. The value is 0 at the moisture of extinction and above.</summary>
        public static double Damping(double r)
        {
            // The guard absorbs the rounding of the weighted moisture sum.
            if (r >= 1.0 - 1e-12) return 0.0;
            if (r < 0.0) r = 0.0;
            return Math.Max(0.0, 1.0 - 2.59 * r + 5.11 * r * r - 3.52 * r * r * r);
        }

        /// <summary>
        /// Head fire behavior of one cell. Moisture values are fractions. windToDeg is the azimuth the wind blows toward.
        /// slope is rise over run. upslopeDeg is the azimuth of the upslope direction.
        /// </summary>
        public static FireBehavior Spread(int fuelCode, double m1, double m10, double m100, double mLive,
            double midflameWindMps, double windToDeg, double slope, double upslopeDeg, bool applyWindLimit = true)
        {
            FuelConstants k = FuelTable.Get(fuelCode);
            if (!k.Burnable) return default;

            double[] md = { m1, m10, m100 };
            double mDead = 0.0, mFine = 0.0, sink = 0.0;
            for (int j = 0; j < 3; j++)
            {
                mDead += k.DeadMoistureW[j] * md[j];
                mFine += k.FineMoistureW[j] * md[j];
                sink += k.SinkDeadW[j] * (250.0 + 1116.0 * md[j]);
            }
            sink += k.SinkLiveW * (250.0 + 1116.0 * mLive);

            double etaDead = Damping(mDead / k.MxDead);
            double etaLive = 0.0;
            if (k.HasLive)
            {
                double mxLive = Math.Max(k.MxDead, 2.9 * k.LiveRatioW * (1.0 - mFine / k.MxDead) - 0.226);
                etaLive = Damping(mLive / mxLive);
            }

            double ir = k.IrDeadCoef * etaDead + k.IrLiveCoef * etaLive;      // Btu/ft2/min
            double r0 = ir * k.Xi / sink;                                      // ft/min

            double u = midflameWindMps * MpsToFtPerMin;                        // ft/min
            double phiW = u > 0.0 ? k.WindC * Math.Pow(u, k.WindB) * k.WindK : 0.0;
            double phiS = k.SlopeK * slope * slope;

            double wr = windToDeg * Math.PI / 180.0, sr = upslopeDeg * Math.PI / 180.0;
            double vx = phiW * Math.Sin(wr) + phiS * Math.Sin(sr);             // east
            double vz = phiW * Math.Cos(wr) + phiS * Math.Cos(sr);             // north
            double phiE = Math.Sqrt(vx * vx + vz * vz);
            double headDeg = 0.0;
            if (phiE > 0.0)
            {
                headDeg = Math.Atan2(vx, vz) * 180.0 / Math.PI;
                headDeg %= 360.0;
                if (headDeg < 0.0) headDeg += 360.0;
            }
            double uEff = phiE > 0.0 ? Math.Pow(phiE / (k.WindC * k.WindK), 1.0 / k.WindB) : 0.0;
            if (applyWindLimit && uEff > 0.9 * ir)
            {
                uEff = 0.9 * ir;
                phiE = uEff > 0.0 ? k.WindC * Math.Pow(uEff, k.WindB) * k.WindK : 0.0;
            }

            double rHead = r0 * (1.0 + phiE);
            double lb = Math.Min(8.0, 1.0 + 0.25 * (uEff / MphToFtPerMin));
            double ecc = Math.Sqrt(lb * lb - 1.0) / lb;
            double byram = ir * k.ResidenceMin * rHead / 60.0;                 // Btu/ft/s
            double flameFt = byram > 0.0 ? 0.45 * Math.Pow(byram, 0.46) : 0.0;

            return new FireBehavior
            {
                R0Mps = r0 * FtPerMinToMps,
                RHeadMps = rHead * FtPerMinToMps,
                HeadDeg = headDeg,
                EffWindMps = uEff / MpsToFtPerMin,
                LengthToBreadth = lb,
                Eccentricity = ecc,
                ReactionKwM2 = ir * BtuFt2MinToKwM2,
                ResidenceS = k.ResidenceMin * 60.0,
                FlameLengthM = flameFt * FtToM,
            };
        }
    }
}
