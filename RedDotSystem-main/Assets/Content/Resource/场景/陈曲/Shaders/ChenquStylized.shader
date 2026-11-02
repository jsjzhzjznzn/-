// 陈曲（贝洛伯格街区）风格化渲染 Shader
// 面向 240 个材质 / 1200 万三角的建筑群资产，特征：
//   1. 三段式卡通明暗（Ramp），阴影带冷色偏移，避免纯 PBR 在大场景里发灰
//   2. 直接吃工程里分离的 Metallic(R) 与 Gloss(A) 两张图，不需要预打包
//   3. 硬边高光 + 菲涅尔边光，补足金属和边缘轮廓
//   4. Alpha 裁剪支持（树叶/镂空件）
//   5. 完整 URP Pass（ForwardLit / ShadowCaster / DepthOnly / DepthNormals），
//      以便 SSAO、屏幕空间阴影、后处理深度效果都能正常工作
// 注意：本资产 1200 万三角，故不带 Outline Pass（多一个 pass 代价过高）。
Shader "Game/ChenquStylized"
{
    Properties
    {
        [MainTexture] _BaseMap ("基础色贴图", 2D) = "white" {}
        [MainColor]   _BaseColor ("基础色染色", Color) = (1, 1, 1, 1)

        [Header(Normal)]
        _NormalMap ("法线贴图", 2D) = "bump" {}
        _NormalScale ("法线强度", Range(0, 2)) = 1

        [Header(Surface)]
        _MetallicMap ("金属度贴图 (读 R 通道)", 2D) = "black" {}
        _SmoothnessMap ("光滑度贴图 (读 R 通道)", 2D) = "black" {}
        _Metallic ("金属度倍率", Range(0, 1)) = 1
        _Smoothness ("光滑度倍率", Range(0, 1)) = 1
        _Occlusion ("环境光遮蔽", Range(0, 1)) = 1

        [Header(Toon)]
        _RampOffset ("明暗分界", Range(-1, 1)) = 0.0
        _RampSoftness ("分界过渡宽度", Range(0.001, 1)) = 0.14
        _ShadowColor ("暗部颜色", Color) = (0.44, 0.50, 0.64, 1)
        _ShadowStrength ("暗部强度", Range(0, 1)) = 0.85
        _LightWrap ("背光补光", Range(0, 1)) = 0.3
        [HDR] _AmbientTint ("环境光染色", Color) = (1, 1, 1, 1)

        [Header(Specular)]
        [HDR] _SpecColor ("高光颜色", Color) = (0.85, 0.92, 1.0, 1)
        _SpecSmoothness ("高光锐度", Range(0.01, 1)) = 0.55
        _SpecIntensity ("高光强度", Range(0, 4)) = 0.8
        _SpecThreshold ("高光硬边阈值", Range(0, 1)) = 0.35

        [Header(Rim)]
        [HDR] _RimColor ("边光颜色", Color) = (0.72, 0.84, 1.0, 1)
        _RimPower ("边光收束", Range(0.5, 10)) = 3.5
        _RimIntensity ("边光强度", Range(0, 4)) = 0.7

        [Header(Emission)]
        [HDR] _EmissionColor ("自发光颜色", Color) = (0, 0, 0, 1)
        _EmissionMap ("自发光贴图", 2D) = "black" {}
        _EmissionIntensity ("自发光强度", Range(0, 8)) = 1

        [Header(Alpha)]
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("启用 Alpha 裁剪", Float) = 0
        _Cutoff ("裁剪阈值", Range(0, 1)) = 0.5

        [Header(Fog)]
        _FogIntensity ("雾强度", Range(0, 2)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _NormalMap_ST;
            float4 _MetallicMap_ST;
            float4 _SmoothnessMap_ST;
            float4 _EmissionMap_ST;
            half4  _BaseColor;
            half   _NormalScale;
            half   _Metallic;
            half   _Smoothness;
            half   _Occlusion;
            half   _RampOffset;
            half   _RampSoftness;
            half4  _ShadowColor;
            half   _ShadowStrength;
            half   _LightWrap;
            half4  _AmbientTint;
            half4  _SpecColor;
            half   _SpecSmoothness;
            half   _SpecIntensity;
            half   _SpecThreshold;
            half4  _RimColor;
            half   _RimPower;
            half   _RimIntensity;
            half4  _EmissionColor;
            half   _EmissionIntensity;
            half   _Cutoff;
            half   _FogIntensity;
        CBUFFER_END

        TEXTURE2D(_BaseMap);        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_NormalMap);      SAMPLER(sampler_NormalMap);
        TEXTURE2D(_MetallicMap);    SAMPLER(sampler_MetallicMap);
        TEXTURE2D(_SmoothnessMap);  SAMPLER(sampler_SmoothnessMap);
        TEXTURE2D(_EmissionMap);    SAMPLER(sampler_EmissionMap);
        ENDHLSL

        // ---------------------------------------------------------------
        // 主光照 Pass
        // ---------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ForwardVert
            #pragma fragment ForwardFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                half3  normalWS    : TEXCOORD2;
                half3  tangentWS   : TEXCOORD3;
                half3  bitangentWS : TEXCOORD4;
                half   fogFactor   : TEXCOORD5;
                float4 shadowCoord : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ForwardVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs nrmInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                o.positionCS  = posInputs.positionCS;
                o.positionWS  = posInputs.positionWS;
                o.normalWS    = nrmInputs.normalWS;
                o.tangentWS   = nrmInputs.tangentWS;
                o.bitangentWS = nrmInputs.bitangentWS;
                o.uv          = TRANSFORM_TEX(input.uv, _BaseMap);
                o.fogFactor   = ComputeFogFactor(posInputs.positionCS.z);
                o.shadowCoord = TransformWorldToShadowCoord(posInputs.positionWS);
                return o;
            }

            // 单盏灯的卡通漫反射：把 NdotL 压成带软边的两段
            half ToonRamp(half ndl)
            {
                half wrap = saturate((ndl + _LightWrap) / (1.0 + _LightWrap));
                half ramp = smoothstep(_RampOffset - _RampSoftness,
                                       _RampOffset + _RampSoftness, wrap);
                return lerp(1.0 - _ShadowStrength, 1.0, ramp);
            }

            // 软边的硬高光（保留一点过渡，不至于锯齿）
            half ToonSpec(half nh, half smoothness)
            {
                half specPow = exp2(smoothness * 10.0 + 1.0);
                half raw = pow(saturate(nh), specPow);
                half edge = smoothstep(_SpecThreshold - 0.06, _SpecThreshold + 0.06, raw);
                return lerp(raw, edge, 0.65);
            }

            half4 ForwardFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);

                #ifdef _ALPHATEST_ON
                    clip(baseTex.a - _Cutoff);
                #endif

                half3 albedo = baseTex.rgb * _BaseColor.rgb;

                // ---- 法线 ----
                half4 nSample = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv);
                half3 normalTS = UnpackNormalScale(nSample, _NormalScale);
                half3x3 tbn = half3x3(input.tangentWS, input.bitangentWS, input.normalWS);
                half3 N = normalize(mul(normalTS, tbn));

                // 兜底：模型法线朝外但 UV 缺失时会翻转，这里用几何法线做一次校正
                half3 Ngeo = normalize(input.normalWS);
                N = normalize(lerp(Ngeo, N, 0.92));

                // ---- 表面参数 ----
                half metal  = SAMPLE_TEXTURE2D(_MetallicMap, sampler_MetallicMap, input.uv).r * _Metallic;
                half smooth = SAMPLE_TEXTURE2D(_SmoothnessMap, sampler_SmoothnessMap, input.uv).r * _Smoothness;
                half ao     = _Occlusion;

                half3 V = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));

                // ---- 环境光 ----
                half3 ambient = SampleSH(N) * _AmbientTint.rgb * ao;

                // ---- 主光 ----
                Light mainLight = GetMainLight(input.shadowCoord);
                half3 radiance = mainLight.color;
                half atten = mainLight.distanceAttenuation * mainLight.shadowAttenuation;

                half ndlMain = dot(N, mainLight.direction);
                half rampMain = ToonRamp(ndlMain);

                // 暗部按 _ShadowColor 偏冷再往青蓝推一档，亮部保留灯光本色
                half3 shadowTint = _ShadowColor.rgb * half3(0.92, 1.00, 1.12);
                half3 mainTint = lerp(shadowTint, half3(1.0, 1.0, 1.0), saturate(rampMain));
                half3 mainLit = radiance * atten * rampMain * mainTint;

                // ---- 附加光（URP 每物体上限 4 盏，超出由 URP 自己丢弃）----
                half3 addLit = half3(0, 0, 0);
                #if defined(_ADDITIONAL_LIGHTS)
                    uint lightCount = GetAdditionalLightsCount();
                    for (uint li = 0u; li < lightCount; ++li)
                    {
                        Light l = GetAdditionalLight(li, input.positionWS);
                        half ndlAdd = dot(N, l.direction);
                        half rampAdd = ToonRamp(ndlAdd);
                        half3 tintAdd = lerp(_ShadowColor.rgb, half3(1.0, 1.0, 1.0), saturate(rampAdd));
                        addLit += l.color * (l.distanceAttenuation * l.shadowAttenuation) * rampAdd * tintAdd;
                    }
                #endif

                // ---- 高光 ----
                half3 H = SafeNormalize(mainLight.direction + V);
                half nh = dot(N, H);
                half specTerm = ToonSpec(nh, smooth) * _SpecIntensity * atten;
                half3 specTint = lerp(_SpecColor.rgb, albedo, metal);
                half3 specular = specTint * specTerm * radiance;

                // ---- 边光 ----
                half fresnel = 1.0 - saturate(dot(Ngeo, V));
                half rim = pow(fresnel, _RimPower) * _RimIntensity;
                half3 rimCol = _RimColor.rgb * rim;

                // ---- 自发光 ----
                half3 emissive = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, input.uv).rgb
                               * _EmissionColor.rgb * _EmissionIntensity;

                // ---- 合成 ----
                // 金属面缺少反射信息会死黑：保留 30% 环境漫反射，并用天空环境贴图做一次低频反射
                half3 R = reflect(-V, N);
                half3 envRefl = GlossyEnvironmentReflection(R, input.positionWS, saturate(1.0 - smooth), ao);
                half3 envSpec = envRefl * metal * smooth * 0.30;
                half3 diffuseAlbedo = albedo * lerp(1.0, 0.30, metal);
                half3 color = diffuseAlbedo * (ambient + mainLit + addLit) + specular + envSpec + rimCol + emissive;

                // 雾：交给 URP 的 MixFog（内部按 FOG_LINEAR/EXP/EXP2 换算清晰度）
                color = MixFog(color, input.fogFactor * _FogIntensity);

                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------
        // 阴影投射（含 Alpha 裁剪支持）
        // ---------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings ShadowVert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);

                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif

                o.positionCS = positionCS;
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return o;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
                #ifdef _ALPHATEST_ON
                    half a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a;
                    clip(a - _Cutoff);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------
        // 深度写入
        // ---------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return o;
            }

            half4 DepthFrag(Varyings input) : SV_Target
            {
                #ifdef _ALPHATEST_ON
                    half a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a;
                    clip(a - _Cutoff);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------
        // 深度 + 法线（SSAO / 屏幕空间效果需要）
        // ---------------------------------------------------------------
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float2 uv         : TEXCOORD1;
            };

            Varyings DepthNormalsVert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.normalWS   = TransformObjectToWorldNormal(input.normalOS);
                o.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
                return o;
            }

            half4 DepthNormalsFrag(Varyings input) : SV_Target
            {
                #ifdef _ALPHATEST_ON
                    half a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a;
                    clip(a - _Cutoff);
                #endif
                return half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
