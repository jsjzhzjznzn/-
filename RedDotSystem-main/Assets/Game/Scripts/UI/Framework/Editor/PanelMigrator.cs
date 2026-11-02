#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using SkierFramework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace SkierFrameworkUIMigration
{
    /// <summary>
    /// 通用「场景面板 → SkierFramework 预制体」迁移器（P9 各面板共用）。
    ///
    /// 一次做完：
    ///   1. 入口按钮搬进 HomeView.prefab 的 EntryBar（可选，字段名 = HomeView 里的 [ControlBinding] 字段）
    ///   2. 面板根套一层全屏 wrapper，挂 View 脚本 + UIControlData，按 [ControlBinding] 生成绑定
    ///   3. 存到 Assets/Game/UI/.../*.prefab
    ///   4. 删场景源对象（面板 + 搬空的画布）
    ///
    /// 绑定解析顺序：ControlBinding.path > 字段名 > 字段名首字母大写，全部相对**面板根**。
    /// </summary>
    public static class PanelMigrator
    {
        public const string HomeViewPath = "Assets/Game/UI/Home/HomeView.prefab";

        public sealed class Options
        {
            /// <summary>面板在场景里的路径，如 "youjian/MailPanel"</summary>
            public string panelPath;
            public string prefabPath;
            public Type viewType;
            /// <summary>入口按钮对象名（可选）；为空表示不搬入口</summary>
            public string entryName;
            /// <summary>HomeView 里对应的字段名（entryName 非空时必填）</summary>
            public string entryField;
            /// <summary>面板腾空后要删掉的画布名（可选）</summary>
            public string canvasToDelete;
        }

        public static bool Migrate(Options o)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.path.EndsWith("zhuye.scene"))
            {
                EditorUtility.DisplayDialog("面板迁移", "请在 zhuye 场景里执行", "OK");
                return false;
            }

            Transform panel = FindByPath(o.panelPath);
            if (panel == null)
            {
                EditorUtility.DisplayDialog("面板迁移", "找不到面板：" + o.panelPath, "OK");
                return false;
            }

            if (!string.IsNullOrEmpty(o.entryName) && !string.IsNullOrEmpty(o.entryField))
                MoveEntryToHome(o.entryName, o.entryField);

            string prefabPath = o.prefabPath;
            if (File.Exists(prefabPath) &&
                !EditorUtility.DisplayDialog("面板迁移", prefabPath + " 已存在，覆盖？", "覆盖", "取消"))
                return false;

            var wrapper = new GameObject(Path.GetFileNameWithoutExtension(prefabPath), typeof(RectTransform));
            var wrt = wrapper.GetComponent<RectTransform>();
            wrt.SetParent(null, false);
            wrt.anchorMin = Vector2.zero;
            wrt.anchorMax = Vector2.one;
            wrt.offsetMin = Vector2.zero;
            wrt.offsetMax = Vector2.zero;

            panel.SetParent(wrapper.transform, false);

            // 面板本体必须激活：框架只负责激活"视图根"（wrapper），预制体若带着"迁移时是关闭态"进包，
            // 运行时就会"面板打开了却看不见"（阿福面板 2026-10-21 踩过，这里统一兜底）
            if (!panel.gameObject.activeSelf) panel.gameObject.SetActive(true);

            var view = wrapper.AddComponent(o.viewType) as UIView;
            if (view == null)
            {
                UnityEngine.Object.DestroyImmediate(wrapper);
                EditorUtility.DisplayDialog("面板迁移", o.viewType.Name + " 不是 UIView", "OK");
                return false;
            }

            var controlData = wrapper.GetComponent<UIControlData>();
            if (controlData == null) controlData = wrapper.AddComponent<UIControlData>();

            int bindings = BindFields(panel, view, controlData);

            wrapper.layer = Layer.UI;
            foreach (var t in wrapper.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = Layer.UI;

            EnsureFolder(Path.GetDirectoryName(prefabPath).Replace("\\", "/"));
            var prefab = PrefabUtility.SaveAsPrefabAsset(wrapper, prefabPath);
            if (prefab == null)
            {
                UnityEngine.Object.DestroyImmediate(wrapper);
                EditorUtility.DisplayDialog("面板迁移", "预制体保存失败，见 Console", "OK");
                return false;
            }

            UnityEngine.Object.DestroyImmediate(wrapper);

            if (!string.IsNullOrEmpty(o.canvasToDelete))
            {
                var canvas = FindByName(o.canvasToDelete);
                if (canvas != null && canvas.childCount == 0)
                {
                    UnityEngine.Object.DestroyImmediate(canvas.gameObject);
                    Debug.Log("[面板迁移] 删除已空的画布 " + o.canvasToDelete);
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[面板迁移] " + prefabPath + " 生成成功，绑定 " + bindings + " 项");
            Selection.activeObject = prefab;
            return true;
        }

        /// <summary>把场景里的入口按钮并入 HomeView.prefab（供一次性脚本/其它工具复用）</summary>
        public static void MoveEntryToHome(string entryName, string entryField)
        {
            var entry = FindByName(entryName);
            if (entry == null)
            {
                Debug.LogWarning("[面板迁移] 场景里没有 " + entryName + "，跳过入口搬迁");
                return;
            }

            var rt = (RectTransform)entry;
            Vector2 amin = rt.anchorMin, amax = rt.anchorMax, pivot = rt.pivot, anchored = rt.anchoredPosition, size = rt.sizeDelta;
            Vector3 scale = rt.localScale;

            var homePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(HomeViewPath);
            if (homePrefab == null)
            {
                Debug.LogError("[面板迁移] 找不到 " + HomeViewPath);
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(homePrefab);
            var entryBar = instance.transform.Find("EntryBar");
            if (entryBar == null)
            {
                Debug.LogError("[面板迁移] HomeView 预制体里没有 EntryBar");
                UnityEngine.Object.DestroyImmediate(instance);
                return;
            }

            rt.SetParent(entryBar, false);
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.pivot = pivot;
            rt.anchoredPosition = anchored;
            rt.sizeDelta = size;
            rt.localScale = scale;
            rt.localRotation = Quaternion.identity;
            entry.name = entryField;

            var button = entry.GetComponent<Button>();
            var controlData = instance.GetComponent<UIControlData>();
            if (button != null && controlData != null)
            {
                var so = new SerializedObject(controlData);
                var list = so.FindProperty("ctrlItemDatas");
                list.arraySize += 1;
                var element = list.GetArrayElementAtIndex(list.arraySize - 1);
                element.FindPropertyRelative("name").stringValue = entryField;
                element.FindPropertyRelative("type").stringValue = "Button";
                var targets = element.FindPropertyRelative("targets");
                targets.arraySize = 1;
                targets.GetArrayElementAtIndex(0).objectReferenceValue = button;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            PrefabUtility.ApplyPrefabInstance(instance, InteractionMode.AutomatedAction);
            UnityEngine.Object.DestroyImmediate(instance);
            Debug.Log("[面板迁移] " + entryName + " 已并入 HomeView.prefab（" + entryField + "）");
        }

        private static int BindFields(Transform panelRoot, UIView view, UIControlData controlData)
        {
            var so = new SerializedObject(controlData);
            var list = so.FindProperty("ctrlItemDatas");
            list.arraySize = 0;

            foreach (var field in view.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = field.GetCustomAttribute<ControlBindingAttribute>();
                if (attr == null) continue;

                Transform target = !string.IsNullOrEmpty(attr.path)
                    ? panelRoot.Find(attr.path)
                    : (panelRoot.Find(field.Name) ?? panelRoot.Find(char.ToUpper(field.Name[0]) + field.Name.Substring(1)));

                if (target == null)
                {
                    Debug.LogWarning("[面板迁移] 找不到 " + field.DeclaringType.Name + "." + field.Name + " 的节点");
                    continue;
                }

                // GameObject 字段（模板/占位节点）不能走 GetComponent，直接取节点本身
                UnityEngine.Object component = field.FieldType == typeof(GameObject)
                    ? target.gameObject
                    : target.GetComponent(field.FieldType);
                if (component == null)
                {
                    Debug.LogWarning("[面板迁移] " + target.name + " 上没有 " + field.FieldType.Name + "（字段 " + field.Name + "）");
                    continue;
                }

                field.SetValue(view, component);

                list.arraySize += 1;
                var element = list.GetArrayElementAtIndex(list.arraySize - 1);
                element.FindPropertyRelative("name").stringValue = field.Name;
                // UIControlData 的 type 必须是 _typeMap 的键（TextMeshProUGUI/Button/Image/RectTransform…），
                // 写字段类型名（TMP_Text）会让运行期 FindCorrectComponent 返回 null 直接中断绑定
                element.FindPropertyRelative("type").stringValue = component is GameObject ? "GameObject" : component.GetType().Name;
                var targets = element.FindPropertyRelative("targets");
                targets.arraySize = 1;
                targets.GetArrayElementAtIndex(0).objectReferenceValue = component;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            return list.arraySize;
        }

        /// <summary>按 "a/b/c" 找场景里的 Transform（含未激活对象）</summary>
        private static Transform FindByPath(string path)
        {
            string[] parts = path.Split('/');
            Transform current = null;
            foreach (var go in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (go.name == parts[0]) { current = go.transform; break; }
            }
            if (current == null) return null;

            for (int i = 1; i < parts.Length; i++)
            {
                current = current.Find(parts[i]);
                if (current == null) return null;
            }
            return current;
        }

        /// <summary>按名字找场景对象（含未激活；跳过预制体资产）</summary>
        private static Transform FindByName(string name)
        {
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.name != name) continue;
                if (!t.gameObject.scene.IsValid()) continue;      // 预制体资产
                if (t.gameObject.scene.name == null) continue;
                return t;
            }
            return null;
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
