using System;

using UnityEditor;

using UnityEngine;

/// <summary>
/// 表情包纹理导入器（编辑器用，作用域仅限表情目录）
/// 表情包的图片统一按 Sprite 导入：
/// ① 表情是 2D 贴图，不需要模型贴图那套设置；
/// ② 运行时 StickerCatalog 用 Resources.LoadAll&lt;Sprite&gt; 扫描表情目录，不是 Sprite 就扫不到。
/// 用「前缀路径」而不是「全局设置」的原因：项目里还有大量模型贴图 / UI 图，
/// 全局改导入设置会把它们一起带偏，这里只认表情目录，其它资源完全不受影响。
/// </summary>
public class StickerTextureImporter : AssetPostprocessor
{
    /// <summary>表情包资源目录（Project 根视角，必须以 / 结尾才好做前缀匹配）</summary>
    private const string StickerFolder = "Assets/Game/Resources/biaoq/";

    /// <summary>
    /// 导入前处理：命中表情目录就按 Sprite 配置导入器
    /// </summary>
    private void OnPreprocessTexture()
    {
        string path = assetPath.Replace('\\', '/');
        if (!path.StartsWith(StickerFolder, StringComparison.Ordinal)) return;

        TextureImporter importer = (TextureImporter)assetImporter;
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = 100f;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        // 表情是 UI 元素：512 上限 + 普通质量压缩，避免多帧动画把包体与显存撑起来
        importer.maxTextureSize = 512;
        importer.textureCompression = TextureImporterCompression.Compressed;
    }
}
