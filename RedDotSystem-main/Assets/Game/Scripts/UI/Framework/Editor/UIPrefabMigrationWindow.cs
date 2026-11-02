using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SkierFramework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SkierFrameworkUIMigration
{
    /// <summary>
    /// 旧 UI 面板 → SkierFramework 预制体 的通用迁移窗口。
    ///
    /// 用法：
    ///   1. 在场景里选中一个面板根（例如 beibao/BagPanel）。
    ///   2. Tools/UI/SkierFramework 面板迁移窗口。
    ///   3. 选择资源分组、预制体名、View 类型，点“提取并生成预制体”。
    ///
    /// 绑定规则：View 类字段上的 [ControlBinding("子物体路径")] 优先；没写路径时按
    /// “字段名 / 字段名首字母大写”在面板子树里找同名节点。找不到会打 warning 并跳过。
    /// 运行时仍按字段名匹配 UIControlData.name，本工具会自动生成 ctrlItemDatas。
    /// </summary>
    public class UIPrefabMigrationWindow : EditorWindow
    {
        private GameObject contentRoot;
        private string group = "Common";
        private string prefabName = "";
        private string viewTypeName = "";
        private bool isWindow;
        private UILayer uiLayer = UILayer.NormalLayer;
        private bool deleteSceneSource = true;
        private Vector2 scroll;

        private List<Type> viewTypes = new();
        private string[] viewTypeNames = Array.Empty<string>();
        private int viewTypeIndex = -1;

        [MenuItem("Tools/UI/SkierFramework 面板迁移窗口")]
        private static void Open()
        {
            var win = GetWindow<UIPrefabMigrationWindow>("UI 面板迁移");
            win.minSize = new Vector2(460, 320);
            win.Show();
        }

        private void OnEnable()
        {
            RefreshViewTypes();
            if (Selection.activeGameObject != null)
                SetContent(Selection.activeGameObject);
        }

        private void OnSelectionChange()
        {
            if (Selection.activeGameObject != null && contentRoot == null)
                SetContent(Selection.activeGameObject);
            Repaint();
        }

        private void SetContent(GameObject go)
        {
            contentRoot = go;
            prefabName = go.name;
            group = go.transform.parent != null ? go.transform.parent.name : "Common";
            Repaint();
        }

        private void RefreshViewTypes()
        {
            viewTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .Where(t => typeof(UIView).IsAssignableFrom(t) && !t.IsAbstract && t != typeof(UIView))
                .OrderBy(t => t.Name)
                .ToList();

            viewTypeNames = viewTypes.Select(t => t.FullName ?? t.Name).ToArray();
            if (viewTypeIndex < 0 || viewTypeIndex >= viewTypes.Count)
                viewTypeIndex = viewTypes.FindIndex(t => t.Name == viewTypeName);
            if (viewTypeIndex < 0) viewTypeIndex = 0;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("把场景面板抽成 Assets/Game/UI/<Group>/<Name>.prefab", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            contentRoot = (GameObject)EditorGUILayout.ObjectField("场景面板根", contentRoot, typeof(GameObject), true);
            if (contentRoot != null && string.IsNullOrEmpty(prefabName))
                SetContent(contentRoot);

            group = EditorGUILayout.TextField("资源分组", group);
            prefabName = EditorGUILayout.TextField("预制体名", prefabName);

            if (viewTypeNames.Length == 0) RefreshViewTypes();
            viewTypeIndex = EditorGUILayout.Popup("View 类型", viewTypeIndex, viewTypeNames);
            if (viewTypeIndex >= 0 && viewTypeIndex < viewTypes.Count)
                viewTypeName = viewTypes[viewTypeIndex].Name;

            isWindow = EditorGUILayout.Toggle("窗口型（不遮挡下层）", isWindow);
            uiLayer = (UILayer)EditorGUILayout.EnumPopup("UI 层级", uiLayer);
            deleteSceneSource = EditorGUILayout.Toggle("保存后删除场景源对象", deleteSceneSource);

            EditorGUILayout.Space();
            if (GUILayout.Button("提取并生成预制体", GUILayout.Height(32)))
                Extract();

            if (GUILayout.Button("刷新 View 类型列表"))
                RefreshViewTypes();

            EditorGUILayout.HelpBox(
                "字段绑定优先级：[ControlBinding(\"子物体路径\")] > 字段名 > 字段名首字母大写。\n" +
                "运行时 UIControlData 仍按字段名匹配；生成后请把 uiType 加到 UIType 枚举。",
                MessageType.Info);
        }

        private void Extract()
        {
            if (contentRoot == null) { EditorUtility.DisplayDialog("迁移", "请先选择场景面板根", "OK"); return; }
            if (viewTypeIndex < 0 || viewTypeIndex >= viewTypes.Count) { EditorUtility.DisplayDialog("迁移", "请选择 View 类型", "OK"); return; }
            if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(prefabName))
            {
                EditorUtility.DisplayDialog("迁移", "资源分组/预制体名不能为空", "OK");
                return;
            }

            Type viewType = viewTypes[viewTypeIndex];
            string dir = $"Assets/Game/UI/{group}";
            EnsureFolder(dir);
            string prefabPath = $"{dir}/{prefabName}.prefab";

            if (File.Exists(prefabPath) &&
                !EditorUtility.DisplayDialog("覆盖预制体", $"{prefabPath} 已存在，是否覆盖？", "覆盖", "取消"))
                return;

            Transform originalParent = contentRoot.transform.parent;
            var wrapper = new GameObject(prefabName, typeof(RectTransform));
            var wrt = wrapper.GetComponent<RectTransform>();
            wrt.SetParent(originalParent, false);
            wrt.anchorMin = Vector2.zero;
            wrt.anchorMax = Vector2.one;
            wrt.offsetMin = Vector2.zero;
            wrt.offsetMax = Vector2.zero;

            contentRoot.transform.SetParent(wrapper.transform, false);
            contentRoot.SetActive(true);

            var view = wrapper.AddComponent(viewType) as UIView;
            if (view == null)
            {
                DestroyImmediate(wrapper);
                EditorUtility.DisplayDialog("迁移", $"{viewType.FullName} 不是 UIView", "OK");
                return;
            }

            var controlData = wrapper.AddComponent<UIControlData>();
            var bindings = BindFields(contentRoot.transform, view, viewType, controlData);

            wrapper.layer = Layer.UI;
            foreach (var t in wrapper.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = Layer.UI;

            var prefab = PrefabUtility.SaveAsPrefabAsset(wrapper, prefabPath);
            if (prefab == null)
            {
                DestroyImmediate(wrapper);
                EditorUtility.DisplayDialog("迁移", "预制体保存失败，见 Console", "OK");
                return;
            }

            AddOrUpdateUIConfig(prefabName, $"Assets/Game/UI/{group}/{prefabName}.prefab", isWindow, uiLayer);

            if (deleteSceneSource)
            {
                DestroyImmediate(wrapper);
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                EditorSceneManager.MarkSceneDirty(scene);
            }
            else
            {
                contentRoot.transform.SetParent(originalParent, true);
                DestroyImmediate(wrapper);
                EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[UI迁移] {prefabPath} 生成成功，绑定 {bindings} 项；请把 {prefabName} 加入 UIType 枚举。");
            Selection.activeObject = prefab;
        }

        private static int BindFields(Transform root, UIView view, Type viewType, UIControlData controlData)
        {
            var fields = viewType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var bindings = new List<(string name, string type, UnityEngine.Object target)>();

            foreach (var f in fields)
            {
                var attr = f.GetCustomAttribute<ControlBindingAttribute>();
                if (attr == null) continue;

                Transform target = ResolveTarget(root, attr.path, f.Name);
                if (target == null)
                {
                    Debug.LogWarning($"[UI迁移] 找不到字段 {viewType.Name}.{f.Name} 的节点（path='{attr.path}'），已跳过");
                    continue;
                }

                UnityEngine.Object component = GetComponentForField(target, f.FieldType);
                if (component == null)
                {
                    Debug.LogWarning($"[UI迁移] 节点 {target.name} 上没有 {f.FieldType.Name}（字段 {f.Name}），已跳过");
                    continue;
                }

                f.SetValue(view, component);
                bindings.Add((f.Name, f.FieldType.Name, component));
            }

            var so = new SerializedObject(controlData);
            var list = so.FindProperty("ctrlItemDatas");
            list.arraySize = bindings.Count;
            for (int i = 0; i < bindings.Count; i++)
            {
                var e = list.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("name").stringValue = bindings[i].name;
                e.FindPropertyRelative("type").stringValue = bindings[i].type;
                var targets = e.FindPropertyRelative("targets");
                targets.arraySize = 1;
                targets.GetArrayElementAtIndex(0).objectReferenceValue = bindings[i].target;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            return bindings.Count;
        }

        private static Transform ResolveTarget(Transform root, string path, string fieldName)
        {
            if (!string.IsNullOrEmpty(path))
                return root.Find(path);

            Transform direct = root.Find(fieldName);
            if (direct != null) return direct;

            string pascal = char.ToUpperInvariant(fieldName[0]) + fieldName.Substring(1);
            direct = root.Find(pascal);
            if (direct != null) return direct;

            return FindDeep(root, fieldName) ?? FindDeep(root, pascal);
        }

        private static Transform FindDeep(Transform root, string name)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (string.Equals(child.name, name, StringComparison.OrdinalIgnoreCase))
                    return child;
                var hit = FindDeep(child, name);
                if (hit != null) return hit;
            }
            return null;
        }

        private static UnityEngine.Object GetComponentForField(Transform target, Type fieldType)
        {
            if (typeof(GameObject).IsAssignableFrom(fieldType)) return target.gameObject;
            if (typeof(Transform).IsAssignableFrom(fieldType)) return target;
            if (typeof(Component).IsAssignableFrom(fieldType)) return target.GetComponent(fieldType);
            return null;
        }

        private static void AddOrUpdateUIConfig(string uiType, string path, bool isWindow, UILayer layer)
        {
            const string configPath = "Assets/Game/UI/UIConfig.json";
            UIConfigJsonList wrapper;
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                wrapper = JsonUtility.FromJson<UIConfigJsonList>(json) ?? new UIConfigJsonList();
            }
            else
            {
                wrapper = new UIConfigJsonList();
            }

            wrapper.items ??= new List<UIConfigJson>();
            var item = wrapper.items.FirstOrDefault(x => x.uiType == uiType);
            if (item == null)
            {
                item = new UIConfigJson { uiType = uiType };
                wrapper.items.Add(item);
            }

            item.path = path;
            item.isWindow = isWindow;
            item.uiLayer = layer.ToString();
            item.isAutoNavigation = false;
            item.isLuaUI = false;
            item.luaModuleName = "";

            File.WriteAllText(configPath, JsonUtility.ToJson(wrapper, true));
            AssetDatabase.ImportAsset(configPath);
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
