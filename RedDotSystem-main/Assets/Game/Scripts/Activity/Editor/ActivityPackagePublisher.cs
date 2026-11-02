// 活动热更包构建与发布工具（编辑器专用，运营模拟）
// 产物直接铺到服务器静态服务目录 competeserves/HotContent（由游戏服务器 HttpListener:8899 对外提供）
// 发布布局（YooAsset 联机模式要求）：
//   *.bundle / activity_{ver}.bytes / activity_{ver}.hash  ← 构建版本目录里的文件拷平到 CDN 根
//   activity.version                                       ← 内容 = 当前版本号（客户端 RequestPackageVersion 拉这个文件）
// 包内资源寻址规则见 BundleCollectorSetting：EnableAddressable + AddressByFileName + PackSeparately（增量发布时只下载变化的文件）
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YooAsset;
using YooAsset.Editor;

/// <summary>
/// 一键构建 activity 包并发布到服务器 HotContent 目录。
/// 调用入口：菜单 Tools/活动热更/构建并发布活动包（弹窗输入版本号），或代码调 BuildAndPublish("x.y.z", 备注)。
/// </summary>
public static class ActivityPackagePublisher
{
    public const string PackageName = "activity";

    /// <summary>构建输出根 = 服务器静态服务根（competeserves/HotContent，本项目根的邻级目录）。</summary>
    public static string OutputRoot
    {
        get
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.GetFullPath(Path.Combine(projectRoot, "..", "competeserves", "HotContent"));
        }
    }

    /// <summary>构建并发布一个版本。返回 YooAsset 构建结果（Success/输出目录）。</summary>
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
            Debug.LogError($"[活动热更] 构建失败：{result.ErrorInfo}（任务：{result.FailedTask}）");
            return result;
        }
        Debug.Log($"[活动热更] 构建成功：{result.OutputPackageDirectory}");

        // ── 3. 发布：拷平到 CDN 根 + 写版本指针 ──
        Publish(buildParameters, packageVersion);
        return result;
    }

    /// <summary>把构建版本目录里的补丁文件拷平到 CDN 根，并写 {pkg}.version 指针。</summary>
    private static void Publish(ScriptableBuildParameters buildParameters, string packageVersion)
    {
        Directory.CreateDirectory(OutputRoot);
        string versionDir = Path.Combine(OutputRoot, buildParameters.BuildTarget.ToString(), PackageName, packageVersion);
        if (Directory.Exists(versionDir) == false)
            throw new DirectoryNotFoundException($"构建版本目录不存在：{versionDir}");

        int copied = 0;
        foreach (string file in Directory.GetFiles(versionDir))
        {
            string ext = Path.GetExtension(file);
            if (ext == ".bundle" || ext == ".bytes" || ext == ".hash")
            {
                File.Copy(file, Path.Combine(OutputRoot, Path.GetFileName(file)), true);
                copied++;
            }
        }
        File.WriteAllText(Path.Combine(OutputRoot, $"{PackageName}.version"), packageVersion);
        Debug.Log($"[活动热更] 发布完成：CDN 根 {OutputRoot}，拷贝 {copied} 个补丁文件，版本指针 activity.version → {packageVersion}");
    }

    [MenuItem("Tools/活动热更/构建并发布活动包")]
    private static void MenuBuildAndPublish()
    {
        // 小型版本号输入窗口（Tuanjie 没有内置文本输入对话框）；初始版本号由 OnEnable 从 lastVersion 带出
        PublishWindow.ShowWindow();
    }

    /// <summary>版本号输入小窗（记住上次发布的版本号）。</summary>
    private class PublishWindow : EditorWindow
    {
        public static string lastVersion = "1.0.0";
        public string version = "1.0.0";

        public static void ShowWindow()
        {
            PublishWindow window = GetWindow<PublishWindow>(true, "构建并发布活动包");
            window.minSize = new Vector2(320, 110);
            window.Show();
        }

        private void OnEnable()
        {
            version = lastVersion;
        }
        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                $"包名：{PackageName}\n输出：{OutputRoot}\n构建管线：SBP（{EditorUserBuildSettings.activeBuildTarget}）",
                MessageType.Info);
            version = EditorGUILayout.TextField("版本号", version);
            EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(version));
            if (GUILayout.Button("构建并发布"))
            {
                lastVersion = version.Trim();
                string v = lastVersion;
                Close();
                // delayCall 避免在 GUI 流程里跑长任务
                EditorApplication.delayCall += () => ActivityPackagePublisher.BuildAndPublish(v, $"菜单发布 {DateTime.Now:HH:mm:ss}");
            }
            EditorGUI.EndDisabledGroup();
        }
    }
}
