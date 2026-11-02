#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// 联机预制体缺失脚本修复。
///
/// 现象：点击“创建房间”时报
///   The referenced script on this Behaviour (Game Object 'BattleNetworking') is missing!
/// 原因：嵌入式 NGO 包里的 UnityTransport.cs 在 Tuanjie 重导入后 MonoScript 关联失效
///       （资产存在、类型也已编译，但 MonoScript.GetClass() 返回 null），
///       于是 Resources/Battle/BattleNetworking.prefab 上的 UnityTransport 组件被当成缺失脚本。
///
/// 处理：强制重导入 UnityTransport.cs，再检查预制体缺失组件数。只读检查 + 重导入，不改资产内容。
/// 菜单：Tools/Battle/修复联机预制体缺失脚本
/// </summary>
public static class BattleNetworkingRepair
{
    private const string TransportScriptPath =
        "Packages/com.unity.netcode.gameobjects/Runtime/Transports/UTP/UnityTransport.cs";
    private const string NetworkingPrefabPath =
        "Assets/Game/Resources/Battle/BattleNetworking.prefab";

    [MenuItem("Tools/Battle/修复联机预制体缺失脚本", priority = 3)]
    public static void Repair()
    {
        var beforeScript = AssetDatabase.LoadAssetAtPath<MonoScript>(TransportScriptPath);
        string beforeClass = beforeScript != null && beforeScript.GetClass() != null
            ? beforeScript.GetClass().FullName
            : "null";

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(NetworkingPrefabPath);
        int beforeMissing = prefab != null
            ? GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(prefab)
            : -1;

        AssetDatabase.ImportAsset(TransportScriptPath, ImportAssetOptions.ForceUpdate);

        var afterScript = AssetDatabase.LoadAssetAtPath<MonoScript>(TransportScriptPath);
        string afterClass = afterScript != null && afterScript.GetClass() != null
            ? afterScript.GetClass().FullName
            : "null";

        prefab = AssetDatabase.LoadAssetAtPath<GameObject>(NetworkingPrefabPath);
        int afterMissing = prefab != null
            ? GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(prefab)
            : -1;

        Debug.Log($"[BattleRepair] UnityTransport MonoScript: {beforeClass} -> {afterClass}");
        Debug.Log($"[BattleRepair] {NetworkingPrefabPath} missing scripts: {beforeMissing} -> {afterMissing}");

        if (afterMissing != 0)
        {
            Debug.LogWarning("[BattleRepair] 预制体仍有缺失脚本，请检查是否还有其它包脚本导入失效");
        }
        else
        {
            Debug.Log("[BattleRepair] 修复完成：创建房间可以正常实例化联机预制体");
        }
    }
}
#endif
