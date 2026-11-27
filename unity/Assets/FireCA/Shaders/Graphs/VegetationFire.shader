// Draws the flames and the smoke of the burning Plants (docs/decisions.md, decision D33). Each Plant in the list of
// burning Plants has 4 quads that face the camera. With _Mode at 0 the quads are flames. With _Mode at 1 the quads
// are puffs of smoke that rise and drift with the Surface Wind. The effect runs on real time, as the billows do.
Shader "FireCA/VegetationFire"
{
    Properties
    {
        _Mode("Mode (0 flames, 1 smoke)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            // The color is premultiplied. A flame has a low alpha, so it adds to the picture.
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            #include "Assets/FireCA/Shaders/Graphs/Vegetation.hlsl"

            #define FIRE_QUADS 4u
            // Luminance of the core of a flame, in nits. The value is near the fire glow of the terrain shader.
            #define FLAME_NITS 40000.0
            #define SMOKE_PERIOD_S 7.0
            #define SMOKE_UNDERGLOW_NITS 2500.0

            StructuredBuffer<Plant> _Plants;
            StructuredBuffer<uint> _Visible;
            Texture2D<float4> _FireCA_SurfaceWind;
            SamplerState fireca_linear_clamp_sampler;

            float _Mode;
            float _SmolderS;
            float4 _VegSunDirection;
            float4 _VegSunColor;
            float4 _VegSkyColor;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 quad : TEXCOORD0;        // x: -1 to 1 across, y: 0 at the bottom and 1 at the top
                float4 data : TEXCOORD1;        // x: strength, y: phase, z: life of a puff (0 to 1), w: flaming (0 or 1)
            };

            float Hash1(float x) { return frac(sin(x * 127.1) * 43758.5453); }

            float Noise2(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Vegetation_Hash3(float3(i, 1.7)), b = Vegetation_Hash3(float3(i + float2(1.0, 0.0), 1.7));
                float c = Vegetation_Hash3(float3(i + float2(0.0, 1.0), 1.7)), d = Vegetation_Hash3(float3(i + float2(1.0, 1.0), 1.7));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            Varyings Vert(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
            {
                static const float2 corners[6] =
                {
                    float2(-1.0, 0.0), float2(1.0, 0.0), float2(-1.0, 1.0),
                    float2(-1.0, 1.0), float2(1.0, 0.0), float2(1.0, 1.0),
                };
                Varyings o;
                o.positionCS = float4(0.0, 0.0, -1.0, 1.0);
                o.quad = float2(0.0, 0.0);
                o.data = float4(0.0, 0.0, 0.0, 0.0);

                Plant p = _Plants[_Visible[instanceId]];
                uint quad = (vertexId / 6u) % FIRE_QUADS;
                float2 corner = corners[vertexId % 6u];
                int model = (int)(p.model + 0.5);
                PlantFire fire = Vegetation_Fire(p);
                if (fire.age < 0.0)
                    return o;

                float key = p.seed * 91.0 + quad * 13.7;
                float halfWidth = Vegetation_HalfWidth[model] * p.widthScale * p.height;
                bool flaming = fire.age < fire.burnS;
                // A fire that takes more than half of the Plant is a crown fire: the flames stand above the Plant.
                bool crown = fire.consumeM > 0.5 * p.height;
                float3 right = normalize(cross(float3(0.0, 1.0, 0.0), _WorldSpaceCameraPos - p.position));
                float3 world;
                float strength;
                float life = 0.0;

                if (_Mode < 0.5)
                {
                    if (!flaming)
                        return o;
                    // The flames rise fast, hold, and sink at the end of the burn.
                    strength = smoothstep(0.0, 0.07, fire.burn) * (1.0 - smoothstep(0.7, 1.0, fire.burn));
                    float flameHeight = crown ? 0.7 * p.height + 1.3 * fire.flameLengthM : 1.4 * fire.flameLengthM + 0.4;
                    flameHeight *= (0.65 + 0.6 * Hash1(key)) * sqrt(max(strength, 1.0e-3));
                    float flameWidth = max(max(halfWidth * 0.9, 0.35 * fire.flameLengthM), 0.5) * (0.6 + 0.5 * Hash1(key + 1.0));
                    float3 offset = float3(Hash1(key + 2.0) - 0.5, 0.0, Hash1(key + 3.0) - 0.5) * 1.5 * halfWidth;
                    float baseHeight = crown ? 0.25 * p.height * saturate(fire.burn * 2.0) * Hash1(key + 4.0) : 0.0;
                    world = p.position + offset + float3(0.0, baseHeight, 0.0)
                        + right * corner.x * flameWidth + float3(0.0, corner.y * flameHeight, 0.0);
                }
                else
                {
                    // Smoke: thick and dark while the Plant flames, thin and pale while it smolders.
                    strength = flaming ? smoothstep(0.0, 0.05, fire.burn) : 0.45 * saturate(1.0 - (fire.age - fire.burnS) / max(_SmolderS, 1.0));
                    if (strength <= 0.0)
                        return o;
                    life = frac(_Time.y / SMOKE_PERIOD_S + quad / (float)FIRE_QUADS + Hash1(key));
                    float seconds = life * SMOKE_PERIOD_S;
                    float2 wind = _FireCA_SurfaceWind.SampleLevel(fireca_linear_clamp_sampler, p.position.xz * _FireCA_GridParams.z, 0).xy;
                    float top = crown && flaming ? p.height : min(p.height, max(fire.flameLengthM, 0.6 * p.height));
                    float rise = (flaming ? 5.5 : 2.2) * seconds * (1.0 - 0.25 * life);
                    float3 center = p.position + float3(0.0, top + rise, 0.0)
                        + float3(wind.x, 0.0, wind.y) * seconds * 0.8
                        + float3(Hash1(key + 2.0) - 0.5, 0.0, Hash1(key + 3.0) - 0.5) * (halfWidth + 2.0 * life);
                    float size = (0.8 + 0.7 * halfWidth) * (0.7 + 3.0 * life) * (flaming ? 1.3 : 0.9);
                    float3 up = normalize(cross(_WorldSpaceCameraPos - center, right));
                    world = center + right * corner.x * size + up * (corner.y * 2.0 - 1.0) * size;
                }

                o.positionCS = TransformWorldToHClip(GetCameraRelativePositionWS(world));
                o.quad = corner;
                o.data = float4(strength, Hash1(key + 5.0) * 20.0, life, flaming ? 1.0 : 0.0);
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float strength = i.data.x;
                float phase = i.data.y;
                float exposure = GetCurrentExposureMultiplier();
                if (_Mode < 0.5)
                {
                    // A flame: noise that scrolls up, inside a shape that narrows and wavers toward the top.
                    float time = _Time.y * 1.6 + phase;
                    float2 q = float2(i.quad.x * 1.3 + phase, i.quad.y * 2.0 - time * 1.5);
                    float noise = 0.6 * Noise2(q * 2.0) + 0.4 * Noise2(q * 4.7 + 9.0);
                    float waver = i.quad.x + (noise - 0.5) * 0.7 * i.quad.y;
                    float shape = saturate(1.0 - abs(waver) / lerp(0.80, 0.10, pow(saturate(i.quad.y), 0.7)));
                    float body = shape * saturate(1.0 - i.quad.y * (0.70 + 0.7 * noise));
                    float heat = saturate(body * 2.2 - 0.30 + (noise - 0.5) * 0.5);
                    if (heat <= 0.001)
                        discard;
                    float3 color = lerp(float3(1.0, 0.10, 0.015), float3(1.0, 0.42, 0.06), saturate(heat * 2.2));
                    color = lerp(color, float3(1.0, 0.82, 0.45), saturate(heat * 2.2 - 1.2));
                    float3 radiance = color * (FLAME_NITS * heat * heat * strength);
                    return float4(radiance * exposure, saturate(heat * 0.5 * strength));
                }

                // A puff of smoke: a soft round shape with a noise edge. It fades as it ages.
                float2 centered = float2(i.quad.x, i.quad.y * 2.0 - 1.0);
                float life = i.data.z;
                float noise = Noise2(centered * 1.6 + phase + float2(0.0, -_Time.y * 0.25));
                float round = saturate(1.0 - length(centered) * (0.85 + 0.5 * noise));
                float alpha = round * round * strength * smoothstep(0.0, 0.12, life) * pow(1.0 - life, 1.4) * 0.75;
                if (alpha <= 0.002)
                    discard;
                // Smoke from flames is dark with soot. Smoke from a smoldering Plant is pale.
                float albedo = lerp(0.42, 0.07, i.data.w);
                float3 light = _VegSunColor.rgb * (0.55 / PI) + _VegSkyColor.rgb;
                float3 color = albedo * light;
                color += float3(1.0, 0.38, 0.08) * SMOKE_UNDERGLOW_NITS * i.data.w * pow(1.0 - life, 4.0);
                return float4(color * exposure * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
