// Layer logic of the terrain shader. The Custom Function node of the FireCATerrain Shader Graph calls FireCATerrain_float.
// Layers, from bottom to top: fuel color, Overlay, burn scar, fire glow. See docs/04-visualization.md.
#ifndef FIRECA_TERRAIN_INCLUDED
#define FIRECA_TERRAIN_INCLUDED

// Overlay identifiers. The values match Overlays.Catalog in Overlays.cs.
#define FIRECA_OVERLAY_NONE 0
#define FIRECA_OVERLAY_FUEL_MODEL 1
#define FIRECA_OVERLAY_FUEL_MOISTURE 2
#define FIRECA_OVERLAY_RATE_OF_SPREAD 3
#define FIRECA_OVERLAY_FLAME_LENGTH 4
#define FIRECA_OVERLAY_ARRIVAL_TIME 5
#define FIRECA_OVERLAY_SURFACE_WIND 6
#define FIRECA_OVERLAY_AIR_TEMPERATURE 7
#define FIRECA_OVERLAY_RELATIVE_HUMIDITY 8
#define FIRECA_OVERLAY_INSOLATION 9
#define FIRECA_OVERLAY_SLOPE 10

// Luminance of the fire glow, in nits. The values are above the sunlit ground, so the glow triggers HDRP bloom.
#define FIRECA_FLAME_NITS 160000.0
#define FIRECA_EMBER_NITS 45000.0
// The Arrival time Overlay has bands of 10 simulated minutes. The color map spans 36 bands (6 hours).
#define FIRECA_ARRIVAL_BAND_S 600.0
#define FIRECA_ARRIVAL_BANDS 36.0

#include "Assets/FireCA/Shaders/Compute/FireCACommon.hlsl"
#include "Assets/FireCA/Shaders/Compute/Weather.hlsl"

Texture2D<float> _FireCA_Fuel;
Texture2D<float4> _FireCA_Normal;
Texture2D<float4> _FireCA_Slope;
Texture2D<float> _FireCA_ArrivalTime;
Texture2D<float4> _FireCA_SpreadA;
Texture2D<float4> _FireCA_SpreadB;
Texture2D<float> _FireCA_Elevation;
Texture2D<float> _FireCA_Moisture1h;
Texture2D<float> _FireCA_Insolation;
Texture2D<float4> _FireCA_SurfaceWind;
Texture2D<float4> _FireCA_ColorMap;
Texture2D<float4> _FireCA_FuelPalette;
SamplerState fireca_linear_clamp_sampler;

float4 _FireCA_GridParams;      // x: Fire Grid cells per side, y: cell size (m), z: 1 / extent (1/m)
float4 _FireCA_OverlayParams;   // x: Overlay identifier, y: Overlay opacity
float4 _FireCA_FireParams;      // x: Sim Clock (s), y: sourceHoldS (s), z: emberFadeS (s)
float4 _FireCA_WeatherA;        // startSolarHour, weather time (s), tempMaxC, tempMinC
float4 _FireCA_WeatherB;        // rhMinPct, lapseRateCPerKm, referenceElevationM, unused

