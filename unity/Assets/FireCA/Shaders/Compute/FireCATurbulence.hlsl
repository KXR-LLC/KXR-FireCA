// The turbulence field of the display (docs/decisions.md, decision D31). The Wind Particles and the Smoke Pass use
// it to show the eddies that are too small for the 32 m cells of the Wind Grid.
#ifndef FIRECA_TURBULENCE_INCLUDED
#define FIRECA_TURBULENCE_INCLUDED

#define FIRECA_TURBULENCE_MODES 6

// A sum of 6 plane waves with wavelengths from 44 m to 197 m. Each wave moves the air across its own direction,
// so the field has no divergence. The mean square of the field is 1. The field has no unit: multiply it by the
// turbulence speed of the Turbulence texture. p is a world position in m, and time is in seconds.
float3 FireCA_TurbulenceField(float3 p, float time)
{
    static const float3 direction[FIRECA_TURBULENCE_MODES] =
    {
        float3(0.80, 0.10, 0.59), float3(-0.45, 0.30, 0.84), float3(0.20, 0.75, -0.63),
        float3(-0.70, -0.55, -0.45), float3(0.35, -0.85, 0.39), float3(0.93, 0.25, -0.27)
    };
    static const float3 axis[FIRECA_TURBULENCE_MODES] =
    {
        float3(0.0, 1.0, 0.0), float3(1.0, 0.0, 0.0), float3(0.0, 0.0, 1.0),
        float3(0.0, 1.0, 0.0), float3(1.0, 0.0, 0.0), float3(0.0, 1.0, 0.0)
    };
    static const float wavelength[FIRECA_TURBULENCE_MODES] = { 44.0, 61.0, 83.0, 112.0, 151.0, 197.0 };
    static const float rate[FIRECA_TURBULENCE_MODES] = { 0.95, 0.78, 0.62, 0.49, 0.38, 0.30 };
    static const float phase[FIRECA_TURBULENCE_MODES] = { 0.0, 1.7, 3.1, 4.4, 0.9, 5.3 };

    float3 sum = float3(0.0, 0.0, 0.0);
    for (int n = 0; n < FIRECA_TURBULENCE_MODES; n++)
    {
        float3 k = normalize(direction[n]);
        float3 across = normalize(cross(k, axis[n]));
        sum += across * cos(dot(k, p) * (6.2831853 / wavelength[n]) + phase[n] + rate[n] * time);
    }
    // Each wave has a mean square of 1/2.
    return sum * sqrt(2.0 / FIRECA_TURBULENCE_MODES);
}

#endif
