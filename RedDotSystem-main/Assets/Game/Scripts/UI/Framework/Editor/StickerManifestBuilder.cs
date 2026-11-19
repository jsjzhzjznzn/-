using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;

/// <summary>
/// 表情包清单生成器。
/// 表情资源已迁出 Resources（Assets/Game/Art/UI/biaoq），运行时靠 sticker_manifest.txt 列出全部 location，
/// 因此「往目录丢图」之后要跑一次本菜单，把新文件写进清单，否则表情面板看不到它。
/// </summary>
public static class StickerManifestBuilder
{
    private const string Root = "Assets/Game/Art/UI/biaoq";
    private const string ManifestPath = Root + "/sticker_manifest.txt";

    [MenuItem("Tools/UI/生成表情清单")]
    public static void Generate()
    {
        if (!AssetDatabase.IsValidFolder(Root))
        {
            Debug.LogError("[表情清单] 目录不存在：" + Root);
            return;
        }

        string[] files = AssetDatabase.FindAssets("t:Sprite", new[] { Root })
            .Select(AssetDatabase.GUIDToAssetPath)
            .OrderBy(p => p, System.StringComparer.Ordinal)
            .ToArray();

        Dictionary<string, int> counts = new(System.StringComparer.Ordinal);
        foreach (string path in files)
        {
            string key = KeyOf(path);
            counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        var sb = new StringBuilder();
        sb.AppendLine("# biaoq 表情清单：每行一个 YooAsset location（Sprite），# 开头为注释");
        sb.AppendLine("# A = 资源位置");
        foreach (string path in files) sb.AppendLine("A|" + path);
        sb.AppendLine("# B = 表情键|帧数（帧按文件名序号排序，键为动画目录名或静态图文件名）");
        foreach (string key in counts.Keys.OrderBy(k => k, System.StringComparer.Ordinal))
            sb.AppendLine($"B|{key}|{counts[key]}");

        File.WriteAllText(ManifestPath, sb.ToString(), new UTF8Encoding(false));
        AssetDatabase.ImportAsset(ManifestPath);

        int animated = counts.Count(kv => kv.Value > 1);
        Debug.Log($"[表情清单] 已生成 {files.Length} 张图 / {counts.Count} 个表情（动画 {animated} 个）→ {ManifestPath}");
    }

    /// <summary>动画帧命名 &lt;键&gt;_&lt;三位序号&gt;，其余按文件名当键</summary>
    private static string KeyOf(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        var match = System.Text.RegularExpressions.Regex.Match(name, @"^(?<key>.+)_(?<index>\d{3})$");
        return match.Success ? match.Groups["key"].Value : name;
    }
}
