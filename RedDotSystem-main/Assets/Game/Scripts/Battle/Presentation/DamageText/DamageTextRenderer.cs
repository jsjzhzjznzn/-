using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TextCore;

/// <summary>
/// 一条待绘制的飘字（由 DamageTextManager 每帧填好，渲染层只负责提交）
/// </summary>
public struct DamageTextDrawData
{
    public Vector2 origin;      // 屏幕像素坐标（数字中心）
    public float rotation;      // 绕 Z 的角度
    public float scale;         // 动画缩放乘数（1 = 基准）
    public float fontSize;      // 目标字号（像素高度基准）
    public int packed;          // 数字按 4bit/位打包，低位是最右边一位
    public int digitCount;      // 有效位数
    public Color color;
}

/// <summary>
/// 实例化伤害飘字渲染器。
///
/// 【为什么不用 UGUI/TMP 逐条渲染】
///   一条飘字一个 CanvasRenderer 时，1000 条就是 1000 份文本网格；
///   任何一条改文本都会触发整个 Canvas 重建，成本随元素数线性上升。
///   这里把「一条飘字」压成「一个 GPU 实例」：网格固定为 6 个四边形，
///   排版（逐位取字形、字距、居中）、倾斜、缩放、billboard 全在顶点着色器里算，
///   CPU 每帧只写实例矩阵与一个打包好的数字字。
///
/// 【字形来源】
///   直接采样工程现有 TMP_FontAsset 的 SDF 图集，不额外做美术资源；
///   UV 与字宽在初始化时读一次 glyphRect，字体图集扩容后自动重读。
/// </summary>
public class DamageTextRenderer : MonoBehaviour
{
    private const int MaxInstancesPerBatch = 1023;   // DrawMeshInstanced 单批上限
    private const int MaxDigits = 6;                 // 一条飘字最多 6 位
    private const int GlyphSlots = 16;               // 着色器里的 _DigitUV 数组长度
    private const int MinusGlyph = 10;               // '-' 的字形下标

    private TMP_FontAsset _font;
    private Mesh _mesh;
    private Material _material;
    private Texture _atlas;
    private int _atlasWidth;
    private int _atlasHeight;

    private int _digitRefHeight = 1;   // '0' 的字形高度（图集像素），用于把字号换算成缩放
    private float _cellWidth = 1f;     // 等宽字格宽度（图集像素）

    private readonly Matrix4x4[] _matrices = new Matrix4x4[MaxInstancesPerBatch];
    private readonly Vector4[] _digits = new Vector4[MaxInstancesPerBatch];
    private readonly Vector4[] _tints = new Vector4[MaxInstancesPerBatch];
    private MaterialPropertyBlock _propertyBlock;

    private bool _ready;
    private bool _loggedMissingGlyph;

    public bool IsReady => _ready;

    public void Initialize(TMP_FontAsset font, float outlineWidth, Color outlineColor)
    {
        if (_ready) return;

        _font = font != null ? font : TMP_Settings.defaultFontAsset;
        if (_font == null)
        {
            Debug.LogError("[DamageText] 没有字体资源，飘字无法渲染");
            return;
        }

        Shader shader = Resources.Load<Shader>("Shaders/DamageText");
        if (shader == null) shader = Shader.Find("Game/DamageText");
        if (shader == null)
        {
            Debug.LogError("[DamageText] 找不到 shader Game/DamageText");
            return;
        }

        _material = new Material(shader) { hideFlags = HideFlags.DontSave };
        _material.enableInstancing = true;
        _material.renderQueue = 4000;   // Overlay：压在场景上、UI 相机之前
        _material.SetColor("_OutlineColor", outlineColor);
        _material.SetFloat("_OutlineWidth", Mathf.Max(0f, outlineWidth));

        _mesh = BuildQuadMesh();
        _propertyBlock = new MaterialPropertyBlock();

        _font.TryAddCharacters("0123456789-", out _);
        RefreshGlyphTable();

        _ready = _material.HasProperty("_CellWidth");
    }

    /// <summary>提交一帧的全部飘字（内部按 1023 实例分批）</summary>
    public void Draw(DamageTextDrawData[] data, int count, Camera camera)
    {
        if (!_ready || count <= 0 || camera == null) return;
        EnsureAtlasValid();

        for (int start = 0; start < count; start += MaxInstancesPerBatch)
        {
            int batchCount = Mathf.Min(MaxInstancesPerBatch, count - start);
            for (int i = 0; i < batchCount; i++)
            {
                DamageTextDrawData d = data[start + i];
                float scale = d.scale * (d.fontSize / _digitRefHeight);
                _matrices[i] = Matrix4x4.TRS(
                    new Vector3(d.origin.x, d.origin.y, 0f),
                    Quaternion.Euler(0f, 0f, d.rotation),
                    new Vector3(scale, scale, 1f));
                _digits[i] = new Vector4(d.packed, d.digitCount, 0f, 0f);
                _tints[i] = d.color;
            }

            _propertyBlock.SetVectorArray("_Digits", _digits);
            _propertyBlock.SetVectorArray("_Tint", _tints);

            Graphics.DrawMeshInstanced(
                _mesh, 0, _material, _matrices, batchCount, _propertyBlock,
                ShadowCastingMode.Off, false, 0, camera);
        }
    }

