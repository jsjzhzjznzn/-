#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using SkierFramework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace SkierFrameworkUIMigration
{
    /// <summary>
    /// 把 zhuye 场景里散落在各画布上的入口按钮抽成 Assets/Game/UI/Home/HomeView.prefab。
    ///
    /// 做法：在场景根建 wrapper(全屏) + EntryBar，把入口按钮按「原来的锚点/尺寸/anchoredPosition」
    /// 搬进 EntryBar（画布参考分辨率同为 1920x1080，锚点语义直接沿用，不换算世界坐标），
    /// 再挂 HomeView + UIControlData 写绑定，最后 SaveAsPrefabAsset 并删除场景源对象。
    ///
    /// 已迁移到框架的面板（好友/排行榜/战斗大厅）由 HomeView 直接 UIManager.Open；
    /// 副本、活动保持原行为（切场景 / 热更外壳）。
    /// 未迁移的面板（挑战/世界聊天/阿福/任务/邮件）入口暂时留在各自画布里，等对应预制体做完再搬。
    /// </summary>
    public static class HomeViewBuilder
    {
        private const string PrefabPath = "Assets/Game/UI/Home/HomeView.prefab";
        private const string ExpectedScene = "zhuye";

        /// <summary>场景路径 → (HomeView 字段名, 需要摘掉的旧入口组件)</summary>
        private static readonly (string path, string field, string legacyComponent)[] Entries =
        {
            ("FriendCanvas/FriendUIRoot/FriendEntryButton", "friendButton", "OpenUIPanelButton"),
            ("FriendCanvas/FriendUIRoot/LeaderboardEntryButton", "leaderboardButton", ""),
            ("battle/BattleEntryButton", "battleButton", "BattleEntryButton"),
            ("huodong/ActivityEntryButton", "activityButton", "ActivityEntryButton"),
        };

        /// <summary>按钮搬空后要清理的画布</summary>
        private static readonly string[] EmptyCanvasCandidates = { "battle", "huodong" };

        [MenuItem("Tools/UI/构建 HomeView 预制体")]
        public static void Build()
        {
            if (!EditorSceneManager.GetActiveScene().path.EndsWith(ExpectedScene + ".scene"))
            {
                EditorUtility.DisplayDialog("HomeView", "请在 " + ExpectedScene + " 场景里执行", "OK");
                return;
            }

            if (File.Exists(PrefabPath) &&
                !EditorUtility.DisplayDialog("HomeView", PrefabPath + " 已存在，是否覆盖？", "覆盖", "取消"))
                return;

            // 1) 采集按钮与其布局参数（此刻还在各自画布里）
            var captured = new List<(RectTransform rt, string field, string legacy, Vector2 amin, Vector2 amax,
                Vector2 pivot, Vector2 anchored, Vector2 size, Vector3 scale)>();
            foreach (var entry in Entries)
            {
                var go = GameObject.Find(entry.path);
                if (go == null)
                {
                    Debug.LogWarning("[HomeView] 找不到 " + entry.path + "，已跳过");
                    continue;
                }
                var rt = (RectTransform)go.transform;
                captured.Add((rt, entry.field, entry.legacyComponent, rt.anchorMin, rt.anchorMax, rt.pivot,
                    rt.anchoredPosition, rt.sizeDelta, rt.localScale));
            }
            if (captured.Count == 0)
            {
                EditorUtility.DisplayDialog("HomeView", "一个入口按钮都没找到", "OK");
                return;
            }

            // 2) wrapper + EntryBar
            var wrapper = new GameObject("HomeView", typeof(RectTransform));
            var wrt = wrapper.GetComponent<RectTransform>();
            wrt.SetParent(null, false);
            wrt.anchorMin = Vector2.zero;
            wrt.anchorMax = Vector2.one;
            wrt.offsetMin = Vector2.zero;
            wrt.offsetMax = Vector2.zero;

            var entryBar = new GameObject("EntryBar", typeof(RectTransform));
            var ert = entryBar.GetComponent<RectTransform>();
            ert.SetParent(wrapper.transform, false);
            ert.anchorMin = Vector2.zero;
            ert.anchorMax = Vector2.one;
            ert.offsetMin = Vector2.zero;
            ert.offsetMax = Vector2.zero;

            // 3) 搬按钮：先摘旧入口组件，再还原布局参数，最后改名成字段名
            var view = wrapper.AddComponent<HomeView>();
            var controlData = wrapper.AddComponent<UIControlData>();
            var bindings = new List<(string name, string type, Object target)>();

            foreach (var item in captured)
            {
                var go = item.rt.gameObject;
                RemoveLegacyComponent(go, item.legacy);

                item.rt.SetParent(entryBar.transform, false);
                item.rt.anchorMin = item.amin;
                item.rt.anchorMax = item.amax;
                item.rt.pivot = item.pivot;
                item.rt.anchoredPosition = item.anchored;
                item.rt.sizeDelta = item.size;
                item.rt.localScale = item.scale;
                item.rt.localRotation = Quaternion.identity;
                go.name = item.field;

                var button = go.GetComponent<Button>();
                if (button == null)
                {
                    Debug.LogWarning("[HomeView] " + item.field + " 上没有 Button，跳过绑定");
                    continue;
                }
                bindings.Add((item.field, "Button", button));
            }

            // 4) 全子树设 UI 层
            wrapper.layer = Layer.UI;
            foreach (var t in wrapper.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = Layer.UI;

            // 5) 写绑定
            var so = new SerializedObject(controlData);
            var list = so.FindProperty("ctrlItemDatas");
            list.arraySize = bindings.Count;
            for (int i = 0; i < bindings.Count; i++)
            {
                var element = list.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("name").stringValue = bindings[i].name;
                element.FindPropertyRelative("type").stringValue = bindings[i].type;
                var targets = element.FindPropertyRelative("targets");
                targets.arraySize = 1;
                targets.GetArrayElementAtIndex(0).objectReferenceValue = bindings[i].target;
            }
            so.ApplyModifiedPropertiesWithoutUndo();

            // 6) 存预制体
            EnsureFolder("Assets/Game/UI/Home");
            var prefab = PrefabUtility.SaveAsPrefabAsset(wrapper, PrefabPath);
            if (prefab == null)
            {
                Object.DestroyImmediate(wrapper);
                EditorUtility.DisplayDialog("HomeView", "预制体保存失败，见 Console", "OK");
                return;
            }

            // 7) 删场景源（wrapper 里就是搬过来的按钮）
            Object.DestroyImmediate(wrapper);
            CleanupEmptyCanvases();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[HomeView] 生成 " + PrefabPath + "，绑定 " + bindings.Count + " 项");
            Selection.activeObject = prefab;
        }

        private static void RemoveLegacyComponent(GameObject go, string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return;
            foreach (var component in go.GetComponents<Component>())
            {
                if (component != null && component.GetType().Name == typeName)
                {
                    Object.DestroyImmediate(component);
                    Debug.Log("[HomeView] 移除旧入口组件 " + typeName + " on " + go.name);
                    return;
                }
            }
        }

        private static void CleanupEmptyCanvases()
        {
            foreach (var name in EmptyCanvasCandidates)
            {
                var go = GameObject.Find(name);
                if (go == null) continue;
                if (go.transform.childCount == 0)
                {
                    Object.DestroyImmediate(go);
                    Debug.Log("[HomeView] 删除已空的入口画布 " + name);
                }
            }
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace("\\", "/");
            var leaf = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
#endif
