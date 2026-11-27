// Draws the Plants of the Vegetation (docs/decisions.md, decision D33). One draw call renders every Plant of one
// level of detail: the vertex shader reads the Plant from a buffer and the vertex of its Plant Model from a second
// buffer. The Plant Models and the Leaf Atlas come from tools/blender/plant_models.py (decision D34). The shader is a hand-written HDRP forward pass with its own sun and sky light.
// A Plant is normal, burning, or burnt. The state comes from the Arrival Time of the cell under the Plant.
Shader "FireCA/Vegetation"
{
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "Queue" = "Geometry" "RenderType" = "Opaque" }

        HLSLINCLUDE
        #pragma target 4.5

        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/NormalBuffer.hlsl"
        #include "Assets/FireCA/Shaders/Graphs/Vegetation.hlsl"

        // Luminance of the embers on burning wood and at the edge of burning foliage, in nits.
        #define VEGETATION_EMBER_NITS 60000.0
        // The fire lights the Plants near it. The value is the luminance of a white Plant next to a strong fire.
        #define VEGETATION_FIRE_LIGHT_NITS 900.0

        StructuredBuffer<Plant> _Plants;
        StructuredBuffer<uint> _Visible;
        StructuredBuffer<PlantVertex> _PlantVertices;
        Texture2D<float> _FireCA_Fuel;
        Texture2D<float4> _FireCA_Normal;
        Texture2D<float4> _FireCA_SurfaceWind;
        Texture2D<float> _FireCA_HeatFluxCoarse;
        // The Leaf Atlas. r: brightness, where 0.5 is the mean. g: 1 on a twig. b: a random value per leaf. a: the cutout.
        Texture2D<float4> _LeafAtlas;
        SamplerState sampler_LeafAtlas;
        SamplerState fireca_linear_clamp_sampler;

        int _LodStart;              // the first vertex of the level of detail in _PlantVertices
        int _LodVertices;           // vertices per Plant Model at the level of detail
        float _Detail;              // 1 at level 0, lower at the far levels. Scales the leaf cutout
        float4 _VegSunDirection;    // toward the sun
        float4 _VegSunColor;        // illuminance of the sun, lux
        float4 _VegSkyColor;        // luminance of the sky, nits
        float _VegSway;             // multiplier on the wind sway

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 normalWS : TEXCOORD0;
            float3 leafPosition : TEXCOORD1;    // position in the Plant, m. The noise of the foliage reads it
            float4 surface : TEXCOORD2;         // x: part, y: shade, z: height above the base (m), w: seed
            float4 fire : TEXCOORD3;            // x: burn, y: consume height (m), z: scorch height (m), w: ember strength
            float4 plant : TEXCOORD4;           // x: model, y: fuel code, z: sun share from the slope, w: fire light
            float3 leaf : TEXCOORD5;            // xy: position in the Leaf Atlas (below 0: no texture), z: leaf seed
        };

        Varyings VegetationVertex(uint vertexId, uint instanceId)
        {
            Varyings o;
            Plant p = _Plants[_Visible[instanceId]];
            int model = (int)(p.model + 0.5);
            PlantVertex v = _PlantVertices[_LodStart + model * _LodVertices + (int)vertexId];

            float3 local = v.position * float3(p.widthScale, 1.0, p.widthScale) * p.height;
            float s = sin(p.yaw), c = cos(p.yaw);
            float3 turned = float3(c * local.x + s * local.z, local.y, -s * local.x + c * local.z);
            float3 normal = normalize(float3(c * v.normal.x + s * v.normal.z, v.normal.y, -s * v.normal.x + c * v.normal.z));
            float3 world = p.position + turned;

            // The wind bends a Plant. The bend grows with the height above the base. Logs do not move.
            float2 uv = p.position.xz * _FireCA_GridParams.z;
            if (model < 8)
            {
                float2 wind = _FireCA_SurfaceWind.SampleLevel(fireca_linear_clamp_sampler, uv, 0).xy;
                float gust = 0.55 + 0.45 * sin(_Time.y * (1.3 + p.seed) + p.seed * 40.0 + world.x * 0.05);
                float flutter = v.flutter * 0.02 * sin(_Time.y * 5.0 + dot(turned, float3(3.1, 2.3, 2.7)) + v.leafSeed * 6.0);
                world.xz += _VegSway * wind * (0.0045 * gust * turned.y * turned.y / max(p.height, 1.0) + flutter * saturate(length(wind) * 0.15));
            }

            PlantFire fire = Vegetation_Fire(p);
            // Embers glow while the Plant burns, and fade over emberFadeS after the flames.
            float ember = 0.0;
            if (fire.age >= 0.0)
                ember = fire.age < fire.burnS ? 1.0 : saturate(1.0 - (fire.age - fire.burnS) / max(_FireCA_FireParams.z, 1.0));

            // The share of the sun on the slope under the Plant. A slope that faces away from the sun is in shade.
            float2 nxz = _FireCA_Normal.SampleLevel(fireca_linear_clamp_sampler, uv, 0).xy;
            float3 ground = float3(nxz.x, sqrt(saturate(1.0 - dot(nxz, nxz))), nxz.y);
            float slopeSun = saturate(dot(ground, _VegSunDirection.xyz) * 5.0 + 0.35);

            // The light of the fire: the Heat Flux Coarse under the Plant and at four points 48 m away.
            float2 tap = 48.0 * _FireCA_GridParams.zz;
            float heat = saturate(_FireCA_HeatFluxCoarse.SampleLevel(fireca_linear_clamp_sampler, uv, 0) / 400.0);
            heat += 0.4 * saturate(_FireCA_HeatFluxCoarse.SampleLevel(fireca_linear_clamp_sampler, uv + float2(tap.x, 0.0), 0) / 400.0);
            heat += 0.4 * saturate(_FireCA_HeatFluxCoarse.SampleLevel(fireca_linear_clamp_sampler, uv - float2(tap.x, 0.0), 0) / 400.0);
            heat += 0.4 * saturate(_FireCA_HeatFluxCoarse.SampleLevel(fireca_linear_clamp_sampler, uv + float2(0.0, tap.y), 0) / 400.0);
            heat += 0.4 * saturate(_FireCA_HeatFluxCoarse.SampleLevel(fireca_linear_clamp_sampler, uv - float2(0.0, tap.y), 0) / 400.0);

            int cells = (int)_FireCA_GridParams.x;
            int2 cell = clamp(int2(floor(p.position.xz / max(_FireCA_GridParams.y, 1.0e-3))), 0, max(cells - 1, 0));
            float fuel = floor(_FireCA_Fuel.Load(int3(cell, 0)) * 255.0 + 0.5);

            o.positionCS = TransformWorldToHClip(GetCameraRelativePositionWS(world));
            o.normalWS = normal;
            o.leafPosition = local + p.seed * 37.0;
            o.surface = float4(v.part, v.shade, turned.y, p.seed);
            o.fire = float4(fire.burn, fire.consumeM, fire.scorchM, ember);
            o.plant = float4(p.model, fuel, slopeSun, heat);
            o.leaf = float3(v.uv, v.leafSeed);
            return o;
        }

        // The pattern of the foliage, 0 to 1. The far levels of detail use a coarse pattern, which does not shimmer.
        float FoliagePattern(float3 leafPosition)
        {
            float coarse = Vegetation_Noise3(leafPosition * 0.9);
            float fine = Vegetation_Noise3(leafPosition * 4.3 + 11.0);
            return lerp(coarse, 0.55 * coarse + 0.45 * fine, saturate(_Detail));
        }

        // How far the fire has taken the foliage at a height, 0 to 1. The fire climbs the Plant as it burns.
        float Consumed(float heightM, float burn, float consumeM)
        {
            float reach = consumeM * saturate(burn * 1.6);
            return saturate((reach - heightM) / (0.25 * consumeM + 0.4)) * step(1.0e-4, burn);
        }

        // Removes the pixels of the foliage that are gaps between leaves, and the foliage that the fire took.
        // Returns the distance of the pattern above the cut, which is small at a burning edge.
        // A triangle with a place in the Leaf Atlas takes its gaps from the alpha of the atlas.
        float ClipFoliage(Varyings i, out float consumed, out float4 leafTexel)
        {
            consumed = 0.0;
            leafTexel = float4(0.5, 0.0, 0.5, 1.0);
            bool foliage = i.surface.x > 0.5 && i.surface.x < 1.5;
            if (!foliage)
                return 1.0;
            bool textured = i.leaf.x >= 0.0;
            if (textured)
            {
                leafTexel = _LeafAtlas.Sample(sampler_LeafAtlas, i.leaf.xy);
                clip(leafTexel.a - 0.4);
            }
            float pattern = FoliagePattern(i.leafPosition);
            consumed = Consumed(i.surface.z, i.fire.x, i.fire.y);
            float cut = lerp(textured ? -0.01 : 0.30 * saturate(_Detail), 1.02, consumed);
            clip(pattern - cut);
            return pattern - cut;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            Varyings Vert(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
            {
                return VegetationVertex(vertexId, instanceId);
            }

            float3 FoliageColor(int model, float fuel)
            {
                float3 color = float3(0.040, 0.085, 0.032);                 // fir
                if (model == 1) color = float3(0.060, 0.105, 0.035);        // pine
                if (model == 2) color = float3(0.050, 0.110, 0.038);        // young conifer
                if (model == 3) color = float3(0.085, 0.150, 0.035);        // hardwood
                if (model == 4) color = float3(0.075, 0.140, 0.040);
                if (model == 5) color = float3(0.095, 0.125, 0.050);        // shrubs
                if (model == 6) color = float3(0.110, 0.130, 0.060);
                if (model == 7) color = float3(0.150, 0.160, 0.095);
                // Fuel model 6 is dormant brush.
                if (abs(fuel - 6.0) < 0.5 && model >= 5) color = float3(0.200, 0.150, 0.075);
                return color;
            }

            float4 Frag(Varyings i, bool frontFace : SV_IsFrontFace) : SV_Target
            {
                float consumed;
                float4 leafTexel;
                float edge = ClipFoliage(i, consumed, leafTexel);

                int model = (int)(i.plant.x + 0.5);
                float part = i.surface.x;
                float heightM = i.surface.z;
                float seed = i.surface.w;
                float burn = i.fire.x;
                bool foliage = part > 0.5 && part < 1.5;
                float mottle = Vegetation_Noise3(i.leafPosition * 2.1 + 5.0);

                float3 albedo;
                float3 emission = float3(0.0, 0.0, 0.0);
                if (foliage)
                {
                    albedo = FoliageColor(model, i.plant.y);
                    albedo *= (0.78 + 0.44 * seed) * (0.75 + 0.5 * mottle);
                    albedo.r *= 0.85 + 0.5 * frac(seed * 5.3);
                    // The Leaf Atlas gives the brightness of each leaf, a shift of its color, and the twigs.
                    albedo *= 2.0 * leafTexel.r * (0.85 + 0.3 * i.leaf.z);
                    albedo.rg *= float2(0.85 + 0.3 * leafTexel.b, 1.06 - 0.12 * leafTexel.b);
                    albedo = lerp(albedo, float3(0.165, 0.118, 0.082) * 2.0 * leafTexel.r, leafTexel.g);
                    // Scorch: brown below the scorch height, black where the fire is about to take the foliage.
                    float scorch = step(1.0e-4, burn) * saturate(burn * 2.0) * saturate((i.fire.z - heightM) / (0.3 * i.fire.z + 0.5));
                    albedo = lerp(albedo, float3(0.150, 0.085, 0.035) * (0.7 + 0.6 * mottle), scorch);
                    albedo = lerp(albedo, float3(0.020, 0.017, 0.015), saturate(consumed * 1.5));
                    // The burning edge of the foliage glows.
                    if (consumed > 0.0 && consumed < 1.0 && i.fire.w > 0.0)
                        emission = float3(1.0, 0.35, 0.05) * VEGETATION_EMBER_NITS * saturate(1.0 - edge / 0.07) * i.fire.w;
                }
                else
                {
                    albedo = part > 1.5 ? float3(0.230, 0.205, 0.175) : float3(0.165, 0.118, 0.082);
                    albedo *= (0.8 + 0.4 * seed) * (0.8 + 0.4 * mottle);
                    // Char: black up to the scorch height. Dead wood on the ground chars all over.
                    float reach = part > 1.5 && model != 10 ? 1.0 : saturate((1.5 * i.fire.y + 0.5 - heightM) / 1.0);
                    float charred = step(1.0e-4, burn) * saturate(burn * 3.0) * reach;
                    albedo = lerp(albedo, float3(0.022, 0.019, 0.017) * (0.7 + 0.6 * mottle), charred);
                    // Embers in the cracks of the charred wood.
                    float cracks = pow(saturate(Vegetation_Noise3(i.leafPosition * 6.0 + _Time.y * 0.3)), 5.0);
                    emission = float3(1.0, 0.25, 0.04) * VEGETATION_EMBER_NITS * cracks * charred * i.fire.w;
                }

                // A leaf card has one normal for both faces, which points out of the crown.
                float3 normal = normalize(i.normalWS);
                if (!frontFace && !foliage)
                    normal = -normal;
                float3 sun = _VegSunDirection.xyz;
                // Foliage lets light through, so the light wraps around it.
                float lambert = dot(normal, sun);
                float sunShare = foliage ? saturate(lambert * 0.6 + 0.4) : saturate(lambert);
                float shade = i.surface.y;
                float3 light = _VegSunColor.rgb * (sunShare * i.plant.z * lerp(1.0, shade, 0.6) / PI);
                light += _VegSkyColor.rgb * shade * (0.55 + 0.45 * normal.y);
                light += float3(1.0, 0.42, 0.12) * VEGETATION_FIRE_LIGHT_NITS * i.plant.w;
                float3 color = albedo * light + emission;
                return float4(color * GetCurrentExposureMultiplier(), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthForwardOnly"
            Tags { "LightMode" = "DepthForwardOnly" }
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            Varyings Vert(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
            {
                return VegetationVertex(vertexId, instanceId);
            }

            // The depth pass of HDRP also fills the normal buffer.
            float4 Frag(Varyings i, bool frontFace : SV_IsFrontFace) : SV_Target0
            {
                float consumed;
                float4 leafTexel;
                ClipFoliage(i, consumed, leafTexel);
                bool foliage = i.surface.x > 0.5 && i.surface.x < 1.5;
                NormalData normalData;
                normalData.normalWS = normalize(i.normalWS) * (frontFace || foliage ? 1.0 : -1.0);
                normalData.perceptualRoughness = 0.9;
                float4 normalBuffer;
                EncodeIntoNormalBuffer(normalData, normalBuffer);
                return normalBuffer;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ZClip Off
            Cull Off
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            Varyings Vert(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
            {
                return VegetationVertex(vertexId, instanceId);
            }

            void Frag(Varyings i)
            {
                float consumed;
                float4 leafTexel;
                ClipFoliage(i, consumed, leafTexel);
            }
            ENDHLSL
        }
    }
}
