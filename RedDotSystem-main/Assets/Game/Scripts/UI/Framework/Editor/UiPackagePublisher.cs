// ui 包构建与发布工具（编辑器专用）
// 与活动包同源（ActivityPackagePublisher），差别只有两处：
//   1) 包名 ui
//   2) 发布到 HotContent 的 ui 子目录，避免与活动包的 HashName 文件互相覆盖
// 发布布局（YooAsset 联机模式要求）：
//   HotContent/ui/*.bundle / ui_{ver}.bytes / ui_{ver}.hash   ← 构建版本目录里的文件拷到 ui 子目录
//   HotContent/ui/ui.version                                  ← 客户端 RequestPackageVersion 拉这个文件
// 客户端侧对应 UIConfigHotUpdate：RemoteServiceFactory 返回 {8899}/ui
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using YooAsset;
using YooAsset.Editor;

/// <summary>
/// 一键构建 ui 包并发布到服务器 HotContent/ui 目录。
/// 调用入口：菜单 Tools/UI 热更/构建并发布 ui 包，或代码调 BuildAndPublish("x.y.z", 备注)。
/// </summary>
public static class UiPackagePublisher
{
    public const string PackageName = "ui";

    /// <summary>构建输出根 = 服务器静态服务根（competeserves/HotContent）。</summary>
    public static string OutputRoot
    {
        get
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.GetFullPath(Path.Combine(projectRoot, "..", "competeserves", "HotContent"));
        }
    }

    /// <summary>该包的发布目录（ui 包独占子目录，不与活动包混放）。</summary>
    public static string PackageOutputRoot => Path.Combine(OutputRoot, PackageName);

    public static BuildResult BuildAndPublish(string packageVersion, string note)
    {
        // ── 1. 构建参数 ──
        ScriptableBuildParameters buildParameters = new ScriptableBuildParameters
        {
            BuildOutputRoot = OutputRoot,
            BundledFileRoot = BundleBuilderHelper.GetStreamingAssetsRoot(),
            BuildPipeline = EBuildPipeline.ScriptableBuildPipeline.ToString(),
            BuildBundleType = (int)EBundleType.AssetBundle,
            BuildTarget = EditorUserBuildSettings.activeBuildTarget,
            PackageName = PackageName,
            PackageVersion = packageVersion,
            PackageNote = string.IsNullOrEmpty(note) ? $"发布于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}" : note,
            EnableSharePackRule = true,
            VerifyBuildingResult = true,
            FileNameStyle = EFileNameStyle.HashName,
            BundledCopyOption = EBundledCopyOption.None,
            CompressOption = ECompressOption.LZ4,
            ClearBuildCacheFiles = true,
            BuiltinShadersBundleName = DefaultBundlePackRule.CreateShadersPackRuleResult()
                .GetBundleName(PackageName, BundleCollectorSettingData.Setting.UniqueBundleName),
        };

        // ── 2. SBP 构建 ──
        ScriptableBuildPipeline pipeline = new ScriptableBuildPipeline();
        BuildResult result = pipeline.Run(buildParameters, true);
        if (result.Success == false)
        {
            Debug.LogError($"[UI热更] 构建失败：{result.ErrorInfo}（任务：{result.FailedTask}）");
            return result;
        }
        Debug.Log($"[UI热更] 构建成功：{result.OutputPackageDirectory}");

        // ── 3. 发布：拷到 ui 子目录 + 写版本指针 ──
        Publish(buildParameters, packageVersion);
        return result;
    }

    private static void Publish(ScriptableBuildParameters buildParameters, string packageVersion)
    {
        Directory.CreateDirectory(PackageOutputRoot);
        string versionDir = Path.Combine(OutputRoot, buildParameters.BuildTarget.ToString(), PackageName, packageVersion);
        if (Directory.Exists(versionDir) == false)
            throw new DirectoryNotFoundException($"构建版本目录不存在：{versionDir}");

        // 先清掉本包目录里的旧补丁与清单：HashName 文件名只与内容相关，换内容必然换名字，
        // 不清会留下上一版的孤儿文件（历史上就出现过「目录里是三个月前的旧包」被误当成新发布）
        int cleared = 0;
        foreach (string stale in Directory.GetFiles(PackageOutputRoot))
        {
            string ext = Path.GetExtension(stale);
            if (ext == ".bundle" || ext == ".bytes" || ext == ".hash")
            {
                File.Delete(stale);
                cleared++;
            }
        }

        int copied = 0;
        foreach (string file in Directory.GetFiles(versionDir))
        {
            string ext = Path.GetExtension(file);
            if (ext == ".bundle" || ext == ".bytes" || ext == ".hash")
            {
                File.Copy(file, Path.Combine(PackageOutputRoot, Path.GetFileName(file)), true);
                copied++;
            }
        }
        File.WriteAllText(Path.Combine(PackageOutputRoot, $"{PackageName}.version"), packageVersion);
        Debug.Log($"[UI热更] 发布完成：{PackageOutputRoot}，清理旧文件 {cleared} 个，拷贝 {copied} 个补丁文件，版本指针 ui.version → {packageVersion}");
    }

    [MenuItem("Tools/UI 热更/构建并发布 ui 包")]
    private static void MenuBuildAndPublish()
    {
        PublishWindow.ShowWindow();
    }

    [MenuItem("Tools/UI 热更/切换 UIConfig 资源模式（包内↔联机下载）")]
    private static void ToggleMode()
    {
        bool next = !SkierFramework.UIConfigHotUpdate.Enabled;
        SkierFramework.UIConfigHotUpdate.Enabled = next;
        Debug.Log($"[UI热更] 资源模式 → {(next ? "联机 HostPlay（从 8899/ui 真下载）" : "包内资源（编辑器模拟 / 真机只读包内）")}，重新进 Play 生效");
    }

    [MenuItem("Tools/UI 热更/查看当前 UIConfig 资源模式")]
    private static void ShowMode()
    {
        Debug.Log($"[UI热更] 当前模式：{(SkierFramework.UIConfigHotUpdate.Enabled ? "联机 HostPlay" : "包内资源")}；发布目录 {PackageOutputRoot}");
    }

    /// <summary>版本号输入小窗（记住上次发布的版本号）。</summary>
    private class PublishWindow : EditorWindow
    {
        public static string lastVersion = "1.0.0";
        public string version = "1.0.0";

        public static void ShowWindow()
        {
            PublishWindow window = GetWindow<PublishWindow>(true, "构建并发布 ui 包");
            window.minSize = new Vector2(360, 120);
            window.Show();
        }

        private void OnEnable()
        {
            version = lastVersion;
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                $"包名：{PackageName}\n输出：{PackageOutputRoot}\n构建管线：SBP（{EditorUserBuildSettings.activeBuildTarget}）",
                MessageType.Info);
            version = EditorGUILayout.TextField("版本号", version);
            EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(version));
            if (GUILayout.Button("构建并发布"))
            {
                lastVersion = version.Trim();
                string v = lastVersion;
                Close();
                EditorApplication.delayCall += () => UiPackagePublisher.BuildAndPublish(v, $"菜单发布 {DateTime.Now:HH:mm:ss}");
            }
            EditorGUI.EndDisabledGroup();
        }
    }
}