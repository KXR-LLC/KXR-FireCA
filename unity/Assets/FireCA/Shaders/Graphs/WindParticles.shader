// Draws each Wind Particle as a streak along the velocity of the particle, colored by wind speed with the
// color map of the Surface wind Overlay (0 to 20 m/s). The shader is a hand-written HDRP forward pass.
// One particle is one quad of 6 vertices that faces the camera. The quad is at least 1 pixel wide.
Shader "FireCA/WindParticles"
{
    Properties
    {
        _Brightness("Brightness", Float) = 0.9
        _StreakSeconds("Streak length (s)", Float) = 0.12
        _SizeM("Particle size (m)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            Blend SrcAlpha One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            struct Particle
            {
                float4 positionAge;
                float4 velocitySeed;
            };

            StructuredBuffer<Particle> _Particles;
            Texture2D<float4> _FireCA_ColorMap;
            SamplerState fireca_linear_clamp_sampler;
            float _Brightness;
            float _StreakSeconds;
            float _SpeedScale;
            float _LifeS;
            float _SizeM;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR0;
                float2 quad : TEXCOORD0;    // x: 0 at the head and 1 at the tail, y: -1 to 1 across the streak
            };

            Varyings Vert(uint vertexId : SV_VertexID)
            {
                Varyings o;
                // Corners of the quad: (tail, side) for the two triangles 0 1 2 and 2 1 3.
                static const float2 corners[6] =
                {
                    float2(0.0, -1.0), float2(0.0, 1.0), float2(1.0, -1.0),
                    float2(1.0, -1.0), float2(0.0, 1.0), float2(1.0, 1.0),
                };
                Particle p = _Particles[vertexId / 6u];
                float2 corner = corners[vertexId % 6u];
                float3 velocity = p.velocitySeed.xyz;
                float speed = length(velocity);
                float age = p.positionAge.w;

                // The streak follows the on-screen motion of the particle. A large particle is at least as long as wide.
                float3 direction = speed > 1.0e-4 ? velocity / speed : float3(1.0, 0.0, 0.0);
                float streakM = max(speed * _SpeedScale * _StreakSeconds, 1.5 * _SizeM);
                float3 headWS = p.positionAge.xyz;
                float3 tailWS = headWS - direction * streakM;
                float4 headCS = TransformWorldToHClip(GetCameraRelativePositionWS(headWS));
                float4 tailCS = TransformWorldToHClip(GetCameraRelativePositionWS(tailWS));

                float3 color = _FireCA_ColorMap.SampleLevel(fireca_linear_clamp_sampler, float2(saturate(speed / 20.0), 0.5), 0).rgb;
                float fade = saturate(age) * saturate((_LifeS - age) * 0.5) * p.velocitySeed.w;
                o.color = float4(color * _Brightness, fade);
                o.quad = corner;

                // A particle with an end behind the camera is not drawn.
                if (headCS.w < 0.05 || tailCS.w < 0.05)
                {
                    o.positionCS = float4(0.0, 0.0, -1.0, 1.0);
                    o.color.a = 0.0;
                    return o;
                }

                // Widen the streak in screen space. The half width is in pixels.
                float2 headPx = headCS.xy / headCS.w * 0.5 * _ScreenSize.xy;
                float2 tailPx = tailCS.xy / tailCS.w * 0.5 * _ScreenSize.xy;
                float2 along = tailPx - headPx;
                float alongLength = length(along);
                along = alongLength > 1.0e-3 ? along / alongLength : float2(1.0, 0.0);
                float2 across = float2(-along.y, along.x);
                float4 endCS = corner.x > 0.5 ? tailCS : headCS;
                float pixelsPerMeter = abs(UNITY_MATRIX_P[1][1]) * 0.5 * _ScreenSize.y / endCS.w;
                float halfWidthPx = max(0.5 * _SizeM * pixelsPerMeter, 0.6);
                endCS.xy += across * corner.y * halfWidthPx * 2.0 * _ScreenSize.zw * endCS.w;
                o.positionCS = endCS;
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                // The streak fades toward the tail. A wide streak also fades toward both sides.
                // A wide streak also has a round head, and is dimmer, because a wide streak covers more pixels.
                float alpha = i.color.a * (1.0 - i.quad.x);
                if (_SizeM > 0.0)
                {
                    float side = saturate(1.0 - i.quad.y * i.quad.y);
                    alpha *= side * side * smoothstep(0.0, 0.25, i.quad.x) * lerp(1.0, 0.45, saturate(_SizeM / 40.0));
                }
                return float4(i.color.rgb, alpha);
            }
            ENDHLSL
        }
    }
}
