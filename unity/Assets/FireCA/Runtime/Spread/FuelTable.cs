using System;
using System.Runtime.InteropServices;

namespace FireCA
{
    /// <summary>The parameters of one Anderson (1982) fuel model. Loads in tons per acre, SAV in 1/ft, depth in ft.</summary>
    public readonly struct FuelModel
    {
        public readonly int Code;
        public readonly string Name;
        public readonly double Load1h, Load10h, Load100h, LoadLive;
        public readonly double Sav1h, SavLive;
        public readonly double DepthFt;
        public readonly double MxDeadPct;

        public FuelModel(int code, string name, double load1h, double load10h, double load100h, double loadLive,
            double sav1h, double savLive, double depthFt, double mxDeadPct)
        {
            Code = code; Name = name;
            Load1h = load1h; Load10h = load10h; Load100h = load100h; LoadLive = loadLive;
            Sav1h = sav1h; SavLive = savLive; DepthFt = depthFt; MxDeadPct = mxDeadPct;
        }
    }

    /// <summary>The fuel constants of one fuel code, in the native units of the Rothermel model (ft, lb, min, Btu).</summary>
    public sealed class FuelConstants
    {
        public int Code;
        public bool Burnable;
        public double DepthFt;
        public double Sigma;
        public double MxDead;
        public bool HasLive;
        public double[] DeadMoistureW = new double[3];
        public double[] FineMoistureW = new double[3];
        public double LiveRatioW;
        public double IrDeadCoef;
        public double IrLiveCoef;
        public double Xi;
        public double[] SinkDeadW = new double[3];
        public double SinkLiveW;
        public double WindC, WindB, WindK;
        public double SlopeK;
        public double ResidenceMin;

        /// <summary>The unsheltered wind adjustment factor: midflame wind over the wind at 6.1 m (20 ft).</summary>
        public double WindAdjustmentFactor => 1.83 / Math.Log((20.0 + 0.36 * DepthFt) / (0.13 * DepthFt));
    }

    /// <summary>One entry of the fuel constants buffer on the GPU. The layout matches FuelConstantsGpu in FuelTable.hlsl.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FuelConstantsGpu
    {
        public float burnable, depthFt, mxDead, hasLive;
        public float deadMoistureW0, deadMoistureW1, deadMoistureW2, liveRatioW;
        public float fineMoistureW0, fineMoistureW1, fineMoistureW2, irDeadCoef;
        public float sinkDeadW0, sinkDeadW1, sinkDeadW2, sinkLiveW;
        public float irLiveCoef, xi, windC, windB;
        public float windK, slopeK, residenceMin, pad;

        public const int Stride = 24 * sizeof(float);
    }

    /// <summary>
    /// The Fuel Table: the 13 Anderson fuel models and the constants computed from the parameters.
    /// The class has no Unity dependency. The Reference Model and the GPU upload share this implementation.
    /// </summary>
    public static class FuelTable
    {
        public const double RhoP = 32.0;       // particle density, lb/ft3
        public const double ST = 0.0555;       // total mineral content
        public const double SE = 0.010;        // effective mineral content
        public const double Heat = 8000.0;     // heat content, Btu/lb
        public const double Sav10h = 109.0;    // 1/ft
        public const double Sav100h = 30.0;    // 1/ft
        public const double TonsPerAcreToLbPerFt2 = 2000.0 / 43560.0;

        /// <summary>The number of entries of the fuel constants buffer. The fuel code is the index.</summary>
        public const int EntryCount = 256;

        public static readonly FuelModel[] Models =
        {
            new FuelModel(1, "Short grass", 0.74, 0.00, 0.00, 0.00, 3500, 1500, 1.0, 12),
            new FuelModel(2, "Timber grass and understory", 2.00, 1.00, 0.50, 0.50, 3000, 1500, 1.0, 15),
            new FuelModel(3, "Tall grass", 3.01, 0.00, 0.00, 0.00, 1500, 1500, 2.5, 25),
            new FuelModel(4, "Chaparral", 5.01, 4.01, 2.00, 5.01, 2000, 1500, 6.0, 20),
            new FuelModel(5, "Brush", 1.00, 0.50, 0.00, 2.00, 2000, 1500, 2.0, 20),
            new FuelModel(6, "Dormant brush", 1.50, 2.50, 2.00, 0.00, 1750, 1500, 2.5, 25),
            new FuelModel(7, "Southern rough", 1.13, 1.87, 1.50, 0.37, 1750, 1550, 2.5, 40),
            new FuelModel(8, "Closed timber litter", 1.50, 1.00, 2.50, 0.00, 2000, 1500, 0.2, 30),
            new FuelModel(9, "Hardwood litter", 2.92, 0.41, 0.15, 0.00, 2500, 1500, 0.2, 25),
            new FuelModel(10, "Timber litter and understory", 3.01, 2.00, 5.01, 2.00, 2000, 1500, 1.0, 25),
            new FuelModel(11, "Light slash", 1.50, 4.51, 5.51, 0.00, 1500, 1500, 1.0, 15),
            new FuelModel(12, "Medium slash", 4.01, 14.03, 16.53, 0.00, 1500, 1500, 2.3, 20),
            new FuelModel(13, "Heavy slash", 7.01, 23.04, 28.05, 0.00, 1500, 1500, 3.0, 25),
        };

        static readonly FuelConstants[] Cache = BuildCache();

        static FuelConstants[] BuildCache()
        {
            var cache = new FuelConstants[EntryCount];
            for (int code = 0; code < EntryCount; code++)
                cache[code] = code >= 1 && code <= Models.Length ? Compute(Models[code - 1]) : new FuelConstants { Code = code };
            return cache;
        }

