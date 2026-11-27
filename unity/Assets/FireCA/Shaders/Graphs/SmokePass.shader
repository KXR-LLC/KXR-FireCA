// The Smoke Pass: a full-screen ray march through the Smoke texture of the Wind Grid. See docs/04-visualization.md.
// The shader runs in an HDRP Custom Pass at the Before Post Process injection point.
Shader "FireCA/SmokePass"
{
    HLSLINCLUDE

    #pragma vertex Vert
    #pragma target 4.5
    // The Smoke Pass sets the keyword when a Wind Nest is placed. A branch at run time between the two marches
    // made the march with no Wind Nest slower (docs/decisions.md, decision D32).
    #pragma multi_compile _ FIRECA_WIND_NEST
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"

    #define SMOKE_STEPS 96
    // With a Wind Nest on the ray (docs/decisions.md, decision D32): the steps before the box, in the box, and after.
    #define SMOKE_STEPS_OUTSIDE 40
    #define SMOKE_STEPS_NEST 96
    #define SMOKE_STEPS_MAX 176
    // The turbulence moves the smoke by the turbulence speed times this time, and by this distance at most.
    #define SMOKE_TURBULENCE_TIME_S 14.0
    #define SMOKE_TURBULENCE_MAX_M 70.0
    #define SMOKE_LIGHT_STEPS 6
    // Extinction per meter for a smoke density of 1.
    #define SMOKE_EXTINCTION 0.012
    // Smoke within this height of a ground cell with a Heat Flux Coarse over the threshold gets an orange glow.
    #define SMOKE_GLOW_HEIGHT_M 96.0
    #define SMOKE_GLOW_THRESHOLD_KW 50.0
    // The light of the fire on the smoke: the luminance for a full ring of fire below, and the height of the half value.
    #define SMOKE_FIRE_LIGHT_NITS 45.0
    #define SMOKE_FIRE_LIGHT_HEIGHT_M 250.0

    Texture3D<float> _FireCA_Smoke;
    Texture3D<float4> _FireCA_Turbulence;   // w: turbulence speed, m/s
    Texture2D<float> _FireCA_GroundHeight;
    Texture2D<float> _FireCA_HeatFluxCoarse;
    SamplerState fireca_linear_clamp_sampler;
    float4 _FireCA_WindGrid;        // cells on X, Y, Z, and the cell size (m)
    float _FireCA_WindBottomY;
    float4 _FireCA_SmokeParams;     // x: density multiplier, y: darkness (0 to 1), z: billows (0 to 1), w: turbulence gain
    float4 _FireCA_SunDirection;    // unit vector toward the sun
    float4 _FireCA_SunColor;        // sun illuminance, lux, per color channel
    float4 _FireCA_SkyColor;        // ambient luminance on the smoke, nits

    Texture3D<float> _FireCA_NestSmoke;
    Texture3D<float4> _FireCA_NestTurbulence;
    float4 _FireCA_NestGrid;        // cells on X, Y, Z, and the cell size (m). A cell size of 0: no Wind Nest
    float4 _FireCA_NestOrigin;      // world position of the corner of cell (0, 0, 0), and the width of the blend zone (m)

    // The position in the Wind Nest of a position in the Wind Grid.
    float3 NestLocal(float3 local)
    {
        return local - (_FireCA_NestOrigin.xyz - float3(0.0, _FireCA_WindBottomY, 0.0));
    }

    // The share of the Wind Nest at a position: 0 outside the box and at its lateral faces and its top face, 1 from
    // the width of the blend zone inside. The Wind Nest blends toward the Wind Grid in the same zone.
    float NestWeight(float3 nestLocal)
    {
        if (_FireCA_NestGrid.w <= 0.0)
            return 0.0;
        float3 nestExtent = _FireCA_NestGrid.xyz * _FireCA_NestGrid.w;
        float3 inside = min(nestLocal, nestExtent - nestLocal);
        if (nestLocal.y < 0.0)
            return 0.0;
        return saturate(min(min(inside.x, inside.z), nestExtent.y - nestLocal.y) / max(_FireCA_NestOrigin.w, 1.0));
    }

    float SampleSmoke(float3 local, float3 extent, bool nest)
    {
        float smoke = _FireCA_Smoke.SampleLevel(fireca_linear_clamp_sampler, local / extent, 0);
        // The flag is a constant at each call, so the code with no Wind Nest holds no read of the Wind Nest.
        // Outside the box, a real branch skips the read.
        if (nest)
        {
            float3 nestLocal = NestLocal(local);
            float w = NestWeight(nestLocal);
            [branch] if (w > 0.0)
                smoke = lerp(smoke, _FireCA_NestSmoke.SampleLevel(fireca_linear_clamp_sampler, nestLocal / (_FireCA_NestGrid.xyz * _FireCA_NestGrid.w), 0), w);
        }
        return smoke;
    }

    float SampleTurbulenceSpeed(float3 local, float3 extent, bool nest)
    {
        float speed = _FireCA_Turbulence.SampleLevel(fireca_linear_clamp_sampler, local / extent, 0).w;
        if (nest)
        {
            float3 nestLocal = NestLocal(local);
            float w = NestWeight(nestLocal);
            [branch] if (w > 0.0)
                speed = lerp(speed, _FireCA_NestTurbulence.SampleLevel(fireca_linear_clamp_sampler, nestLocal / (_FireCA_NestGrid.xyz * _FireCA_NestGrid.w), 0).w, w);
        }
        return speed;
    }

    float SmokeHash(float3 p)
    {
        p = frac(p * 0.1031);
        p += dot(p, p.zyx + 31.32);
        return frac((p.x + p.y) * p.z);
    }

    // Smooth noise from 0 to 1, with one cycle per unit of p.
    float SmokeNoise(float3 p)
    {
        float3 i = floor(p);
        float3 f = frac(p);
        f = f * f * (3.0 - 2.0 * f);
        float x00 = lerp(SmokeHash(i), SmokeHash(i + float3(1.0, 0.0, 0.0)), f.x);
        float x10 = lerp(SmokeHash(i + float3(0.0, 1.0, 0.0)), SmokeHash(i + float3(1.0, 1.0, 0.0)), f.x);
        float x01 = lerp(SmokeHash(i + float3(0.0, 0.0, 1.0)), SmokeHash(i + float3(1.0, 0.0, 1.0)), f.x);
        float x11 = lerp(SmokeHash(i + float3(0.0, 1.0, 1.0)), SmokeHash(i + float3(1.0, 1.0, 1.0)), f.x);
        return lerp(lerp(x00, x10, f.y), lerp(x01, x11, f.y), f.z);
    }

    // Billows (docs/decisions.md, decision D28): a noise field that rises with real time breaks the smoke of the
    // 32 m Wind Grid into puffs. The factor has a mean near 1, so the amount of smoke stays about the same.
    float Billows(float3 p, float amount)
    {
        if (amount <= 0.0)
            return 1.0;
        float3 q = p - float3(0.0, 9.0 * _Time.y, 0.0);
        float n = 0.65 * SmokeNoise(q / 110.0) + 0.35 * SmokeNoise(q / 41.0 + 17.0);
        return lerp(1.0, saturate(n * 3.4 - 1.05) * 1.55, amount);
    }

    // A vector of smooth noise with a mean of 0 and a size near 1, for the turbulence of the smoke (decision D31).
    // Two scales, 180 m and 80 m. The noise drifts up with real time, as the billows do.
    float3 SmokeEddies(float3 p)
    {
        float3 q = p - float3(0.0, 6.0 * _Time.y, 0.0);
        float3 a = float3(SmokeNoise(q / 180.0), SmokeNoise(q / 180.0 + float3(31.4, 17.7, 5.3)), SmokeNoise(q / 180.0 + float3(-12.1, 44.2, 23.9)));
        float3 b = float3(SmokeNoise(q / 80.0 + 7.1), SmokeNoise(q / 80.0 + float3(13.9, -27.3, 9.5)), SmokeNoise(q / 80.0 + float3(41.2, 3.6, -18.8)));
        return (0.7 * a + 0.3 * b - 0.5) * 3.4;
    }

    // The march of one ray. The flag is a constant: the variant with no Wind Nest holds no code of the Wind Nest,
    // and costs what the march cost before the Wind Nest (docs/decisions.md, decision D32).
    float4 MarchSmoke(float2 pixel, float3 origin, float3 direction, float3 inverse, float3 extent, float tEnter, float tExit, bool nest)
    {
        // 2. 96 steps per ray, with a per-pixel jitter of the start point. A ray that crosses the box of the Wind Nest
        // has three parts: before the box, in the box, and after the box. The part in the box has 96 steps of its own.
        float3 nestExtent = _FireCA_NestGrid.xyz * _FireCA_NestGrid.w;
        float3 nestOrigin = origin - (_FireCA_NestOrigin.xyz - float3(0.0, _FireCA_WindBottomY, 0.0));
        float3 n0 = (float3(0.0, 0.0, 0.0) - nestOrigin) * inverse;
        float3 n1 = (nestExtent - nestOrigin) * inverse;
        float3 nNear = min(n0, n1), nFar = max(n0, n1);
        float nestEnter = max(max(max(nNear.x, nNear.y), nNear.z), tEnter);
        float nestExit = min(min(min(nFar.x, nFar.y), nFar.z), tExit);
        bool crossesNest = nest && nestExit > nestEnter;
        int stepsBefore = crossesNest ? SMOKE_STEPS_OUTSIDE : SMOKE_STEPS;
        int stepsNest = crossesNest ? SMOKE_STEPS_NEST : 0;
        int stepsAfter = crossesNest ? SMOKE_STEPS_OUTSIDE : 0;
        float endBefore = crossesNest ? nestEnter : tExit;
        int stepsTotal = stepsBefore + stepsNest + stepsAfter;
        float lengthBefore = (endBefore - tEnter) / stepsBefore;
        float lengthNest = crossesNest ? (nestExit - nestEnter) / SMOKE_STEPS_NEST : 0.0;
        float lengthAfter = crossesNest ? (tExit - nestExit) / SMOKE_STEPS_OUTSIDE : 0.0;
        float jitter = InterleavedGradientNoise(pixel, 0);
        float density = max(_FireCA_SmokeParams.x, 0.0) * SMOKE_EXTINCTION;
        float3 sun = normalize(_FireCA_SunDirection.xyz);
        float exposure = GetCurrentExposureMultiplier();
        // Darkness lowers the share of the light that the smoke scatters. The rest is absorbed, as in soot.
        float albedo = lerp(1.0, 0.06, saturate(_FireCA_SmokeParams.y));
        float billows = saturate(_FireCA_SmokeParams.z);
        float turbulenceGain = max(_FireCA_SmokeParams.w, 0.0);

        float transmittance = 1.0;
        float3 radiance = float3(0.0, 0.0, 0.0);
        for (int i = 0; i < SMOKE_STEPS_MAX; i++)
        {
            if (i >= stepsTotal)
                break;
            float t = tEnter + (i + jitter) * lengthBefore;
            float stepLength = lengthBefore;
            if (crossesNest && i >= stepsBefore)
            {
                bool inNest = i < stepsBefore + stepsNest;
                stepLength = inNest ? lengthNest : lengthAfter;
                t = inNest ? nestEnter + (i - stepsBefore + jitter) * lengthNest : nestExit + (i - stepsBefore - stepsNest + jitter) * lengthAfter;
            }
            if (stepLength <= 0.0)
                continue;
            float3 p = origin + direction * t;
            // 3. Trilinear sample of the Smoke texture. The turbulence field moves the sample point
            // (docs/decisions.md, decision D31), so the smoke shows the eddies that the Wind Grid cannot hold.
            // A point with no smoke before the move is skipped, which saves the noise for most of the ray.
            float3 samplePoint = p;
            if (SampleSmoke(p, extent, nest) <= 1.0e-4)
                continue;
            if (turbulenceGain > 0.0)
            {
                float speed = SampleTurbulenceSpeed(p, extent, nest);
                float reach = min(turbulenceGain * speed * SMOKE_TURBULENCE_TIME_S, SMOKE_TURBULENCE_MAX_M);
                samplePoint += reach * SmokeEddies(p);
            }
            float smoke = SampleSmoke(samplePoint, extent, nest);
            if (smoke <= 1.0e-4)
                continue;
            // The Wind Nest holds eddies of its own, so the billows are weaker in the box.
            smoke *= Billows(p, billows * lerp(1.0, 0.4, (nest ? NestWeight(NestLocal(p)) : 0.0)));
            if (smoke <= 1.0e-4)
                continue;

            // 5. March 6 steps toward the sun for the light that reaches the smoke.
            float lightDepth = 0.0;
            float lightStep = 48.0;
            float3 lp = p;
            for (int k = 0; k < SMOKE_LIGHT_STEPS; k++)
            {
                lp += sun * lightStep;
                lightDepth += SampleSmoke(lp, extent, nest) * lightStep;
                lightStep *= 1.5;
            }
            float lightTransmittance = exp(-lightDepth * density);
            // The smoke above shades the ambient light of the sky (docs/decisions.md, decision D30). Two samples
            // straight up give the depth. A floor stands for the light that scatters more than once.
            float skyDepth = SampleSmoke(p + float3(0.0, 48.0, 0.0), extent, nest) * 80.0 + SampleSmoke(p + float3(0.0, 144.0, 0.0), extent, nest) * 160.0;
            float skyTransmittance = lerp(0.2, 1.0, exp(-skyDepth * density));
            float3 scattered = (_FireCA_SunColor.rgb * lightTransmittance * (0.9 / (4.0 * PI)) + _FireCA_SkyColor.rgb * skyTransmittance) * albedo;

            // 6. Orange glow near a ground cell with a Heat Flux Coarse above 50 kW/m2.
            float2 uv = p.xz / extent.xz;
            float heat = _FireCA_HeatFluxCoarse.SampleLevel(fireca_linear_clamp_sampler, uv, 0);
            float aboveGround = p.y + _FireCA_WindBottomY - _FireCA_GroundHeight.SampleLevel(fireca_linear_clamp_sampler, uv, 0);
            if (heat > SMOKE_GLOW_THRESHOLD_KW && aboveGround < SMOKE_GLOW_HEIGHT_M)
            {
                float glow = saturate(1.0 - aboveGround / SMOKE_GLOW_HEIGHT_M) * saturate(heat / 400.0);
                scattered += float3(1.0, 0.38, 0.08) * 60000.0 * glow;
            }

            // The light of the fire on the smoke (decision D30). A ground cell at a horizontal distance r lights smoke
            // at a height h in proportion to h / (h^2 + r^2)^1.5. One tap below and two rings of 6 taps stand for the
            // sum over the ground. The light is too weak to see by day.
            float h = max(aboveGround, 16.0);
            float fireLight = saturate(heat / 400.0);
            for (int ring = 0; ring < 12; ring++)
            {
                float angle = ring * (PI / 3.0) + (ring >= 6 ? PI / 6.0 : 0.0);
                float radius = h * (ring >= 6 ? 2.2 : 0.9);
                float2 tap = uv + float2(cos(angle), sin(angle)) * radius / extent.xz;
                float tapHeat = _FireCA_HeatFluxCoarse.SampleLevel(fireca_linear_clamp_sampler, tap, 0);
                fireLight += saturate(tapHeat / 400.0) * (ring >= 6 ? 0.35 : 0.8);
            }
            fireLight *= 1.0 / (1.0 + (h / SMOKE_FIRE_LIGHT_HEIGHT_M) * (h / SMOKE_FIRE_LIGHT_HEIGHT_M));
            scattered += float3(1.0, 0.42, 0.12) * SMOKE_FIRE_LIGHT_NITS * fireLight * lerp(1.0, 0.35, saturate(_FireCA_SmokeParams.y));

            // 4. Beer-Lambert transmittance.
            float stepTransmittance = exp(-smoke * density * stepLength);
            radiance += transmittance * (1.0 - stepTransmittance) * scattered;
            transmittance *= stepTransmittance;
            if (transmittance < 0.01)
                break;
        }

        // 7. Blend over the camera color. The color is premultiplied.
        return float4(radiance * exposure, 1.0 - transmittance);
    }

    float4 FullScreenPass(Varyings varyings) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);
        float depth = LoadCameraDepth(varyings.positionCS.xy);
        PositionInputs posInput = GetPositionInput(varyings.positionCS.xy, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
        // The scene position is relative to the camera.
        float sceneDistance = length(posInput.positionWS);
        float3 direction = posInput.positionWS / max(sceneDistance, 1.0e-4);
        if (depth == UNITY_RAW_FAR_CLIP_VALUE)
            sceneDistance = 1.0e9;

        float3 extent = _FireCA_WindGrid.xyz * _FireCA_WindGrid.w;
        if (extent.x <= 0.0)
            return float4(0.0, 0.0, 0.0, 0.0);
        // Ray origin relative to the Wind Grid origin.
        float3 origin = _WorldSpaceCameraPos - float3(0.0, _FireCA_WindBottomY, 0.0);

        // 1. March from the entry point of the Wind Grid bounds to the exit point, or to the scene depth.
        float3 inverse = 1.0 / (abs(direction) > 1.0e-6 ? direction : float3(1.0e-6, 1.0e-6, 1.0e-6));
        float3 t0 = (float3(0.0, 0.0, 0.0) - origin) * inverse;
        float3 t1 = (extent - origin) * inverse;
        float3 tNear = min(t0, t1), tFar = max(t0, t1);
        float tEnter = max(max(tNear.x, tNear.y), max(tNear.z, 0.0));
        float tExit = min(min(min(tFar.x, tFar.y), tFar.z), sceneDistance);
        if (tExit <= tEnter)
            return float4(0.0, 0.0, 0.0, 0.0);

#if defined(FIRECA_WIND_NEST)
        return MarchSmoke(varyings.positionCS.xy, origin, direction, inverse, extent, tEnter, tExit, true);
#else
        return MarchSmoke(varyings.positionCS.xy, origin, direction, inverse, extent, tEnter, tExit, false);
#endif
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "Smoke"
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha
            Cull Off

            HLSLPROGRAM
            #pragma fragment FullScreenPass
            ENDHLSL
        }
    }
    Fallback Off
}
