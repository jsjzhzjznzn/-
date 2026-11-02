// 新手引导全屏遮罩（视觉层：挖洞；输入穿透由 GuideMaskGraphic.Raycast 负责）
// 洞矩形用遮罩本地坐标（GuideCanvas 为 ConstantPixelSize：1 单位 = 1 屏幕像素）
// 无洞（信息步）：_HoleRect.zw 传大负数 → SDF 恒为正 → 全屏均匀遮罩
Shader "Game/GuideMask"
{
    Properties
    {
        _HoleRect   ("洞矩形 (cx, cy, halfX, halfY) 局部px；half为负=无洞全遮罩", Vector) = (0, 0, -1000, -1000)
        _HoleRadius ("洞圆角半径 px", Float) = 10
        _SoftEdge   ("洞软边宽 px", Float) = 2
        _DimColor   ("遮罩色", Color) = (0, 0, 0, 0.72)
        _EdgeColor  ("洞边呼吸光颜色", Color) = (0.91, 0.767, 0.416, 1)
        _EdgeWidth  ("呼吸光带宽 px", Float) = 3
        _PulseSpeed ("呼吸速度", Float) = 3
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Mask"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 _HoleRect;
            float  _HoleRadius;
            float  _SoftEdge;
            half4  _DimColor;
            half4  _EdgeColor;
            float  _EdgeWidth;
            float  _PulseSpeed;

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 localPos  : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float3 worldPos = mul(GetObjectToWorldMatrix(), float4(input.positionOS.xyz, 1.0)).xyz;
                output.positionCS = TransformWorldToHClip(worldPos);
                output.localPos = input.positionOS.xy;
                return output;
            }

            // SDF 圆角矩形：内负外正，到边界的符号距离
            float sdRoundBox(float2 p, float2 halfSize, float radius)
            {
                float2 q = abs(p) - halfSize + radius;
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float d = sdRoundBox(input.localPos - _HoleRect.xy, _HoleRect.zw, _HoleRadius);

                // 洞外→1、洞内→0（软边过渡）：遮罩透明度
                float dim = smoothstep(0.0, _SoftEdge, d);

                // 洞边呼吸光带：以洞边界为中心的窄衰减带 × 正弦呼吸
                float band  = 1.0 - saturate(abs(d) / _EdgeWidth);
                float pulse = 0.55 + 0.45 * sin(_Time.y * _PulseSpeed);
                float glow  = band * band * pulse;

                half3 rgb = lerp(_DimColor.rgb, _EdgeColor.rgb, glow);
                half  a   = max(dim * _DimColor.a, glow * _EdgeColor.a);
                return half4(rgb, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
