using UnityEditor;
using UnityEngine;

/// <summary>
/// 活动资源模式切换（编辑器专用）：
///   联机 HostPlay —— 从 http://127.0.0.1:8899 真下载（发布新版本后客户端自动增量更新 = 演示热更）
///   编辑器模拟 —— 免构建直读 Assets/Content/HotContent（开发迭代秒生效）
/// 偏好存 PlayerPrefs，下次开面板即用新模式
/// </summary>
public static class ActivityModeToggle
{
    [MenuItem("Tools/活动热更/切换资源模式（模拟↔联机下载）")]
    private static void ToggleMode()
    {
        bool current = PlayerPrefs.GetInt("Activity_UseHostPlayMode", 1) == 1;
        bool next = !current;
        PlayerPrefs.SetInt("Activity_UseHostPlayMode", next ? 1 : 0);
        PlayerPrefs.Save();
        LuaActivityHost.UseHostPlayMode = next;
        Debug.Log($"[活动热更] 资源模式 → {(next ? "联机 HostPlay（8899 真下载，演示热更）" : "编辑器模拟（免构建秒迭代）")}，重开活动面板生效");
    }

    [MenuItem("Tools/活动热更/查看当前资源模式")]
    private static void ShowMode()
    {
        Debug.Log($"[活动热更] 当前模式：{(LuaActivityHost.UseHostPlayMode ? "联机 HostPlay" : "编辑器模拟")}（菜单切换）");
    }
}
