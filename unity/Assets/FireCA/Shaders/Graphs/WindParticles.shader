// Draws each Wind Particle as a streak along the velocity of the particle, colored by wind speed with the
// color map of the Surface wind Overlay (0 to 20 m/s). The shader is a hand-written HDRP forward pass.
Shader "FireCA/WindParticles"
{
    Properties
    {
        _Brightness("Brightness", Float) = 0.9
        _StreakSeconds("Streak length (s)", Float) = 0.12
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

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR0;
            };

            Varyings Vert(uint vertexId : SV_VertexID)
            {
                Varyings o;
                Particle p = _Particles[vertexId >> 1];
                float tail = (float)(vertexId & 1u);
                float3 velocity = p.velocitySeed.xyz;
                float speed = length(velocity);
                float age = p.positionAge.w;

                // The streak follows the on-screen motion of the particle.
                float3 positionWS = p.positionAge.xyz - velocity * (_SpeedScale * _StreakSeconds * tail);
                o.positionCS = TransformWorldToHClip(GetCameraRelativePositionWS(positionWS));

                float3 color = _FireCA_ColorMap.SampleLevel(fireca_linear_clamp_sampler, float2(saturate(speed / 20.0), 0.5), 0).rgb;
                float fade = saturate(age) * saturate((_LifeS - age) * 0.5) * p.velocitySeed.w;
                o.color = float4(color * _Brightness, fade * (1.0 - tail));
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                return i.color;
            }
            ENDHLSL
        }
    }
}
