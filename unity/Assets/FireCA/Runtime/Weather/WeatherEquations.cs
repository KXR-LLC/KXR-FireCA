using System;

namespace FireCA
{
    /// <summary>
    /// The weather equations of docs/03-weather-wind.md on the CPU. Weather.hlsl holds the same equations for the GPU.
    /// The class has no Unity dependency.
    /// </summary>
    public static class WeatherEquations
    {
        public const double SolarConstantWm2 = 1361.0;
        public const double Tau1hS = 3600.0;
        public const double Tau10hS = 36000.0;
        public const double Tau100hS = 360000.0;
        public const double MinMoisture1h = 0.01;
        public const double MaxMoisture1h = 0.35;
        /// <summary>The fuel moisture equations use a midflame wind of 0.4 times the Surface Wind speed.</summary>
        public const double MoistureWindFactor = 0.4;

        /// <summary>Local solar time, hours from 0 to 24.</summary>
        public static double SolarHour(double startSolarHour, double simClockS)
        {
            double h = (startSolarHour + simClockS / 3600.0) % 24.0;
            return h < 0.0 ? h + 24.0 : h;
        }

        /// <summary>Air temperature at referenceElevationM, deg C. The temperature peaks at 15:00 solar time.</summary>
        public static double ReferenceTempC(double tempMaxC, double tempMinC, double solarHour)
        {
            double mean = 0.5 * (tempMaxC + tempMinC), amp = 0.5 * (tempMaxC - tempMinC);
            return mean + amp * Math.Cos(2.0 * Math.PI * (solarHour - 15.0) / 24.0);
        }

        public static double AirTempC(double referenceTempC, double lapseRateCPerKm, double elevationM, double referenceElevationM) =>
            referenceTempC - lapseRateCPerKm * (elevationM - referenceElevationM) / 1000.0;

        /// <summary>Saturation vapor pressure, hPa. T in deg C.</summary>
        public static double SaturationVaporPressure(double tC) => 6.112 * Math.Exp(17.67 * tC / (tC + 243.5));

        public static double VaporPressure(double rhMinPct, double tempMaxC) => rhMinPct / 100.0 * SaturationVaporPressure(tempMaxC);

        public static double RelativeHumidityPct(double vaporPressureHPa, double airTempC) =>
            Math.Min(100.0, Math.Max(1.0, 100.0 * vaporPressureHPa / SaturationVaporPressure(airTempC)));

        /// <summary>The sun direction, a unit vector in world axes (X east, Y up, Z north).</summary>
        public static void SunDirection(double latitudeDeg, int dayOfYear, double solarHour, out double x, out double y, out double z)
        {
            const double deg = Math.PI / 180.0;
            double decl = 23.44 * deg * Math.Sin(2.0 * Math.PI * (284 + dayOfYear) / 365.0);
            double hourAngle = 15.0 * deg * (solarHour - 12.0);
            double lat = latitudeDeg * deg;
            y = Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Cos(hourAngle);
            x = -Math.Cos(decl) * Math.Sin(hourAngle);
            z = Math.Cos(lat) * Math.Sin(decl) - Math.Sin(lat) * Math.Cos(decl) * Math.Cos(hourAngle);
        }

        /// <summary>The sun elevation above the horizon, degrees.</summary>
        public static double SunElevationDeg(double latitudeDeg, int dayOfYear, double solarHour)
        {
            SunDirection(latitudeDeg, dayOfYear, solarHour, out _, out double y, out _);
            return Math.Asin(Math.Min(1.0, Math.Max(-1.0, y))) * 180.0 / Math.PI;
        }

        /// <summary>Direct normal irradiance, W/m2. sunY is the sine of the sun elevation.</summary>
        public static double DirectNormalIrradiance(double sunY) =>
            sunY > 0.02 ? SolarConstantWm2 * Math.Pow(0.75, 1.0 / sunY) : 0.0;

        /// <summary>Equilibrium moisture content, percent. H is relative humidity (percent). T is temperature (deg F).</summary>
        public static double EmcPct(double h, double t)
        {
            if (h < 10.0) return 0.03229 + 0.281073 * h - 0.000578 * h * t;
            if (h < 50.0) return 2.22749 + 0.160107 * h - 0.014784 * t;
            return 21.0606 + 0.005565 * h * h - 0.00035 * h * t - 0.483199 * h;
        }

        /// <summary>Equilibrium moisture content of the 1-hour fuel at the fuel surface, fraction.</summary>
        public static double FuelLevelEmc(double airTempC, double rhPct, double insolationWm2, double midflameMps)
        {
            double taF = airTempC * 9.0 / 5.0 + 32.0;
            double iCal = insolationWm2 / 697.8;
            double uMph = midflameMps * 2.23694;
            double tfF = taF + iCal / (0.015 * uMph + 0.026);
            double rhF = rhPct * Math.Exp(-0.033 * (tfF - taF));
            return EmcPct(rhF, tfF) / 100.0;
        }

        /// <summary>Moves a moisture value toward the equilibrium value over a time step.</summary>
        public static double Relax(double moisture, double emc, double dtS, double tauS) =>
            moisture + (emc - moisture) * (1.0 - Math.Exp(-dtS / tauS));
    }
}
