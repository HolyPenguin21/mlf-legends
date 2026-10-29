// Stage 03 (#38): one instanced, alpha-clipped, depth-writing sprite card for combatants. Drawn only by
// CombatSpriteRenderer through Graphics.RenderMeshPrimitives (one call per atlas page); the per-instance data and the
// frame table are structured buffers indexed by SV_InstanceID. Geometry mirrors SpriteCardMath.CardToWorld exactly:
// above the pivot the card is vertical (world up / cos pitch), below it lies on the ground towards the camera
// (/ sin pitch), so the screen image is the art as drawn while depth is that of an upright body. See SPRITES.md.
Shader "MLF Legends/Combat Sprite"
{
    Properties
    {
        [NoScaleOffset] _MainTex ("Atlas page (set per draw)", 2D) = "white" {}
        _DamageTint ("Damage tint (alpha = intensity)", Color) = (1, 0, 0, 0.25)
        _AlphaCutoff ("Alpha cutoff", Range(0.01, 1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Layouts mirror SpriteFrameGpu and SpriteInstanceGpu (C#).
        struct SpriteFrame
        {
            float4 uv;       // u0, v0 (bottom), u1, v1 (top)
            float4 cardRect; // left, bottom, right, top in pixels relative to the pivot, y up, authored facing right
        };

        struct SpriteInstance
        {
            float3 position;    // CombatPosition (support point)
            float depthOffset;  // tie-break push towards the camera, world units along the view direction
            uint frame;
            float facing;       // +1 right, -1 left
            float damageFactor;
            float padding;
        };

        StructuredBuffer<SpriteFrame> _SpriteFrames;
        StructuredBuffer<SpriteInstance> _SpriteInstances;
        int _InstanceOffset;
        float4 _CardRight;
        float4 _CardUp;
        float4 _CardGround;
        float4 _CardView;
        float _PixelWorldSize;
        float _DepthBias;
        float4 _ImageOffsetCard; // artistic translation in authored card pixels; mirror X with the image

        TEXTURE2D(_MainTex);

        CBUFFER_START(UnityPerMaterial)
            float _AlphaCutoff;
            float4 _DamageTint;
        CBUFFER_END

        struct Attributes
        {
            float3 positionOS : POSITION; // x: column (0 left, 1 right), y: row within the quad (0 low, 1 high), z: 1 = quad below the pivot
            uint instanceID : SV_InstanceID;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            float3 normalWS : TEXCOORD1;
            nointerpolation float damageFactor : TEXCOORD2;
        };

        Varyings SpriteVertex(Attributes input)
        {
            SpriteInstance instance = _SpriteInstances[_InstanceOffset + (int)input.instanceID];
            SpriteFrame frame = _SpriteFrames[instance.frame];
            float4 card = frame.cardRect + _ImageOffsetCard.xyxy;

            // The pivot line splits the trimmed rect; a quad collapses when the rect lies entirely on one side.
            float pivotLine = clamp(0.0, card.y, card.w);
            bool below = input.positionOS.z > 0.5;
            float yPx = below ? lerp(card.y, pivotLine, input.positionOS.y) : lerp(pivotLine, card.w, input.positionOS.y);
            float xPx = lerp(card.x, card.z, input.positionOS.x);

            // UV follows the authored rect; facing only mirrors the geometry (pivot offset and image together).
            float v = card.w > card.y ? (yPx - card.y) / (card.w - card.y) : 0.0;
            Varyings output;
            output.uv = float2(lerp(frame.uv.x, frame.uv.z, input.positionOS.x), lerp(frame.uv.y, frame.uv.w, v));

            float s = _PixelWorldSize;
            float3 positionWS = instance.position + _CardRight.xyz * (xPx * instance.facing * s);
            positionWS += yPx >= 0.0 ? _CardUp.xyz * (yPx * s) : _CardGround.xyz * (-yPx * s);
            positionWS -= _CardView.xyz * (instance.depthOffset + _DepthBias);
            output.positionCS = TransformWorldToHClip(positionWS);
            output.damageFactor = instance.damageFactor;
            output.normalWS = below ? float3(0.0, 1.0, 0.0) : normalize(_CardGround.xyz);
            return output;
        }

        half4 SampleSprite(float2 uv)
        {
            // Own point/clamp sampler at mip 0: the page's import filter/wrap/mipmaps cannot blur or bleed pixels.
            half4 color = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_PointClamp, uv, 0);
            clip(color.a - _AlphaCutoff);
            return color;
        }
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForwardOnly" }
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex SpriteVertex
            #pragma fragment ForwardFragment

            half4 ForwardFragment(Varyings input) : SV_Target
            {
                half3 baseColor = SampleSprite(input.uv).rgb;
                return half4(lerp(baseColor, _DamageTint.rgb, saturate(input.damageFactor * _DamageTint.a)), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Off

            HLSLPROGRAM
            #pragma vertex SpriteVertex
            #pragma fragment DepthFragment

            half DepthFragment(Varyings input) : SV_Target
            {
                SampleSprite(input.uv);
                return input.positionCS.z;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormalsOnly"
            Tags { "LightMode" = "DepthNormalsOnly" }
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma vertex SpriteVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            void DepthNormalsFragment(Varyings input
                , out half4 outNormalWS : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
            )
            {
                SampleSprite(input.uv);
                float3 normalWS = normalize(input.normalWS);
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                outNormalWS = half4(PackFloat2To888(saturate(octNormalWS * 0.5 + 0.5)), 0.0);
            #else
                outNormalWS = half4(normalWS, 0.0);
            #endif
            #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
            }
            ENDHLSL
        }
    }

    Fallback Off
}