        /// <summary>The fuel constants of a fuel code. A code that is not burnable has Burnable set to false.</summary>
        public static FuelConstants Get(int code) => Cache[code & 0xFF];

        /// <summary>The equations of docs/02-fire-spread.md, section "Fuel constants".</summary>
        public static FuelConstants Compute(FuelModel m)
        {
            double[] w = { m.Load1h * TonsPerAcreToLbPerFt2, m.Load10h * TonsPerAcreToLbPerFt2, m.Load100h * TonsPerAcreToLbPerFt2 };
            double[] s = { m.Sav1h, Sav10h, Sav100h };
            double wl = m.LoadLive * TonsPerAcreToLbPerFt2, sl = m.SavLive;

            double[] a = new double[3];
            double aDead = 0.0;
            for (int j = 0; j < 3; j++) { a[j] = s[j] * w[j] / RhoP; aDead += a[j]; }
            double aLive = sl * wl / RhoP;
            double aTotal = aDead + aLive;
            double[] f = new double[3];
            for (int j = 0; j < 3; j++) f[j] = a[j] / aDead;
            double fDead = aDead / aTotal, fLive = aLive / aTotal;

            double sigmaDead = 0.0, wnDead = 0.0;
            for (int j = 0; j < 3; j++)
            {
                sigmaDead += f[j] * s[j];
                wnDead += f[j] * w[j] * (1.0 - ST);
            }
            double sigma = fDead * sigmaDead + fLive * sl;
            double wnLive = wl * (1.0 - ST);
            double rhoB = (w[0] + w[1] + w[2] + wl) / m.DepthFt;
            double beta = rhoB / RhoP;
            double betaOp = 3.348 * Math.Pow(sigma, -0.8189);
            double ratio = beta / betaOp;
            double gammaMax = Math.Pow(sigma, 1.5) / (495.0 + 0.0594 * Math.Pow(sigma, 1.5));
            double aExp = 133.0 * Math.Pow(sigma, -0.7913);
            double gamma = gammaMax * Math.Pow(ratio, aExp) * Math.Exp(aExp * (1.0 - ratio));
            double etaS = Math.Min(1.0, 0.174 * Math.Pow(SE, -0.19));
            double[] eps = { Math.Exp(-138.0 / s[0]), Math.Exp(-138.0 / s[1]), Math.Exp(-138.0 / s[2]) };
            double epsLive = Math.Exp(-138.0 / sl);
            double fineDen = w[0] * eps[0] + w[1] * eps[1] + w[2] * eps[2];
            double windE = 0.715 * Math.Exp(-3.59e-4 * sigma);

            var k = new FuelConstants
            {
                Code = m.Code,
                Burnable = true,
                DepthFt = m.DepthFt,
                Sigma = sigma,
                MxDead = m.MxDeadPct / 100.0,
                HasLive = wl > 0.0,
                LiveRatioW = wl > 0.0 ? fineDen / (wl * Math.Exp(-500.0 / sl)) : 0.0,
                IrDeadCoef = gamma * wnDead * Heat * etaS,
                IrLiveCoef = gamma * wnLive * Heat * etaS,
                Xi = Math.Exp((0.792 + 0.681 * Math.Sqrt(sigma)) * (beta + 0.1)) / (192.0 + 0.2595 * sigma),
                SinkLiveW = rhoB * fLive * epsLive,
                WindC = 7.47 * Math.Exp(-0.133 * Math.Pow(sigma, 0.55)),
                WindB = 0.02526 * Math.Pow(sigma, 0.54),
                WindK = Math.Pow(ratio, -windE),
                SlopeK = 5.275 * Math.Pow(beta, -0.3),
                ResidenceMin = 384.0 / sigma,
            };
            for (int j = 0; j < 3; j++)
            {
                k.DeadMoistureW[j] = f[j];
                k.FineMoistureW[j] = w[j] * eps[j] / fineDen;
                k.SinkDeadW[j] = rhoB * fDead * f[j] * eps[j];
            }
            return k;
        }

        /// <summary>The 256 entries of the fuel constants buffer.</summary>
        public static FuelConstantsGpu[] BuildGpuTable()
        {
            var table = new FuelConstantsGpu[EntryCount];
            for (int code = 0; code < EntryCount; code++)
            {
                FuelConstants k = Cache[code];
                if (!k.Burnable) continue;
                table[code] = new FuelConstantsGpu
                {
                    burnable = 1f,
                    depthFt = (float)k.DepthFt,
                    mxDead = (float)k.MxDead,
                    hasLive = k.HasLive ? 1f : 0f,
                    deadMoistureW0 = (float)k.DeadMoistureW[0], deadMoistureW1 = (float)k.DeadMoistureW[1], deadMoistureW2 = (float)k.DeadMoistureW[2],
                    liveRatioW = (float)k.LiveRatioW,
                    fineMoistureW0 = (float)k.FineMoistureW[0], fineMoistureW1 = (float)k.FineMoistureW[1], fineMoistureW2 = (float)k.FineMoistureW[2],
                    irDeadCoef = (float)k.IrDeadCoef,
                    sinkDeadW0 = (float)k.SinkDeadW[0], sinkDeadW1 = (float)k.SinkDeadW[1], sinkDeadW2 = (float)k.SinkDeadW[2],
                    sinkLiveW = (float)k.SinkLiveW,
                    irLiveCoef = (float)k.IrLiveCoef,
                    xi = (float)k.Xi,
                    windC = (float)k.WindC,
                    windB = (float)k.WindB,
                    windK = (float)k.WindK,
                    slopeK = (float)k.SlopeK,
                    residenceMin = (float)k.ResidenceMin,
                };
            }
            return table;
        }
    }
}
