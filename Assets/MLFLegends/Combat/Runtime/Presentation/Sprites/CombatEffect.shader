Shader "MLF Legends/Combat Effect"
{
    Properties { [NoScaleOffset] _MainTex ("Effect atlas page", 2D) = "white" {} }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }
        Pass
        {
            Tags { "LightMode" = "UniversalForwardOnly" }
            ZTest LEqual
            ZWrite Off
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Frame { float4 uv; float4 cardRect; };
            struct Instance { float3 position; float depthOffset; uint frame; float facing; float scale; float rotation; };
            StructuredBuffer<Frame> _SpriteFrames;
            StructuredBuffer<Instance> _EffectInstances;
            int _InstanceOffset;
            float4 _CardRight, _CardUp, _CardGround, _CardView;
            TEXTURE2D(_MainTex);
            struct Attributes { float3 positionOS : POSITION; uint id : SV_InstanceID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0;  };
            Varyings Vertex(Attributes input)
            {
                Instance instance = _EffectInstances[_InstanceOffset + (int)input.id];
                Frame frame = _SpriteFrames[instance.frame]; float4 card = frame.cardRect;
                float split = clamp(0.0, card.y, card.w);
                float y = input.positionOS.z > .5 ? lerp(card.y, split, input.positionOS.y) : lerp(split, card.w, input.positionOS.y);
                float x = lerp(card.x, card.z, input.positionOS.x);
                float v = card.w > card.y ? (y - card.y) / (card.w - card.y) : 0;
                float c = cos(instance.rotation), s = sin(instance.rotation);
                float2 rotated = float2(c * (x * instance.facing) - s * y, s * (x * instance.facing) + c * y);
                x = rotated.x; y = rotated.y;
                float3 p = instance.position + _CardRight.xyz * (x * instance.scale);
                p += y >= 0 ? _CardUp.xyz * (y * instance.scale) : _CardGround.xyz * (-y * instance.scale);
                p -= _CardView.xyz * instance.depthOffset;
                Varyings output; output.positionCS = TransformWorldToHClip(p);
                output.uv = float2(lerp(frame.uv.x, frame.uv.z, input.positionOS.x), lerp(frame.uv.y, frame.uv.w, v));
                return output;
            }
            half4 Fragment(Varyings input) : SV_Target
            {
                half4 color = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_PointClamp, input.uv, 0);
                clip(color.a - .001); return half4(color.rgb, color.a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
