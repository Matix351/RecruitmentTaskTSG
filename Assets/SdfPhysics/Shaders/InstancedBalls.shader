Shader "SDF Physics/Instanced Balls"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Pass
        {
            Name "Forward"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            StructuredBuffer<float4> _Balls;
            struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL; uint id : SV_InstanceID; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 color : TEXCOORD1; };
            Varyings Vert(Attributes input)
            {
                Varyings o;
                float4 ball = _Balls[input.id];
                o.positionCS = TransformWorldToHClip(ball.xyz + input.positionOS * ball.w);
                o.normalWS = input.normalOS;
                float t = frac(input.id * 0.61803398875);
                o.color = lerp(float3(0.06, 0.45, 0.63), float3(0.55, 0.88, 0.75), t);
                return o;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                Light light = GetMainLight();
                float diffuse = saturate(dot(normalize(input.normalWS), light.direction));
                return half4(input.color * (0.24 + 0.76 * diffuse * light.color), 1);
            }
            ENDHLSL
        }
    }
}
