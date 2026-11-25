// The weather functions of docs/03-weather-wind.md. The Weather Model kernels and the terrain shader both call
// these functions, so the Air temperature Overlay and the Relative humidity Overlay show the values that the model uses.
// WeatherEquations.cs holds the same equations for the CPU.
#ifndef FIRECA_WEATHER_INCLUDED
#define FIRECA_WEATHER_INCLUDED

// Local solar time, hours from 0 to 24.
float FireCA_SolarHour(float startSolarHour, float simClockS)
{
    float h = fmod(startSolarHour + simClockS / 3600.0, 24.0);
    return h < 0.0 ? h + 24.0 : h;
}

// Air temperature at referenceElevationM, deg C. The temperature peaks at 15:00 solar time.
float FireCA_ReferenceTempC(float tempMaxC, float tempMinC, float solarHour)
{
    float tMean = 0.5 * (tempMaxC + tempMinC);
    float tAmp = 0.5 * (tempMaxC - tempMinC);
    return tMean + tAmp * cos(6.28318530718 * (solarHour - 15.0) / 24.0);
}

// Air temperature of a cell, deg C.
float FireCA_AirTempC(float referenceTempC, float lapseRateCPerKm, float elevationM, float referenceElevationM)
{
    return referenceTempC - lapseRateCPerKm * (elevationM - referenceElevationM) / 1000.0;
}

// Saturation vapor pressure, hPa. T in deg C.
float FireCA_SaturationVaporPressure(float tC)
{
    return 6.112 * exp(17.67 * tC / (tC + 243.5));
}

// The water vapor pressure of the day, hPa. The Weather Model holds the value constant through the day.
float FireCA_VaporPressure(float rhMinPct, float tempMaxC)
{
    return rhMinPct / 100.0 * FireCA_SaturationVaporPressure(tempMaxC);
}

// Relative humidity of a cell, percent.
float FireCA_RelativeHumidityPct(float vaporPressureHPa, float airTempC)
{
    return clamp(100.0 * vaporPressureHPa / FireCA_SaturationVaporPressure(airTempC), 1.0, 100.0);
}

// The weather inputs that the kernels and the terrain shader share.
// a: startSolarHour, weather time (s), tempMaxC, tempMinC.  b: rhMinPct, lapseRateCPerKm, referenceElevationM, unused.
float FireCA_CellAirTempC(float4 a, float4 b, float elevationM)
{
    float tRef = FireCA_ReferenceTempC(a.z, a.w, FireCA_SolarHour(a.x, a.y));
    return FireCA_AirTempC(tRef, b.y, elevationM, b.z);
}

float FireCA_CellRelativeHumidityPct(float4 a, float4 b, float elevationM)
{
    return FireCA_RelativeHumidityPct(FireCA_VaporPressure(b.x, a.z), FireCA_CellAirTempC(a, b, elevationM));
}

// Equilibrium moisture content, percent. H is relative humidity (percent). T is temperature (deg F).
float FireCA_EmcPct(float H, float T)
{
    if (H < 10.0)
        return 0.03229 + 0.281073 * H - 0.000578 * H * T;
    if (H < 50.0)
        return 2.22749 + 0.160107 * H - 0.014784 * T;
    return 21.0606 + 0.005565 * H * H - 0.00035 * H * T - 0.483199 * H;
}

// Equilibrium moisture content of the 1-hour fuel at the fuel surface, fraction.
// Sunlight heats the fuel and dries the air at the fuel surface.
float FireCA_FuelLevelEmc(float airTempC, float rhPct, float insolationWm2, float midflameMps)
{
    float taF = airTempC * 9.0 / 5.0 + 32.0;
    float iCal = insolationWm2 / 697.8;                         // cal/cm2/min
    float uMph = midflameMps * 2.23694;
    float tfF = taF + iCal / (0.015 * uMph + 0.026);            // fuel-level temperature
    float rhF = rhPct * exp(-0.033 * (tfF - taF));              // fuel-level humidity
    return FireCA_EmcPct(rhF, tfF) / 100.0;
}

// Direct normal irradiance, W/m2. sunY is the sine of the sun elevation.
float FireCA_DirectNormalIrradiance(float sunY)
{
    return sunY > 0.02 ? 1361.0 * pow(0.75, 1.0 / sunY) : 0.0;
}

#endif