    /// <summary>字体图集被 TMP 重建（扩容/换图）后字形 UV 会失效，检查到变化就重读</summary>
    private void EnsureAtlasValid()
    {
        TMP_FontAsset font = _font;
        if (font == null) return;
        Texture atlas = font.atlasTexture;
        if (atlas == null) return;
        if (atlas == _atlas && atlas.width == _atlasWidth && atlas.height == _atlasHeight) return;
        RefreshGlyphTable();
    }

    private void RefreshGlyphTable()
    {
        _atlas = _font.atlasTexture;
        if (_atlas == null) return;
        _atlasWidth = _atlas.width;
        _atlasHeight = _atlas.height;

        var uvs = new Vector4[GlyphSlots];
        float maxWidth = 1f;
        int refHeight = 1;

        for (int i = 0; i <= MinusGlyph; i++)
        {
            char c = i < 10 ? (char)('0' + i) : '-';
            if (!TryGetGlyphRect(c, out GlyphRect rect)) continue;

            // glyphRect 的 Y 自图集底部量起，与纹理采样 V 轴一致，直接归一化即可；
            // 屏幕方向的翻转由 shader 里的 _ProjectionParams.x 处理。
            uvs[i] = new Vector4(
                rect.x / (float)_atlasWidth,
                rect.y / (float)_atlasHeight,
                (rect.x + rect.width) / (float)_atlasWidth,
                (rect.y + rect.height) / (float)_atlasHeight);

            if (rect.width > maxWidth) maxWidth = rect.width;
            if (i == 0 && rect.height > 0) refHeight = rect.height;
        }

        _cellWidth = maxWidth;
        _digitRefHeight = refHeight;

        _material.SetTexture("_MainTex", _atlas);
        _material.SetVector("_AtlasSize", new Vector4(_atlasWidth, _atlasHeight, 0f, 0f));
        _material.SetFloat("_CellWidth", _cellWidth);
        _material.SetVectorArray("_DigitUV", uvs);
    }

    private bool TryGetGlyphRect(char c, out GlyphRect rect)
    {
        rect = default;
        var table = _font.characterLookupTable;
        if (table == null) return false;

        TMP_Character character;
        if (!table.TryGetValue(c, out character) || character == null) return false;
        if (character.glyph == null) return false;

        rect = character.glyph.glyphRect;
        if (rect.width <= 0 || rect.height <= 0)
        {
            if (!_loggedMissingGlyph)
            {
                _loggedMissingGlyph = true;
                Debug.LogWarning($"[DamageText] 字形 '{c}' 没有有效 glyphRect，飘字会缺位");
            }
            return false;
        }
        return true;
    }

    /// <summary>6 个四边形的固定网格：uv0 = 角点，uv1.x = 第几位，顶点位置由着色器算</summary>
    private static Mesh BuildQuadMesh()
    {
        var mesh = new Mesh { name = "DamageTextQuads", hideFlags = HideFlags.DontSave };

        var vertices = new Vector3[MaxDigits * 4];
        var uv0 = new Vector2[MaxDigits * 4];
        var uv1 = new Vector4[MaxDigits * 4];
        var triangles = new int[MaxDigits * 6];

        for (int slot = 0; slot < MaxDigits; slot++)
        {
            int v = slot * 4;
            uv0[v + 0] = new Vector2(0f, 0f);
            uv0[v + 1] = new Vector2(0f, 1f);
            uv0[v + 2] = new Vector2(1f, 1f);
            uv0[v + 3] = new Vector2(1f, 0f);
            for (int k = 0; k < 4; k++) uv1[v + k] = new Vector4(slot, 0f, 0f, 0f);

            int t = slot * 6;
            triangles[t + 0] = v + 0;
            triangles[t + 1] = v + 1;
            triangles[t + 2] = v + 2;
            triangles[t + 3] = v + 0;
            triangles[t + 4] = v + 2;
            triangles[t + 5] = v + 3;
        }

        mesh.vertices = vertices;
        mesh.uv = uv0;
        mesh.SetUVs(1, uv1);
        mesh.triangles = triangles;
        // 顶点位置由着色器决定，包围盒给足避免实例被视锥剔除
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
        return mesh;
    }

    private void OnDestroy()
    {
        if (_mesh != null) Destroy(_mesh);
        if (_material != null) Destroy(_material);
    }
}
