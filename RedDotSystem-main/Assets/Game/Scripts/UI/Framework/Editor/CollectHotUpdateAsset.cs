using System.IO;
using YooAsset.Editor;

/// <summary>
/// ui 包收集规则：收集全部资源，但跳过路径中含 NonHotUpdate 目录的资源，以及模型文件本身。
///
/// 约定：依赖模型/动画等安装包内资源的界面预制体，放在 Assets/Game/UI/NonHotUpdate 下，
/// 这些资源随安装包发布，不进热更包。UI 热更只覆盖 UI 图片、UI 预制体与 Lua 脚本。
///
/// 依据：YooAsset 构建时会展开被收集资源的全部依赖闭包，无法在打包规则层面排除依赖，
/// 因此不参与热更的预制体必须前置为不被收集，否则它们引用的模型/动画会被一并打入热更包。
/// .fbx 作为入口被直接收集时同样跳过：角色动画属于安装包内容，热更包不承载动画。
/// </summary>
[DisplayName("收集全部（跳过 NonHotUpdate 与模型）")]
public class CollectHotUpdateAsset : IAssetFilterRule
{
    private const string ExcludedFolder = "NonHotUpdate";

    /// <inheritdoc/>
    public string FindAssetType
    {
        get { return EAssetFilterType.All.ToString(); }
    }

    /// <inheritdoc/>
    public bool IsCollectAsset(AssetFilterRuleData data)
    {
        if (IsInExcludedFolder(data.AssetPath))
            return false;

        string extension = Path.GetExtension(data.AssetPath);
        return extension != ".fbx";
    }

    private static bool IsInExcludedFolder(string assetPath)
    {
        string normalized = assetPath.Replace('\\', '/');
        foreach (string segment in normalized.Split('/'))
        {
            if (segment == ExcludedFolder)
                return true;
        }
        return false;
    }
}
