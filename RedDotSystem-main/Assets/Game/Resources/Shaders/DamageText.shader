// Instanced damage text. One instance = one number string (up to 6 digits).
// Layout, glyph picking and billboard are done in the vertex shader; CPU only
// writes one instance transform + one packed digit word per number.
Shader "Game/DamageText"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
        _OutlineColor ("Outline Color", Color) = (0, 0, 0, 1)
        _OutlineWidth ("Outline Width", Range(0, 0.5)) = 0.12
        _Softness ("Softness", Range(0.01, 4)) = 1
    }

    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        LOD 100

        Pass
        {
            Name "DamageText"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _OutlineColor;
            float _OutlineWidth;
            float _Softness;

            // Atlas pixel size and monospace cell width (atlas pixels).
            float2 _AtlasSize;
            float _CellWidth;
            // x = uvMin.xy, z = uvMax.xy for glyph index (0-9 digits, 10 = minus).
            float4 _DigitUV[16];

            UNITY_INSTANCING_BUFFER_START(Props)
                // _Digits.x = digits packed as 4-bit nibbles (least significant first)
                // _Digits.y = valid digit count
                UNITY_DEFINE_INSTANCED_PROP(float4, _Digits)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Tint)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 slotuv : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);

                float4 digits = UNITY_ACCESS_INSTANCED_PROP(Props, _Digits);
                float4 tint = UNITY_ACCESS_INSTANCED_PROP(Props, _Tint);

                int count = (int)digits.y;
                int slot = (int)v.slotuv.x;

                if (slot >= count || count <= 0)
                {
                    // Collapse unused digit quads outside the clip volume.
                    o.pos = float4(2.0, 2.0, 2.0, 1.0);
                    o.uv = float2(0.0, 0.0);
                    o.color = float4(0.0, 0.0, 0.0, 0.0);
                    return o;
                }

                int nibble = count - 1 - slot;
                int glyph = ((int)digits.x >> (nibble * 4)) & 15;
                float4 rect = _DigitUV[glyph];

                float glyphW = (rect.z - rect.x) * _AtlasSize.x;
                float glyphH = (rect.w - rect.y) * _AtlasSize.y;

                float cellX = (slot - (count - 1) * 0.5) * _CellWidth;
                cellX += (_CellWidth - glyphW) * 0.5;

                float2 corner = v.uv - 0.5;
                float2 local = float2(cellX, 0.0) + corner * float2(glyphW, glyphH);

                // Instance matrix carries screen pixel position, z rotation and size.
                float4 world = mul(unity_ObjectToWorld, float4(local, 0.0, 1.0));

                // Screen pixels -> clip space. Drawn on top, depth is irrelevant.
                // _ProjectionParams.x is -1 when the target origin is flipped (D3D / render texture),
                // it must be applied here because this shader writes clip space directly.
                float2 ndc = world.xy / _ScreenParams.xy * 2.0 - 1.0;
                ndc.y *= _ProjectionParams.x;

                o.pos = float4(ndc, 0.5, 1.0);
                o.uv = lerp(rect.xy, rect.zw, v.uv);
                o.color = tint;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float d = tex2D(_MainTex, i.uv).a;
                float soft = max(fwidth(d), 0.0005) * _Softness;

                float fill = smoothstep(0.5 - soft, 0.5 + soft, d);
                float edge = smoothstep(0.5 - _OutlineWidth - soft, 0.5 - _OutlineWidth + soft, d);

                float3 rgb = lerp(_OutlineColor.rgb, i.color.rgb, fill);
                float a = saturate(edge) * i.color.a;

                clip(a - 0.004);
                return float4(rgb, a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
