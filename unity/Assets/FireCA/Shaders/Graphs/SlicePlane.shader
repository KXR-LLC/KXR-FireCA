// The slice plane: a vertical quad that cuts the Wind Grid and shows one field in color, with the terrain outline.
// The shader is a hand-written HDRP forward pass. The Wind Grid is staggered. See Wind.compute.
Shader "FireCA/SlicePlane"
{
    Properties
    {
        _Opacity("Opacity", Range(0, 1)) = 0.7
    }
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            // Field identifiers. The values match SlicePlane.Field in SlicePlane.cs.
            #define FIELD_WIND_SPEED 0
            #define FIELD_VERTICAL_VELOCITY 1
            #define FIELD_THETA 2
            #define FIELD_SMOKE 3

            Texture3D<float4> _FireCA_Velocity;
            Texture3D<float> _FireCA_Smoke;
            Texture3D<float> _FireCA_SolidMask;
            Texture2D<float> _FireCA_GroundHeight;
            Texture2D<float4> _FireCA_ColorMap;
            SamplerState fireca_linear_clamp_sampler;
            SamplerState fireca_point_clamp_sampler;
            float4 _FireCA_WindGrid;        // cells on X, Y, Z, and the cell size (m)
            float _FireCA_WindBottomY;
            float _Opacity;
            float _SliceField;

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                // The quad has an identity transform, so the object position is the world position.
                o.positionWS = v.positionOS;
                o.positionCS = TransformWorldToHClip(GetCameraRelativePositionWS(v.positionOS));
                return o;
            }

            float3 Diverging(float t)
            {
                // Blue for negative, white for zero, red for positive.
                float3 negative = float3(0.10, 0.30, 0.85), zero = float3(0.95, 0.95, 0.95), positive = float3(0.85, 0.15, 0.10);
                return t < 0.0 ? lerp(zero, negative, saturate(-t)) : lerp(zero, positive, saturate(t));
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float3 extent = _FireCA_WindGrid.xyz * _FireCA_WindGrid.w;
                float cell = _FireCA_WindGrid.w;
                float3 p = i.positionWS - float3(0.0, _FireCA_WindBottomY, 0.0);
                float3 uvw = p / extent;
                if (any(uvw < 0.0) || any(uvw > 1.0))
                    discard;

                // The terrain: a dark fill under the ground, and a light line on the ground.
                float ground = _FireCA_GroundHeight.SampleLevel(fireca_linear_clamp_sampler, uvw.xz, 0);
                float aboveGround = i.positionWS.y - ground;
                if (aboveGround < 0.0)
                    return float4(0.05, 0.045, 0.04, 0.85);
                if (aboveGround < 12.0)
                    return float4(0.95, 0.95, 0.9, 0.95);

                float half = 0.5 * cell;
                float3 velocity = float3(
                    _FireCA_Velocity.SampleLevel(fireca_linear_clamp_sampler, (p + float3(half, 0.0, 0.0)) / extent, 0).x,
                    _FireCA_Velocity.SampleLevel(fireca_linear_clamp_sampler, (p + float3(0.0, half, 0.0)) / extent, 0).y,
                    _FireCA_Velocity.SampleLevel(fireca_linear_clamp_sampler, (p + float3(0.0, 0.0, half)) / extent, 0).z);
                float theta = _FireCA_Velocity.SampleLevel(fireca_linear_clamp_sampler, uvw, 0).w;

                int field = (int)round(_SliceField);
                float3 color;
                float alpha = _Opacity;
                if (field == FIELD_VERTICAL_VELOCITY)
                {
                    color = Diverging(velocity.y / 5.0);                     // -5 to 5 m/s
                }
                else if (field == FIELD_THETA)
                {
                    color = Diverging(theta / 5.0);                          // -5 to 5 K
                }
                else if (field == FIELD_SMOKE)
                {
                    float smoke = _FireCA_Smoke.SampleLevel(fireca_linear_clamp_sampler, uvw, 0);
                    color = lerp(float3(0.9, 0.9, 0.9), float3(0.1, 0.1, 0.1), saturate(smoke));
                    alpha = _Opacity * saturate(0.15 + smoke);
                }
                else
                {
                    color = _FireCA_ColorMap.SampleLevel(fireca_linear_clamp_sampler, float2(saturate(length(velocity) / 20.0), 0.5), 0).rgb;   // 0 to 20 m/s
                }
                return float4(color * 0.9, alpha);
            }
            ENDHLSL
        }
    }
}
