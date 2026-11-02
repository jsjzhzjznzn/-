#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Miemie.DialogSystem.Quest.Editor
{
  /// <summary>
  /// 任务编辑器资产路径
  /// </summary>
  static class QuestEditorPaths
  {
    public const string QuestAssetPath = "Assets/Game/Narrative/QuestSystem/QuestSo";

    /// <summary>
    /// 确保任务资产目录存在
    /// </summary>
    public static void EnsureQuestFolder()
    {
      if (AssetDatabase.IsValidFolder(QuestAssetPath))
        return;

      if (!AssetDatabase.IsValidFolder("Assets/Game/Narrative"))
        AssetDatabase.CreateFolder("Assets", "Narrative");
      if (!AssetDatabase.IsValidFolder("Assets/Game/Narrative/QuestSystem"))
        AssetDatabase.CreateFolder("Assets/Game/Narrative", "QuestSystem");
      AssetDatabase.CreateFolder("Assets/Game/Narrative/QuestSystem", "QuestSo");
    }
  }
}
#endif