float FireCA_Hash(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float3 FireCA_ColorMap(float t)
{
    return _FireCA_ColorMap.SampleLevel(fireca_linear_clamp_sampler, float2(saturate(t), 0.5), 0).rgb;
}

// The band of the Arrival time Overlay of a cell, or -1 when the cell is not ignited at the Sim Clock value.
float FireCA_ArrivalBand(int2 cell, int cells)
{
    cell = clamp(cell, 0, cells - 1);
    float arrival = _FireCA_ArrivalTime.Load(int3(cell, 0));
    if (arrival > 1.0e29 || arrival > _FireCA_FireParams.x)
        return -1.0;
    return floor(arrival / FIRECA_ARRIVAL_BAND_S);
}

void FireCATerrain_float(float3 PositionWS, out float3 BaseColor, out float3 NormalWS, out float3 Emission, out float Smoothness)
{
    BaseColor = float3(0.35, 0.33, 0.28);
    NormalWS = float3(0.0, 1.0, 0.0);
    Emission = float3(0.0, 0.0, 0.0);
    Smoothness = 0.08;

#ifndef SHADERGRAPH_PREVIEW
    float cellsF = _FireCA_GridParams.x;
    if (cellsF < 1.0)
        return;
    int cells = (int)cellsF;

    float2 cellF = PositionWS.xz / _FireCA_GridParams.y;
    int2 cell = clamp(int2(floor(cellF)), 0, cells - 1);
    float2 uv = PositionWS.xz * _FireCA_GridParams.z;

    // Normal: the texture holds world X and Z. Rebuild Y.
    float2 nxz = _FireCA_Normal.SampleLevel(fireca_linear_clamp_sampler, uv, 0).xy;
    NormalWS = normalize(float3(nxz.x, sqrt(saturate(1.0 - dot(nxz, nxz))), nxz.y));

    // Cell state, from the Arrival Time and the Sim Clock (docs/02-fire-spread.md, "Cell state").
    float clock = _FireCA_FireParams.x;
    float sourceHold = _FireCA_FireParams.y;
    float emberFade = max(_FireCA_FireParams.z, 1.0);
    float arrival = _FireCA_ArrivalTime.Load(int3(cell, 0));
    float age = clock - arrival;
    bool ignited = arrival < 1.0e29 && age >= 0.0;
    float4 spreadB = _FireCA_SpreadB.Load(int3(cell, 0));
    float residence = spreadB.y;

    // Layer 1: fuel color, with a small noise variation.
    uint fuel = FireCA_LoadFuelCode(_FireCA_Fuel, cell);
    float3 color = _FireCA_FuelPalette.Load(int3(fuel, 0, 0)).rgb;
    float fine = FireCA_Hash(float2(cell));
    float coarse = FireCA_Hash(float2(cell >> 3));
    color *= 0.90 + 0.12 * fine + 0.08 * coarse;

    // Layer 2: Overlay.
    int overlay = (int)round(_FireCA_OverlayParams.x);
    float opacity = saturate(_FireCA_OverlayParams.y);
    float scarWeight = 1.0;
    if (overlay != FIRECA_OVERLAY_NONE)
    {
        float3 overlayColor = color;
        float overlayWeight = opacity;
        if (overlay == FIRECA_OVERLAY_FUEL_MODEL)
        {
            overlayColor = _FireCA_FuelPalette.Load(int3(fuel, 1, 0)).rgb;
        }
        else if (overlay == FIRECA_OVERLAY_SLOPE)
        {
            float2 gradient = _FireCA_Slope.SampleLevel(fireca_linear_clamp_sampler, uv, 0).xy;
            overlayColor = FireCA_ColorMap(length(gradient) * 100.0 / 120.0);
        }
        else if (overlay == FIRECA_OVERLAY_FUEL_MOISTURE)
        {
            // 0 to 25 percent.
            overlayColor = FireCA_ColorMap(_FireCA_Moisture1h.Load(int3(cell, 0)) * 100.0 / 25.0);
        }
        else if (overlay == FIRECA_OVERLAY_SURFACE_WIND)
        {
            // 0 to 20 m/s, with direction arrows on a 128 m spacing.
            float2 wind = _FireCA_SurfaceWind.SampleLevel(fireca_linear_clamp_sampler, uv, 0).xy;
            overlayColor = FireCA_ColorMap(length(wind) / 20.0);
            const float spacing = 128.0;
            float2 grid = PositionWS.xz / spacing;
            float2 local = frac(grid) - 0.5;
            float2 centerUv = (floor(grid) + 0.5) * spacing * _FireCA_GridParams.z;
            float2 centerWind = _FireCA_SurfaceWind.SampleLevel(fireca_linear_clamp_sampler, centerUv, 0).xy;
            float centerSpeed = length(centerWind);
            if (centerSpeed > 0.05)
            {
                // The arrow points where the wind goes.
                float2 d = centerWind / centerSpeed;
                float alongArrow = dot(local, d);
                float acrossArrow = abs(local.x * d.y - local.y * d.x);
                bool shaft = alongArrow > -0.36 && alongArrow < 0.16 && acrossArrow < 0.035;
                bool head = alongArrow >= 0.16 && alongArrow < 0.40 && acrossArrow < (0.40 - alongArrow) * 0.55;
                if (shaft || head)
                {
                    overlayColor = float3(0.95, 0.95, 0.95);
                    overlayWeight = 1.0;
                }
            }
        }
        else if (overlay == FIRECA_OVERLAY_INSOLATION)
        {
            // 0 to 1100 W/m2.
            overlayColor = FireCA_ColorMap(_FireCA_Insolation.Load(int3(cell, 0)) / 1100.0);
        }
        else if (overlay == FIRECA_OVERLAY_AIR_TEMPERATURE)
        {
            // tempMinC - 10 to tempMaxC + 5. The value comes from the functions that the Weather Model calls.
            float elevation = _FireCA_Elevation.SampleLevel(fireca_linear_clamp_sampler, uv, 0);
            float airTempC = FireCA_CellAirTempC(_FireCA_WeatherA, _FireCA_WeatherB, elevation);
            float low = _FireCA_WeatherA.w - 10.0, high = _FireCA_WeatherA.z + 5.0;
            overlayColor = FireCA_ColorMap((airTempC - low) / max(high - low, 1.0e-3));
        }
        else if (overlay == FIRECA_OVERLAY_RELATIVE_HUMIDITY)
        {
            // 0 to 100 percent.
            float elevation = _FireCA_Elevation.SampleLevel(fireca_linear_clamp_sampler, uv, 0);
            overlayColor = FireCA_ColorMap(FireCA_CellRelativeHumidityPct(_FireCA_WeatherA, _FireCA_WeatherB, elevation) / 100.0);
        }
        else if (overlay == FIRECA_OVERLAY_RATE_OF_SPREAD)
        {
            // 0.001 to 5 m/s, logarithmic. A cell that cannot spread keeps the fuel color.
            float rate = _FireCA_SpreadA.Load(int3(cell, 0)).x;
            if (rate > 0.0)
                overlayColor = FireCA_ColorMap((log10(max(rate, 1.0e-6)) + 3.0) / (log10(5.0) + 3.0));
            else
                overlayWeight = 0.0;
        }
        else if (overlay == FIRECA_OVERLAY_FLAME_LENGTH)
        {
            if (FireCA_IsBurnable(fuel))
                overlayColor = FireCA_ColorMap(spreadB.z / 20.0);
            else
                overlayWeight = 0.0;
        }
        else if (overlay == FIRECA_OVERLAY_ARRIVAL_TIME)
        {
            // Bands of 10 simulated minutes, with a contour line at each band edge.
            // The Overlay replaces the burn scar of an ignited cell (docs/decisions.md, decision D11).
            float band = FireCA_ArrivalBand(cell, cells);
            if (band >= 0.0)
            {
                overlayColor = FireCA_ColorMap(fmod(band, FIRECA_ARRIVAL_BANDS) / (FIRECA_ARRIVAL_BANDS - 1.0));
                // The contour line is 3 cells wide, so the line stays visible from the Top Down preset.
                bool edge = false;
                for (int reach = 1; reach <= 3; reach++)
                {
                    edge = edge
                        || FireCA_ArrivalBand(cell + int2(reach, 0), cells) > band
                        || FireCA_ArrivalBand(cell + int2(-reach, 0), cells) > band
                        || FireCA_ArrivalBand(cell + int2(0, reach), cells) > band
                        || FireCA_ArrivalBand(cell + int2(0, -reach), cells) > band;
                }
                if (edge)
                    overlayColor *= 0.15;
                scarWeight = 1.0 - opacity;
            }
            else
            {
                overlayWeight = 0.0;
            }
        }
        color = lerp(color, overlayColor, overlayWeight);
    }

    // Layer 3: burn scar. Char black after ignition, then ash gray after sourceHoldS.
    if (ignited)
    {
        float3 charColor = float3(0.025, 0.022, 0.020) * (0.8 + 0.4 * fine);
        float3 ashColor = float3(0.20, 0.19, 0.18) * (0.85 + 0.3 * coarse);
        float3 scar = lerp(charColor, ashColor, saturate((age - sourceHold) / 300.0));
        float charred = saturate(age / max(residence, 1.0));
        color = lerp(color, scar, charred * scarWeight);
    }

    // Layer 4: fire glow. A flaming cell emits with an intensity that rises with the head flame length.
    // A smoldering cell emits a dim red that fades to zero over emberFadeS.
    if (ignited && age < sourceHold)
    {
        float flicker = 0.75 + 0.5 * FireCA_Hash(float2(cell) + floor(clock * 4.0));
        if (age < residence)
        {
            Emission = float3(1.0, 0.42, 0.08) * FIRECA_FLAME_NITS * (0.5 + spreadB.z) * flicker;
        }
        else
        {
            float fade = saturate(1.0 - (age - residence) / emberFade);
            Emission = float3(1.0, 0.16, 0.03) * FIRECA_EMBER_NITS * fade * fade * flicker;
        }
    }

    BaseColor = color;
#endif
}

#endif
