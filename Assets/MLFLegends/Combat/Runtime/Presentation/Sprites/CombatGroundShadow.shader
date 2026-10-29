Shader "MLF Legends/Combat Ground Shadow"
{
    Properties { _ShadowColor ("Shadow color", Color) = (0, 0, 0, 0.35) }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent-10" }
        Pass
        {
            Name "GroundShadow"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct GroundShadow { float4 positionRadius, tangentX, tangentZ, clipX, clipZ; };
            StructuredBuffer<GroundShadow> _GroundShadows;
            CBUFFER_START(UnityPerMaterial)
                half4 _ShadowColor;
            CBUFFER_END
            struct Attributes { float3 positionOS : POSITION; uint instanceID : SV_InstanceID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 disk : TEXCOORD0; float2 surface : TEXCOORD1; nointerpolation float2 halfSize : TEXCOORD2; };
            Varyings Vert(Attributes input)
            {
                GroundShadow s = _GroundShadows[input.instanceID];
                float3 p = s.positionRadius.xyz + (s.tangentX.xyz * input.positionOS.x + s.tangentZ.xyz * input.positionOS.y) * s.positionRadius.w;
                Varyings o; o.positionCS = TransformWorldToHClip(p); o.disk = input.positionOS.xy;
                o.surface = float2(dot(p.xz, s.clipX.xy) - s.clipX.z, dot(p.xz, s.clipZ.xy) - s.clipZ.z);
                o.halfSize = float2(s.clipX.w, s.clipZ.w); return o;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                float radius2 = dot(input.disk, input.disk);
                clip(1 - radius2); clip(input.halfSize - abs(input.surface));
                half edge = 1 - smoothstep(0.8, 1.0, radius2);
                return half4(_ShadowColor.rgb, _ShadowColor.a * edge);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
