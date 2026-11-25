using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace FireCA.Tests
{
    /// <summary>M1 acceptance criterion 1: the two EditMode tests of docs/02-fire-spread.md.</summary>
    public sealed class ReferenceModelTests
    {
        [Serializable]
        public sealed class CaseFile
        {
            public string units;
            public Case[] cases;
        }

        [Serializable]
        public sealed class Case
        {
            public CaseIn @in;
            public CaseOut @out;
        }

        [Serializable]
        public sealed class CaseIn
        {
            public int fuel;
            public double m1, m10, m100, mLive, midflameWindMps, windToDeg, slope, upslopeDeg;
        }

        [Serializable]
        public sealed class CaseOut
        {
            public double r0Mps, rHeadMps, headDeg, effWindMps, lengthToBreadth, eccentricity, reactionKwM2, residenceS, flameLengthM;
        }

        public static string ReferenceCasesPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "tools", "reference_cases.json"));

        public static Case[] LoadCases()
        {
            Assert.IsTrue(File.Exists(ReferenceCasesPath), $"The Reference Cases file is not at {ReferenceCasesPath}.");
            var file = JsonUtility.FromJson<CaseFile>(File.ReadAllText(ReferenceCasesPath));
            Assert.IsNotNull(file.cases);
            return file.cases;
        }

        // Within 0.1 percent, or within 0.000001 absolute.
        static void AssertMatch(double expected, double actual, int index, string name)
        {
            double tolerance = Math.Max(1e-6, Math.Abs(expected) * 1e-3);
            if (Math.Abs(actual - expected) > tolerance)
                Assert.Fail($"Reference Case {index}, {name}: expected {expected}, the Reference Model gives {actual}.");
        }

        [Test]
        public void ReferenceModel_MatchesEveryReferenceCase()
        {
            Case[] cases = LoadCases();
            Assert.AreEqual(468, cases.Length, "The Reference Cases file must hold 468 cases.");
            for (int i = 0; i < cases.Length; i++)
            {
                CaseIn c = cases[i].@in;
                CaseOut e = cases[i].@out;
                FireBehavior b = ReferenceModel.Spread(c.fuel, c.m1, c.m10, c.m100, c.mLive, c.midflameWindMps, c.windToDeg, c.slope, c.upslopeDeg);
                AssertMatch(e.r0Mps, b.R0Mps, i, "r0Mps");
                AssertMatch(e.rHeadMps, b.RHeadMps, i, "rHeadMps");
                // An azimuth of 359.9999 equals an azimuth of 0.
                double headError = Math.Abs(b.HeadDeg - e.headDeg);
                headError = Math.Min(headError, 360.0 - headError);
                if (headError > Math.Max(1e-6, Math.Abs(e.headDeg) * 1e-3))
                    Assert.Fail($"Reference Case {i}, headDeg: expected {e.headDeg}, the Reference Model gives {b.HeadDeg}.");
                AssertMatch(e.effWindMps, b.EffWindMps, i, "effWindMps");
                AssertMatch(e.lengthToBreadth, b.LengthToBreadth, i, "lengthToBreadth");
                AssertMatch(e.eccentricity, b.Eccentricity, i, "eccentricity");
                AssertMatch(e.reactionKwM2, b.ReactionKwM2, i, "reactionKwM2");
                AssertMatch(e.residenceS, b.ResidenceS, i, "residenceS");
                AssertMatch(e.flameLengthM, b.FlameLengthM, i, "flameLengthM");
            }
        }

        [Test]
        public void ReferenceModel_HeadRateIsZeroAtTheMoistureOfExtinction()
        {
            int tested = 0;
            foreach (FuelModel model in FuelTable.Models)
            {
                if (model.LoadLive > 0.0) continue;
                double mx = model.MxDeadPct / 100.0;
                foreach (double wind in new[] { 0.0, 5.0 })
                {
                    FireBehavior b = ReferenceModel.Spread(model.Code, mx, mx, mx, 1.0, wind, 90.0, 0.3, 0.0);
                    Assert.AreEqual(0.0, b.RHeadMps, $"Fuel model {model.Code} must not spread at the moisture of extinction.");
                    Assert.AreEqual(0.0, b.ReactionKwM2);
                }
                tested++;
            }
            Assert.Greater(tested, 0);
        }

        [Test]
        public void FuelTable_MatchesThePublishedCheck()
        {
            // docs/02-fire-spread.md, "Published check": midflame wind 5 mi/h, dead moisture 8 percent, live moisture 100 percent.
            double[] chainsPerHour = { 81.1, 32.5, 102.1, 71.4, 14.5, 29.6, 25.9, 1.8, 7.6, 7.9, 5.6, 11.9, 14.2 };
            double[] flameFt = { 4.1, 5.9, 12.1, 18.7, 3.8, 5.6, 5.5, 1.0, 2.7, 4.8, 3.3, 7.6, 9.7 };
            for (int code = 1; code <= 13; code++)
            {
                FireBehavior b = ReferenceModel.Spread(code, 0.08, 0.08, 0.08, 1.0, 5.0 * 88.0 / ReferenceModel.MpsToFtPerMin, 0.0, 0.0, 0.0);
                double ros = b.RHeadMps / ReferenceModel.FtPerMinToMps * 60.0 / 66.0;
                double flame = b.FlameLengthM / ReferenceModel.FtToM;
                Assert.AreEqual(chainsPerHour[code - 1], ros, 0.06, $"Fuel model {code}: rate of spread in chains per hour.");
                Assert.AreEqual(flameFt[code - 1], flame, 0.06, $"Fuel model {code}: flame length in ft.");
            }
        }

        [Test]
        public void FuelTable_GpuTableHas256Entries_AndOnlyTheFuelModelsBurn()
        {
            FuelConstantsGpu[] table = FuelTable.BuildGpuTable();
            Assert.AreEqual(256, table.Length);
            Assert.AreEqual(FuelConstantsGpu.Stride, System.Runtime.InteropServices.Marshal.SizeOf<FuelConstantsGpu>());
            for (int code = 0; code < 256; code++)
                Assert.AreEqual(code >= 1 && code <= 13 ? 1f : 0f, table[code].burnable, $"Fuel code {code}.");
        }
    }
}
