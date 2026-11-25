// The Rothermel core of the behavior pass. See docs/02-fire-spread.md, section "Behavior Pass".
// The core runs in the native units of the model (ft, lb, min, Btu). The outputs convert to SI at the end.
#ifndef FIRECA_ROTHERMEL_INCLUDED
#define FIRECA_ROTHERMEL_INCLUDED

// One entry of the fuel constants buffer. The layout matches FuelConstantsGpu in FuelTable.cs.
struct FuelConstants
{
    float burnable;
    float depthFt;
    float mxDead;
    float hasLive;
    float3 deadMoistureW;
    float liveRatioW;
    float3 fineMoistureW;
    float irDeadCoef;
    float3 sinkDeadW;
    float sinkLiveW;
    float irLiveCoef;
    float xi;
    float windC;
    float windB;
    float windK;
    float slopeK;
    float residenceMin;
    float pad;
};

#define FIRECA_MPS_TO_FT_PER_MIN 196.850394
#define FIRECA_FT_PER_MIN_TO_MPS 0.00508
#define FIRECA_BTU_FT2_MIN_TO_KW_M2 0.189276
#define FIRECA_FT_TO_M 0.3048

float FireCA_Damping(float r)
{
    r = saturate(r);
    return max(0.0, 1.0 - 2.59 * r + 5.11 * r * r - 3.52 * r * r * r);
}

// The unsheltered wind adjustment factor: midflame wind over the wind at 6.1 m (20 ft).
float FireCA_WindAdjustmentFactor(float depthFt)
{
    return 1.83 / log((20.0 + 0.36 * depthFt) / (0.13 * depthFt));
}

// Inputs: fuel constants, moisture fractions, the Slope gradient (rise over run), the wind at 6.1 m (m/s, X and Z).
// Outputs: Spread A = (head rate m/s, head direction X, head direction Z, eccentricity),
//          Spread B = (reaction intensity kW/m2, residence time s, head flame length m, no-wind rate m/s).
void FireCA_Behavior(FuelConstants k, float m1, float m10, float m100, float mLive, float2 gradient, float2 surfaceWind,
    out float4 spreadA, out float4 spreadB)
{
    spreadA = float4(0.0, 0.0, 0.0, 0.0);
    spreadB = float4(0.0, 0.0, 0.0, 0.0);
    if (k.burnable < 0.5)
        return;

    m1 = clamp(m1, 0.01, 0.60);
    float3 md = float3(m1, m10, m100);

    // Midflame wind, ft/min.
    float windSpeed = length(surfaceWind);
    float U = FireCA_WindAdjustmentFactor(k.depthFt) * windSpeed * FIRECA_MPS_TO_FT_PER_MIN;

    float mDead = dot(k.deadMoistureW, md);
    float etaDead = FireCA_Damping(mDead / k.mxDead);

    float mFine = dot(k.fineMoistureW, md);
    float mxLive = max(k.mxDead, 2.9 * k.liveRatioW * (1.0 - mFine / k.mxDead) - 0.226);
    float etaLive = k.hasLive > 0.5 ? FireCA_Damping(mLive / mxLive) : 0.0;

    float IR = k.irDeadCoef * etaDead + k.irLiveCoef * etaLive;                 // Btu/ft2/min
    float sink = dot(k.sinkDeadW, 250.0 + 1116.0 * md) + k.sinkLiveW * (250.0 + 1116.0 * mLive);
    float R0 = sink > 0.0 ? IR * k.xi / sink : 0.0;                             // ft/min

    float slope = length(gradient);
    float phiW = U > 0.0 ? k.windC * pow(U, k.windB) * k.windK : 0.0;
    float phiS = k.slopeK * slope * slope;

    // The wind factor points where the wind goes. The slope factor points upslope.
    float2 windDir = windSpeed > 1.0e-6 ? surfaceWind / windSpeed : float2(0.0, 0.0);
    float2 upslopeDir = slope > 1.0e-6 ? gradient / slope : float2(0.0, 0.0);
    float2 V = phiW * windDir + phiS * upslopeDir;
    float phiE = length(V);
    float2 headDir = phiE > 1.0e-9 ? V / phiE : float2(0.0, 0.0);
    if (phiE <= 1.0e-9)
        phiE = 0.0;
    float Ueff = phiE > 0.0 ? pow(phiE / (k.windC * k.windK), 1.0 / k.windB) : 0.0;   // ft/min

    // Wind limit.
    if (Ueff > 0.9 * IR)
    {
        Ueff = 0.9 * IR;
        phiE = Ueff > 0.0 ? k.windC * pow(Ueff, k.windB) * k.windK : 0.0;
    }

    float Rhead = R0 * (1.0 + phiE);                                            // ft/min
    float LB = min(8.0, 1.0 + 0.25 * (Ueff / 88.0));
    float ecc = sqrt(max(0.0, LB * LB - 1.0)) / LB;

    float tR = k.residenceMin * 60.0;                                           // s
    float byram = IR * k.residenceMin * Rhead / 60.0;                           // Btu/ft/s
    float flameFt = byram > 0.0 ? 0.45 * pow(byram, 0.46) : 0.0;

    if (Rhead <= 0.0)
    {
        headDir = float2(0.0, 0.0);
        ecc = 0.0;
    }
    spreadA = float4(Rhead * FIRECA_FT_PER_MIN_TO_MPS, headDir.x, headDir.y, ecc);
    spreadB = float4(IR * FIRECA_BTU_FT2_MIN_TO_KW_M2, tR, flameFt * FIRECA_FT_TO_M, R0 * FIRECA_FT_PER_MIN_TO_MPS);
}

// The rate of spread in the unit direction d, with the ignition point at the rear focus of the ellipse.
float FireCA_RateAt(float4 spreadA, float2 d)
{
    return spreadA.x * (1.0 - spreadA.w) / (1.0 - spreadA.w * dot(spreadA.yz, d));
}

#endif
