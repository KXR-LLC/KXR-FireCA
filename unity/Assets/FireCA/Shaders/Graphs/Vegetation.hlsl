// Shared code of the Vegetation: the Plant struct, and the fire state of a Plant from the Arrival Time of its cell.
// Vegetation.compute, Vegetation.shader, and VegetationFire.shader include this file.
// See docs/decisions.md, decision D33.
#ifndef FIRECA_VEGETATION_INCLUDED
#define FIRECA_VEGETATION_INCLUDED

#define VEGETATION_MODELS 11
// The fire takes the foliage up to this multiple of the flame length.
#define VEGETATION_CONSUME_PER_FLAME 2.0

// One Plant. The layout matches PlantInstance in VegetationRules.cs.
struct Plant
{
    float3 position;    // world position of the base, m
    float height;       // m
    float yaw;          // rad
    float widthScale;
    float model;        // PlantModelId
    float seed;         // 0 to 1
};

// One vertex of a Plant Model. The layout matches PlantModels.cs.
struct PlantVertex
{
    float3 position;    // the Plant Model has a height of 1
    float part;         // 0: bark, 1: foliage, 2: dead wood
    float3 normal;
    float shade;
    float2 uv;          // position in the Leaf Atlas. Below 0: the triangle has no texture
    float flutter;      // 0 at the base of a leaf card, 1 at its tip
    float leafSeed;     // 0 to 1, one value per leaf card
};

// The time that a Plant Model shows flames, s. The values match VegetationRules.BurnSeconds.
static const float Vegetation_BurnSeconds[VEGETATION_MODELS] = { 150.0, 150.0, 90.0, 150.0, 150.0, 50.0, 40.0, 30.0, 420.0, 480.0, 300.0 };
// The half width of a Plant Model as a share of its height.
static const float Vegetation_HalfWidth[VEGETATION_MODELS] = { 0.17, 0.26, 0.27, 0.48, 0.32, 0.85, 1.25, 0.60, 2.4, 2.6, 0.12 };

Texture2D<float> _FireCA_ArrivalTime;
Texture2D<float4> _FireCA_SpreadB;
float4 _FireCA_GridParams;      // x: Fire Grid cells per side, y: cell size (m), z: 1 / extent (1/m)
float4 _FireCA_FireParams;      // x: Sim Clock (s), y: sourceHoldS (s), z: emberFadeS (s)

struct PlantFire
{
    float age;          // Sim Clock minus the Arrival Time of the cell, s. Negative: not ignited
    float burnS;        // the time that the Plant shows flames, s
    float burn;         // 0 before the fire, rises to 1 over burnS
    float flameLengthM; // the flame length of the cell, m
    float consumeM;     // the fire takes the foliage below this height, m
    float scorchM;      // the fire scorches the foliage below this height, m
};

PlantFire Vegetation_Fire(Plant p)
{
    PlantFire f;
    int cells = (int)_FireCA_GridParams.x;
    int2 cell = clamp(int2(floor(p.position.xz / max(_FireCA_GridParams.y, 1.0e-3))), 0, max(cells - 1, 0));
    float arrival = _FireCA_ArrivalTime.Load(int3(cell, 0));
    float4 spreadB = _FireCA_SpreadB.Load(int3(cell, 0));
    f.age = arrival > 1.0e29 ? -1.0 : _FireCA_FireParams.x - arrival;
    f.burnS = max(spreadB.y, Vegetation_BurnSeconds[(int)(p.model + 0.5)]);
    f.burn = saturate(f.age / f.burnS);
    f.flameLengthM = max(spreadB.z, 0.0);
    f.consumeM = VEGETATION_CONSUME_PER_FLAME * f.flameLengthM;
    // Crown scorch height of Van Wagner (1973), hs = 0.1483 I^(2/3), with the fireline intensity I (kW/m) from the
    // flame length of Byram (1959), L = 0.0775 I^0.46. Together: hs = 6.03 L^1.449.
    f.scorchM = 6.03 * pow(f.flameLengthM, 1.449);
    return f;
}

float Vegetation_Hash3(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return frac((p.x + p.y) * p.z);
}

// Smooth noise from 0 to 1, with one cycle per unit of p.
float Vegetation_Noise3(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float x00 = lerp(Vegetation_Hash3(i), Vegetation_Hash3(i + float3(1.0, 0.0, 0.0)), f.x);
    float x10 = lerp(Vegetation_Hash3(i + float3(0.0, 1.0, 0.0)), Vegetation_Hash3(i + float3(1.0, 1.0, 0.0)), f.x);
    float x01 = lerp(Vegetation_Hash3(i + float3(0.0, 0.0, 1.0)), Vegetation_Hash3(i + float3(1.0, 0.0, 1.0)), f.x);
    float x11 = lerp(Vegetation_Hash3(i + float3(0.0, 1.0, 1.0)), Vegetation_Hash3(i + float3(1.0, 1.0, 1.0)), f.x);
    return lerp(lerp(x00, x10, f.y), lerp(x01, x11, f.y), f.z);
}

#endif
