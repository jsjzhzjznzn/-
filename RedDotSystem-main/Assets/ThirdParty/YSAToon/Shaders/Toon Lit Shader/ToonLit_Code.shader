// ============================================================================
// YSA Toon / Toon Lit (Code) — 手写代码版
//
// 这是 "Assets/YSA Toon/Shaders/Toon Lit Shader/Toon Lit.shadergraph" 的 1:1 代码重写版。
// 依据：Lights Calculation.hlsl（图内 Custom Function 的原始 HLSL）+ shadergraph JSON
// 连线逐节点反推。属性名与默认值与 ShaderGraph 版完全一致，已有材质可直接切换 shader。
//
// 与 ShaderGraph 版的已知差异（均为可接受范围）：
//  * 未包含 Meta（GI）与 DepthNormals（SSAO）Pass——URP 默认 Forward 渲染器不依赖它们。
//  * _CastShadows 用运行时 discard 实现（ShaderGraph 版用编辑器 keyword），效果相同。
//  * 高光图/遮蔽图采样取 R 通道（与 ShaderGraph 的 float4→float1 截断一致）。
//  * GRADIENT_DIRECTION / Preview 等开关走运行时分支而非 keyword 变体，视觉结果相同。
// ============================================================================

Shader "YSA Toon/Toon Lit Code"
{
    Properties
    {
        // ---------- 基础贴图 ----------
        _BaseMap ("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _BaseMapTiling ("Base Map Tiling (xy)", Vector) = (1, 1, 0, 0)
        _Alpha_Clipping ("Alpha Clipping (Threshold)", Range(0, 1)) = 0.5

        // ---------- 渐变（半身渐变） ----------
        [Toggle] _Gradient_Enabled ("Gradient Enabled", Float) = 0
        [Enum(X, 0, Y, 1, Z, 2)] _GRADIENT_DIRECTION ("Gradient Direction", Float) = 0
        _GColor_1 ("Gradient Color 1 (Top)", Color) = (0, 0, 0, 0)
        _GColor_2 ("Gradient Color 2 (Bottom)", Color) = (0, 0, 0, 0)
        [Toggle] _Inverse_Colors ("Inverse Colors", Float) = 0
        _Gradient_Multiplier ("Gradient Multiplier", Float) = 1
        _Gradient_Offset ("Gradient Offset", Float) = 0

        // ---------- 主光照 ----------
        _Ambient ("Ambient (阴影侧颜色)", Color) = (0.254717, 0.254717, 0.254717, 0)
        _MainLightMidPoint ("Main Light MidPoint", Range(-1, 1)) = 0
        _MainLightSmoothness ("Main Light Smoothness", Range(0, 1)) = 0.1
        [Toggle] _MainLightSpecular ("Main Light Specular", Float) = 0

        // ---------- 附加光 ----------
        [Toggle] _AdditionalLightsEnabled ("Additional Lights Enabled", Float) = 1
        _AdditionalLightsMidPoint ("Additional Lights MidPoint", Range(-1, 1)) = 0
        // 注意：Smothness 是原资产的拼写，为兼容材质保留（勿改）
        _AdditionalLightsSmothness ("Additional Lights Smoothness", Range(0, 1)) = 1
        [Toggle] _AdditionalLightsSpecular ("Additional Lights Specular", Float) = 0

        // ---------- 高光 ----------
        _SpecularMidPoint ("Specular MidPoint", Range(0, 1)) = 0.9
        _SpecularSmoothness ("Specular Smoothness", Range(0, 1)) = 0.1
        _SpecularMap ("Specular Map (SDF)", 2D) = "gray" {}
        _SpecularMapPower ("Specular Map Power", Range(0, 1)) = 0
        [HDR] _SpecularTint ("Specular Tint", Color) = (1, 1, 1, 1)
        [Toggle] _Specular_Customize_Enabled ("Specular Texture Enabled", Float) = 0
        _Specular_Texture ("Specular Texture", 2D) = "white" {}
        [HDR] _Specular_Texture_Color ("Specular Texture Color", Color) = (1, 1, 1, 1)
        _Specular_Hide_On_Shadows ("Specular Hide On Shadows", Range(0, 1)) = 1

        // ---------- 边缘光 ----------
        [Toggle] _Rim_Enabled ("Rim Enabled", Float) = 0
        [HDR] _RimColor ("Rim Color (a=强度)", Color) = (1, 0, 0, 1)
        _RimMidPoint ("Rim MidPoint", Range(0, 1)) = 0.6
        _RimSmoothness ("Rim Smoothness", Range(0, 1)) = 0.1
        _DynamicRemap ("Dynamic Remap", Range(0, 1)) = 0.5
        _RimHideOnShadow ("Rim Hide On Shadow", Range(0, 1)) = 1

        // ---------- 阴影 ----------
        [Toggle] _ReceiveShadowsEnabled ("Main Light Shadows Enabled", Float) = 1
        [Toggle] _AdditionalLightShadows ("Additional Light Shadows", Float) = 1
        _Shadow_Smoothness ("Shadow Smoothness", Range(0, 1)) = 1
        _MainShadowsPower ("Main Shadows Power", Range(0, 1)) = 0.5
        _AdditionalShadowsPower ("Additional Shadows Power", Range(0, 1)) = 1
        _OcclusionMap ("Occlusion Map", 2D) = "white" {}
        _OcclusionPower ("Occlusion Power", Range(0, 1)) = 1

        // ---------- 自发光 ----------
        [Toggle] _Emission ("Emission Enabled", Float) = 0
        _EmissionMap ("Emission Map", 2D) = "white" {}
        [HDR] _EmissionColor ("Emission Color", Color) = (1, 1, 1, 0)

        // ---------- 脸部 SDF 着色 ----------
        [Toggle] _is_Face ("Is Face", Float) = 0
        [Toggle] _Preview_X ("Preview X", Float) = 0
        _X_Offset ("X Offset", Float) = 0
        _X_Pos_Multiplier ("X Position Multiplier", Float) = 10
        _X_Modifier ("X Modifier", Range(0, 1)) = 0
        [Toggle] _Preview_Y ("Preview Y", Float) = 0
        _Y_Offset ("Y Offset", Float) = 0
        _Y_Pos_Multiplier ("Y Position Multiplier", Float) = 10
        _Y_Modifier ("Y Modifier", Range(0, 1)) = 0
        [Toggle] _Preview_Z ("Preview Z", Float) = 0
        _Z_Offset ("Z Offset", Float) = 0
        _Z_Pos_Multiplier ("Z Position Multiplier", Float) = 10
        _Z_Modifier ("Z Modifier", Range(0, 1)) = 0

        // ---------- 描边配合（网格收缩） ----------
        _Shrink_Size ("Shrink Size", Range(-1, 1)) = -0.1

        // ---------- 材质级表面覆盖（AllowMaterialOverride，与 ShaderGraph 输出一致） ----------
        [HideInInspector] _CastShadows ("_CastShadows", Float) = 1
        [HideInInspector] _Surface ("_Surface", Float) = 0
        [HideInInspector] _Blend ("_Blend", Float) = 0
        [HideInInspector] _AlphaClip ("_AlphaClip", Float) = 0
        [HideInInspector] _SrcBlend ("_SrcBlend", Float) = 1
        [HideInInspector] _DstBlend ("_DstBlend", Float) = 0
        [HideInInspector] _ZWrite ("_ZWrite", Float) = 1
        [HideInInspector] _ZWriteControl ("_ZWriteControl", Float) = 0
        [HideInInspector] _ZTest ("_ZTest", Float) = 4
        [HideInInspector] _Cull ("_Cull", Float) = 2
        [HideInInspector] _AlphaToMask ("_AlphaToMask", Float) = 0
        [HideInInspector] _QueueOffset ("_QueueOffset", Float) = 0
        [HideInInspector] _QueueControl ("_QueueControl", Float) = -1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        HLSLINCLUDE
        // 原图依赖的库：图内 Custom Function 直接调用 GetMainLight 等，因此引入 Lighting.hlsl
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4   _BaseColor;
            float4  _BaseMapTiling;
            float   _Alpha_Clipping;

            float   _Gradient_Enabled;
            float   _GRADIENT_DIRECTION;
            half4   _GColor_1;
            half4   _GColor_2;
            float   _Inverse_Colors;
            float   _Gradient_Multiplier;
            float   _Gradient_Offset;

            half4   _Ambient;
            float   _MainLightMidPoint;
            float   _MainLightSmoothness;
            float   _MainLightSpecular;

            float   _AdditionalLightsEnabled;
            float   _AdditionalLightsMidPoint;
            float   _AdditionalLightsSmothness;   // 原资产拼写
            float   _AdditionalLightsSpecular;

            float   _SpecularMidPoint;
            float   _SpecularSmoothness;
            float   _SpecularMapPower;
            half4   _SpecularTint;
            float   _Specular_Customize_Enabled;
            half4   _Specular_Texture_Color;
            float   _Specular_Hide_On_Shadows;

            float   _Rim_Enabled;
            half4   _RimColor;
            float   _RimMidPoint;
            float   _RimSmoothness;
            float   _DynamicRemap;
            float   _RimHideOnShadow;

            float   _ReceiveShadowsEnabled;
            float   _AdditionalLightShadows;
            float   _Shadow_Smoothness;
            float   _MainShadowsPower;
            float   _AdditionalShadowsPower;
            float   _OcclusionPower;

            float   _Emission;
            half4   _EmissionColor;

            float   _is_Face;
            float   _Preview_X, _X_Offset, _X_Pos_Multiplier, _X_Modifier;
            float   _Preview_Y, _Y_Offset, _Y_Pos_Multiplier, _Y_Modifier;
            float   _Preview_Z, _Z_Offset, _Z_Pos_Multiplier, _Z_Modifier;
            float   _Shrink_Size;

            float   _CastShadows;
            float   _Surface, _Blend, _AlphaClip, _SrcBlend, _DstBlend;
            float   _ZWrite, _ZWriteControl, _ZTest, _Cull, _AlphaToMask;
            float   _QueueOffset, _QueueControl;
        CBUFFER_END

        TEXTURE2D(_BaseMap);          SAMPLER(sampler_BaseMap);
        TEXTURE2D(_EmissionMap);      SAMPLER(sampler_EmissionMap);
        TEXTURE2D(_SpecularMap);      SAMPLER(sampler_SpecularMap);
        TEXTURE2D(_Specular_Texture); SAMPLER(sampler_Specular_Texture);
        TEXTURE2D(_OcclusionMap);     SAMPLER(sampler_OcclusionMap);

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS   : NORMAL;
            float2 uv          : TEXCOORD0;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 normalWS   : TEXCOORD1;
            float3 positionOS : TEXCOORD2;   // 脸部 SDF 需要物体空间位置
            float3 normalOS   : TEXCOORD3;   // 脸部 SDF 需要物体空间法线
            float2 uv         : TEXCOORD4;
            half  fogFactor   : TEXCOORD5;
        };

        // --------------------------------------------------------------------
        // 以下函数体与 "Lights Calculation.hlsl" / "CameraFixMultiplier.hlsl" 保持一致
        // --------------------------------------------------------------------

        // 相机距离修正系数（原图 OutLine Shader 的 CameraFixMultiplier.hlsl，两图共用）
        float GetCameraFix(float positionVS_Z)
        {
            if (unity_OrthoParams.w == 0)
            {
                const float Rad2Deg = 57.2958;
                float fov = atan(1.0 / unity_CameraProjection._m11) * 2.0 * Rad2Deg;
                float fix = saturate(abs(positionVS_Z));   // 原版行为：saturate 之后才乘 FOV
                return fix * fov * 0.0001;
            }
            else
            {
                float orthoSize = saturate(abs(unity_OrthoParams.y));
                return orthoSize * 125.0 * 0.0001;
            }
        }

        // 脸部 SDF 法线生成（图内 FaceNormalsGenerator：物体空间法线与位置按 Modifier 逐轴混合）
        float3 FaceNormalsGenerator(float3 Position, float3 Normal,
            float PreviewX, float XOffset, float XPosMultiplier, float XModifier,
            float PreviewY, float YOffset, float YPosMultiplier, float YModifier,
            float PreviewZ, float ZOffset, float ZPosMultiplier, float ZModifier)
        {
            Position += float3(XOffset, YOffset, ZOffset);
            Position *= float3(XPosMultiplier, YPosMultiplier, ZPosMultiplier);

            float3 Fixed = float3(0, 0, 0);

            // 预览调试分支（与原版一致：单一通道刷屏，越界涂红）
            if (PreviewX > 0.5)
            {
                float r = Position.x, g = Position.x, b = Position.x;
                if (Position.x < -1 || Position.x > 1) { r = 1; g = 0; b = 0; }
                Fixed = float3(r, g, b);
            }
            if (PreviewY > 0.5)
            {
                float r = Position.y, g = Position.y, b = Position.y;
                if (Position.y < -1 || Position.y > 1) { r = 1; g = 0; b = 0; }
                Fixed = float3(r, g, b);
            }
            if (PreviewZ > 0.5)
            {
                float r = Position.z, g = Position.z, b = Position.z;
                if (Position.z < -1 || Position.z > 1) { r = 1; g = 0; b = 0; }
                Fixed = float3(r, g, b);
            }

            float FixedX = lerp(Normal.x, Position.x, XModifier);
            float FixedY = lerp(Normal.y, Position.y, YModifier);
            float FixedZ = lerp(Normal.z, Position.z, ZModifier);

            if (PreviewX < 0.5 && PreviewY < 0.5 && PreviewZ < 0.5)
                Fixed = float3(FixedX, FixedY, FixedZ);

            return Fixed;
        }

        // --------------------------------------------------------------------
        // 顶点阶段：网格收缩（原图 VertexDescription.Position）
        //   positionOS = TransformWorldToObject(WorldPos + normalize(WorldNormal) * (_Shrink_Size * CamFix))
        // --------------------------------------------------------------------
        Varyings ToonVert(Attributes input)
        {
            Varyings o;

            float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
            float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);

            float3 positionVS = TransformWorldToView(positionWS);
            float  camFix = GetCameraFix(positionVS.z);

            // 与描边 Shader 配套：把本体沿法线收缩一点，避免与描边壳 z-fight
            positionWS += normalize(normalWS) * (_Shrink_Size * camFix);

            o.positionWS = positionWS;
            o.normalWS    = normalWS;
            o.positionOS  = input.positionOS.xyz;
            o.normalOS    = input.normalOS;
            o.uv           = input.uv;
            o.positionCS   = TransformWorldToHClip(positionWS);
            o.fogFactor    = ComputeFogFactor(o.positionCS.z);
            return o;
        }

        // --------------------------------------------------------------------
        // Forward 片元：完整还原主图 + MainLightingCalculator 子图的连线
        // --------------------------------------------------------------------
        half4 ToonFrag(Varyings input) : SV_Target
        {
            // ---- 基础贴图（TilingAndOffset：UV * _BaseMapTiling，Offset 未接线=0）----
            float2 uvBase = input.uv * _BaseMapTiling.xy;
            half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvBase);

            // ---- 渐变（KeywordNode 按枚举选 X/Y/Z 通道 + Offset + Multiplier，再 saturate）----
            float3 objPos = input.positionOS;
            float posChannel = _GRADIENT_DIRECTION < 0.5 ? objPos.x
                             : (_GRADIENT_DIRECTION < 1.5 ? objPos.y : objPos.z);
            float gradT = saturate((posChannel + _Gradient_Offset) * _Gradient_Multiplier);

            half3 gradientColor = half3(1, 1, 1);
            if (_Gradient_Enabled > 0.5)
            {
                gradientColor = _Inverse_Colors > 0.5
                    ? lerp(_GColor_2.rgb, _GColor_1.rgb, gradT)
                    : lerp(_GColor_1.rgb, _GColor_2.rgb, gradT);
            }

            // ---- 反照率 = BaseColor * 渐变色 * 贴图 ----
            half3 albedo = _BaseColor.rgb * gradientColor * baseTex.rgb;
            half  alpha  = _BaseColor.a * baseTex.a;
            if (_AlphaClip > 0.5)
                clip(alpha - _Alpha_Clipping);

            // ---- 法线 ----
            float3 N = normalize(input.normalWS);
            float3 V = normalize(GetWorldSpaceViewDir(input.positionWS));

            // 脸部：SDF 法线修正后转世界空间；非脸部直接用世界法线
            float3 faceFixed = FaceNormalsGenerator(
                input.positionOS, input.normalOS,
                _Preview_X, _X_Offset, _X_Pos_Multiplier, _X_Modifier,
                _Preview_Y, _Y_Offset, _Y_Pos_Multiplier, _Y_Modifier,
                _Preview_Z, _Z_Offset, _Z_Pos_Multiplier, _Z_Modifier);
            float3 N_shading = _is_Face > 0.5 ? mul((float3x3)GetObjectToWorldMatrix(), faceFixed) : N;

            // ---- 主光 diffuse 色带（MainLightCalculation）----
            Light mainLight = GetMainLight();
            float ndl = dot(mainLight.direction, N_shading);
            float mainDiffuse = smoothstep(_MainLightMidPoint, _MainLightMidPoint + _MainLightSmoothness, ndl)
                              * mainLight.distanceAttenuation;

            // ---- 遮蔽图（MainShadowsCalculator 子图内：occlusion = lerp(1, occ.r, power)）----
            half occTex = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, input.uv).r;
            float occlusion = lerp(1.0, occTex, _OcclusionPower);

            // ---- 主光阴影系数（MainShadowsCalculation）----
            float mainShadow;
            if (_ReceiveShadowsEnabled > 0.5)
            {
                #if defined(SHADOWS_SCREEN)
                    float4 clipPos = TransformWorldToHClip(input.positionWS);
                    float4 shadowCoord = ComputeScreenPos(clipPos);
                #else
                    float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #endif
                float sh = GetMainLight(shadowCoord).shadowAttenuation;
                sh = smoothstep(0.0, _Shadow_Smoothness, sh);
                sh = max(sh, 1.0 - _MainShadowsPower);
                mainShadow = sh * occlusion;
            }
            else
            {
                mainShadow = occlusion;
            }

            float diffShadow = mainDiffuse * mainShadow;

            // ---- 主光照色（MainLightingCalculator：lerp(Ambient, LightColor, diffShadow) * albedo）----
            half3 mainLit = lerp(_Ambient.rgb, mainLight.color, diffShadow) * albedo;

            // ---- 高光图（SpecularMap remap 到 [-1,1] 再乘 Power，取 R 通道）----
            half specTex = SAMPLE_TEXTURE2D(_SpecularMap, sampler_SpecularMap, input.uv).r;
            float specMapVal = lerp(0.0, specTex * 2.0 - 1.0, _SpecularMapPower);

            // ---- 主光高光（SpecularMainLightingCalculation，注意用的是原始世界法线 N）----
            half3 specMain = half3(0, 0, 0);
            if (_MainLightSpecular > 0.5)
            {
                float3 h = normalize(V + mainLight.direction) + specMapVal;
                float s = dot(N, h);
                s = smoothstep(_SpecularMidPoint, _SpecularMidPoint + _SpecularSmoothness, s);
                specMain = saturate(s) * _SpecularTint.rgb * _SpecularTint.a
                         * mainLight.distanceAttenuation * mainLight.color;
            }

            // ---- 边缘光（RimLightingCalculation：RimColor.a 当强度）----
            half3 rimRaw = half3(0, 0, 0);
            if (_Rim_Enabled > 0.5)
            {
                float rimDot = 1.0 - dot(V, N);
                float ndlRaw = dot(mainLight.direction, N);
                float factor = lerp(1.0, ndlRaw, _DynamicRemap);
                float mid = lerp(1.0, _RimMidPoint, factor);
                float rim = smoothstep(mid, mid + _RimSmoothness, rimDot);
                rimRaw = saturate(rim) * _RimColor.rgb * _RimColor.a;
            }
            half3 rimTerm = lerp(rimRaw, rimRaw * diffShadow, _RimHideOnShadow);

            // ---- 贴图高光（Specular Texture：贴图 rgb * SpecularTextureColor.rgb * .a）----
            half3 customSpec = half3(0, 0, 0);
            if (_Specular_Customize_Enabled > 0.5)
            {
                half4 specTex4 = SAMPLE_TEXTURE2D(_Specular_Texture, sampler_Specular_Texture, input.uv);
                customSpec = specTex4.rgb * _Specular_Texture_Color.rgb * _Specular_Texture_Color.a;
            }
            half3 customSpecTerm = lerp(customSpec, customSpec * diffShadow, _Specular_Hide_On_Shadows);

            // ---- 三种效果逐分量取 max 再加到主光照上（原图 MaximumNode 链）----
            half3 maxTerm = max(max(diffShadow * specMain, rimTerm), customSpecTerm);
            half3 mainOut = mainLit + maxTerm;

            // ---- 附加光（AdditionalLightingCalcualtor 子图）----
            half3 addOut = half3(0, 0, 0);
            uint addCount = GetAdditionalLightsCount();
            if (_AdditionalLightsEnabled > 0.5)
            {
                // diffuse 色带逐光累加
                half3 addDiffuse = half3(0, 0, 0);
                for (uint k = 0u; k < addCount; k++)
                {
                    Light l = GetAdditionalLight((int)k, input.positionWS, half4(1, 1, 1, 1));
                    float d = dot(l.direction, N);
                    d = smoothstep(_AdditionalLightsMidPoint, _AdditionalLightsMidPoint + _AdditionalLightsSmothness, d);
                    d = saturate(d);
                    addDiffuse += d * l.distanceAttenuation * l.color;
                }

                // 高光：原版 HLSL 用 "=" 而非 "+="（每盏灯覆盖，最后一盏生效），忠实保留
                half3 addSpec = half3(0, 0, 0);
                if (_AdditionalLightsSpecular > 0.5)
                {
                    for (uint k2 = 0u; k2 < addCount; k2++)
                    {
                        Light l = GetAdditionalLight((int)k2, input.positionWS, half4(1, 1, 1, 1));
                        float3 h = SafeNormalize(V + l.direction) + specMapVal;
                        float s = dot(N, h);
                        s = smoothstep(_SpecularMidPoint, _SpecularMidPoint + _SpecularSmoothness, s);
                        addSpec = saturate(s) * _SpecularTint.rgb * _SpecularTint.a
                                * l.distanceAttenuation * l.color;
                    }
                }
                addOut = addDiffuse + addSpec;
            }

            // ---- 附加光阴影（AdditionalShadowsCalculator：逐光相乘，floor 1-power）----
            float addShadowFactor = 1.0;
            if (_AdditionalLightShadows > 0.5)
            {
                for (uint k3 = 0u; k3 < addCount; k3++)
                {
                    Light l = GetAdditionalLight((int)k3, input.positionWS, half4(1, 1, 1, 1));
                    float sh = l.shadowAttenuation;
                    sh = smoothstep(0.0, _Shadow_Smoothness, sh);
                    sh = max(sh, 1.0 - _AdditionalShadowsPower);
                    addShadowFactor *= sh;
                }
            }

            // ---- 自发光 ----
            half3 emission = half3(0, 0, 0);
            if (_Emission > 0.5)
            {
                half4 emisTex = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, input.uv);
                emission = emisTex.rgb * _EmissionColor.rgb;
            }

            // ---- 汇总（图：Emission + Main + Additional*Shadow）----
            half3 finalColor = mainOut + addOut * addShadowFactor + emission;

            // ---- 预览调试分支（任一 Preview 开关打开时直接显示修正后的法线）----
            if (_Preview_X > 0.5 || _Preview_Y > 0.5 || _Preview_Z > 0.5)
                finalColor = faceFixed;

            half4 outColor = half4(finalColor, alpha);
            outColor.rgb = MixFog(outColor.rgb, input.fogFactor);
            return outColor;
        }
        ENDHLSL

        // ======================= Forward（主要 Pass）=======================
        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            ZTest  [_ZTest]
            Cull   [_Cull]
            AlphaToMask [_AlphaToMask]

            HLSLPROGRAM
            #pragma vertex ToonVert
            #pragma fragment ToonFrag
            #pragma target 3.5

            // 与 ShaderGraph URP 目标一致的管线关键字
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            ENDHLSL
        }

        // ======================= ShadowCaster（投影）=======================
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            Cull [_Cull]
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma target 3.5

            float3 _LightDirection;   // URP ShadowCaster 传参（CastingShadowsFromDirectionalLight）

            struct AttributesShadow
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct VaryingsShadow
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            VaryingsShadow ShadowVert(AttributesShadow input)
            {
                VaryingsShadow o;

                // 与 Forward 一致：应用顶点收缩（VertexDescription 全 Pass 生效）
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);
                float camFix = GetCameraFix(TransformWorldToView(positionWS).z);
                positionWS += normalize(normalWS) * (_Shrink_Size * camFix);

                float3 positionWSBias = ApplyShadowBias(positionWS, normalWS, _LightDirection);
                o.positionCS = TransformWorldToHClip(positionWSBias);
                #if UNITY_REVERSED_Z
                    o.positionCS.z = min(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    o.positionCS.z = max(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.uv = input.uv;
                return o;
            }

            half4 ShadowFrag(VaryingsShadow input) : SV_Target
            {
                // 材质级投影开关（ShaderGraph 表面覆盖属性）
                if (_CastShadows < 0.5)
                    discard;

                if (_AlphaClip > 0.5)
                {
                    half a = _BaseColor.a * SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv * _BaseMapTiling.xy).a;
                    clip(a - _Alpha_Clipping);
                }
                return 0;
            }
            ENDHLSL
        }

        // ======================= DepthOnly（深度 prepass）=======================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma target 3.5

            struct VaryingsDepth
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            VaryingsDepth DepthVert(Attributes input)
            {
                VaryingsDepth o;

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);
                float camFix = GetCameraFix(TransformWorldToView(positionWS).z);
                positionWS += normalize(normalWS) * (_Shrink_Size * camFix);

                o.positionCS = TransformWorldToHClip(positionWS);
                o.uv = input.uv;
                return o;
            }

            half4 DepthFrag(VaryingsDepth input) : SV_Target
            {
                if (_AlphaClip > 0.5)
                {
                    half a = _BaseColor.a * SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv * _BaseMapTiling.xy).a;
                    clip(a - _Alpha_Clipping);
                }
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
