using NUnit.Framework;

namespace FireCA.Tests
{
    /// <summary>EditMode tests of the weather equations of docs/03-weather-wind.md. Covers M2 acceptance criterion 1.</summary>
    public sealed class WeatherEquationsTests
    {
        [Test]
        public void Criterion1_SunElevationAtSolarNoonIs71Degrees()
        {
            double elevation = WeatherEquations.SunElevationDeg(37.0, 213, 12.0);
            UnityEngine.Debug.Log($"[FireCA test] Sun elevation at solar noon, latitude 37, day 213: {elevation:F2} degrees.");
            Assert.AreEqual(71.0, elevation, 1.0);
        }

        [Test]
        public void SunDirection_IsAUnitVector_AndMovesFromEastToWest()
        {
            for (double hour = 0.0; hour < 24.0; hour += 0.5)
            {
                WeatherEquations.SunDirection(37.0, 213, hour, out double x, out double y, out double z);
                Assert.AreEqual(1.0, x * x + y * y + z * z, 1e-9, $"Hour {hour}.");
            }
            WeatherEquations.SunDirection(37.0, 213, 7.0, out double mx, out double my, out _);
            Assert.Greater(mx, 0.5, "The morning sun is in the east (+X).");
            Assert.Greater(my, 0.0);
            WeatherEquations.SunDirection(37.0, 213, 12.0, out double nx, out _, out double nz);
            Assert.AreEqual(0.0, nx, 1e-9, "The noon sun is on the meridian.");
            Assert.Less(nz, 0.0, "At latitude 37 the noon sun is in the south (-Z).");
            WeatherEquations.SunDirection(37.0, 213, 17.0, out double ex, out _, out _);
            Assert.Less(ex, -0.5, "The evening sun is in the west (-X).");
            WeatherEquations.SunDirection(37.0, 213, 0.0, out _, out double midnightY, out _);
            Assert.Less(midnightY, 0.0, "The sun is under the horizon at midnight.");
            Assert.AreEqual(0.0, WeatherEquations.DirectNormalIrradiance(midnightY));
        }

        [Test]
        public void DirectNormalIrradiance_FollowsTheEquation()
        {
            Assert.AreEqual(1361.0 * 0.75, WeatherEquations.DirectNormalIrradiance(1.0), 1e-9);
            Assert.AreEqual(1361.0 * System.Math.Pow(0.75, 2.0), WeatherEquations.DirectNormalIrradiance(0.5), 1e-9);
            Assert.AreEqual(0.0, WeatherEquations.DirectNormalIrradiance(0.02));
        }

        [Test]
        public void AirTemperature_PeaksAt15_AndFallsWithElevation()
        {
            Assert.AreEqual(35.0, WeatherEquations.ReferenceTempC(35.0, 16.0, 15.0), 1e-9);
            Assert.AreEqual(16.0, WeatherEquations.ReferenceTempC(35.0, 16.0, 3.0), 1e-9);
            Assert.AreEqual(25.5, WeatherEquations.ReferenceTempC(35.0, 16.0, 9.0), 1e-9);
            Assert.AreEqual(35.0 - 6.5, WeatherEquations.AirTempC(35.0, 6.5, 2200.0, 1200.0), 1e-9);
            Assert.AreEqual(1.0, WeatherEquations.SolarHour(13.0, 12.0 * 3600.0), 1e-9);
        }

        [Test]
        public void RelativeHumidity_EqualsRhMinAtTempMax_AndRisesWhenTheAirCools()
        {
            double e = WeatherEquations.VaporPressure(12.0, 35.0);
            Assert.AreEqual(12.0, WeatherEquations.RelativeHumidityPct(e, 35.0), 1e-9);
            double night = WeatherEquations.RelativeHumidityPct(e, 16.0);
            Assert.That(night, Is.InRange(30.0, 45.0));
            Assert.AreEqual(100.0, WeatherEquations.RelativeHumidityPct(e, -20.0), "The value clamps at 100 percent.");
            Assert.AreEqual(6.112, WeatherEquations.SaturationVaporPressure(0.0), 1e-9);
        }

        [Test]
        public void Emc_UsesTheThreeHumidityRanges()
        {
            Assert.AreEqual(0.03229 + 0.281073 * 5.0 - 0.000578 * 5.0 * 90.0, WeatherEquations.EmcPct(5.0, 90.0), 1e-12);
            Assert.AreEqual(2.22749 + 0.160107 * 30.0 - 0.014784 * 70.0, WeatherEquations.EmcPct(30.0, 70.0), 1e-12);
            Assert.AreEqual(21.0606 + 0.005565 * 6400.0 - 0.00035 * 80.0 * 60.0 - 0.483199 * 80.0, WeatherEquations.EmcPct(80.0, 60.0), 1e-12);
            // The three ranges of the source equations do not join exactly. The steps are 0.27 and 0.36 percent at 80 deg F.
            Assert.AreEqual(WeatherEquations.EmcPct(9.999, 80.0), WeatherEquations.EmcPct(10.0, 80.0), 0.5);
            Assert.AreEqual(WeatherEquations.EmcPct(49.999, 80.0), WeatherEquations.EmcPct(50.0, 80.0), 0.5);
        }

        [Test]
        public void FuelLevelEmc_SunlightDriesTheFuel_AndRelaxMovesTowardTheEmc()
        {
            double shade = WeatherEquations.FuelLevelEmc(35.0, 12.0, 0.0, 1.6);
            double sun = WeatherEquations.FuelLevelEmc(35.0, 12.0, 900.0, 1.6);
            Assert.Less(sun, shade);
            Assert.That(sun, Is.InRange(0.005, 0.03));
            Assert.That(shade, Is.InRange(0.02, 0.05));

            double m = WeatherEquations.Relax(0.05, 0.02, 3600.0, 3600.0);
            Assert.AreEqual(0.02 + 0.03 / System.Math.E, m, 1e-12);
            Assert.AreEqual(0.05, WeatherEquations.Relax(0.05, 0.02, 0.0, 3600.0), 1e-15);
        }
    }
}
