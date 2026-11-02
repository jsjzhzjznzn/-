using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MH
{
    public class RedDotKeyWindow : EditorWindow
    {
        private RedDotKeyAsset m_Target;
        private Vector2 m_ScrollPosition;
        private int m_NewId = 1;
        private string m_NewDes = "";
        private string m_ExportPath = "Assets/Game/Scripts/RedDot/RedDot/";

        // 搜索过滤
        private string m_SearchFilter = "";

        // 编辑状态
        private Dictionary<int, bool> m_EditingStates = new Dictionary<int, bool>();

        // EditorPrefs key
        private const string EXPORT_PATH_PREF_KEY = "RedDot_ExportPath";

        // 缓存 GUIStyle，避免在 OnGUI 中频繁创建
        private GUIStyle m_ScrollViewStyle;
        private GUIStyle m_ItemBoxStyle;

        [MenuItem("Tool/RedDot/RedDotKey Editor")]
        private static void ShowWindow()
        {
            var window = GetWindow<RedDotKeyWindow>();
            window.titleContent = new GUIContent("红点配置");
            window.LoadExportPath();
            window.InitializeNewId();
            window.Show();
        }

        private void LoadExportPath()
        {
            if (EditorPrefs.HasKey(EXPORT_PATH_PREF_KEY))
            {
                m_ExportPath = EditorPrefs.GetString(EXPORT_PATH_PREF_KEY);
            }
        }

        private void SaveExportPath()
        {
            EditorPrefs.SetString(EXPORT_PATH_PREF_KEY, m_ExportPath);
        }

        private void InitializeNewId()
        {
            if (m_Target == null)
            {
                m_Target = AssetDatabase.LoadAssetAtPath<RedDotKeyAsset>(RedDotEditorSettings.Instance.redDotKeyAssetPath);
            }

            if (m_Target != null)
            {
                var serializedObject = new SerializedObject(m_Target);
                var listProperty = serializedObject.FindProperty("m_AllRedDotList");
                
                int maxId = 0;
                for (int i = 0; i < listProperty.arraySize; i++)
                {
                    var element = listProperty.GetArrayElementAtIndex(i);
                    int currentId = element.FindPropertyRelative("id").intValue;
                    maxId = Mathf.Max(maxId, currentId);
                }
                
                m_NewId = maxId + 1;
            }
            else
            {
                m_NewId = 1;
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);

            // 如果目标为空，尝试加载默认配置
            if (m_Target == null)
            {
                m_Target = AssetDatabase.LoadAssetAtPath<RedDotKeyAsset>(RedDotEditorSettings.Instance
                    .redDotKeyAssetPath);
            }

            if (m_Target == null)
            {
                EditorGUILayout.HelpBox("请先选择一个RedDotKeyAsset配置文件", MessageType.Warning);
                return;
            }

            GUI.enabled = false;
            m_Target = EditorGUILayout.ObjectField("目标配置", m_Target, typeof(RedDotKeyAsset), false) as RedDotKeyAsset;
            GUI.enabled = true;


            EditorGUILayout.Space(10);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            m_ExportPath = EditorGUILayout.TextField("导出路径", m_ExportPath);
            if (EditorGUI.EndChangeCheck())
            {
                SaveExportPath();
            }
            if (GUILayout.Button("选择路径", GUILayout.Width(60)))
            {
                string path = EditorUtility.OpenFolderPanel(
                    "选择枚举文件导出文件夹",
                    Application.dataPath,
                    "");
                
                if (!string.IsNullOrEmpty(path))
                {
                    // 转换为相对于Assets的路径
                    if (path.StartsWith(Application.dataPath))
                    {
                        string relativePath = "Assets" + path.Substring(Application.dataPath.Length);
                        m_ExportPath = relativePath + "/";
                        SaveExportPath();
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            m_NewId = EditorGUILayout.IntField("红点Id", m_NewId);
            m_NewDes = EditorGUILayout.TextField("红点描述", m_NewDes);
            if (GUILayout.Button("添加", GUILayout.Width(60)))
            {
                AddNewItem();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            // 搜索框
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("搜索:", GUILayout.Width(40));
            m_SearchFilter = EditorGUILayout.TextField(m_SearchFilter);
            if (GUILayout.Button("清除", GUILayout.Width(50)))
            {
                m_SearchFilter = "";
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);
            // 懒加载 GUIStyle
            if (m_ScrollViewStyle == null)
            {
                m_ScrollViewStyle = new GUIStyle(GUI.skin.box);
                m_ScrollViewStyle.padding = new RectOffset(10, 10, 10, 10);
            }
            EditorGUILayout.BeginVertical(m_ScrollViewStyle);
            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);

            var serializedObject = new SerializedObject(m_Target);
            var listProperty = serializedObject.FindProperty("m_AllRedDotList");

            for (int i = 0; i < listProperty.arraySize; i++)
            {
                var element = listProperty.GetArrayElementAtIndex(i);
                int id = element.FindPropertyRelative("id").intValue;
                string des = element.FindPropertyRelative("des").stringValue;

                // 搜索过滤
                if (!string.IsNullOrEmpty(m_SearchFilter))
                {
                    bool matchId = id.ToString().Contains(m_SearchFilter);
                    bool matchDes = des.IndexOf(m_SearchFilter, System.StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!matchId && !matchDes) continue;
                }

                // 懒加载 item box style
                if (m_ItemBoxStyle == null)
                {
                    m_ItemBoxStyle = new GUIStyle("box");
                    m_ItemBoxStyle.normal.background = EditorGUIUtility.whiteTexture;
                }
                var itemColor = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.4f, 0.4f, 0.4f); // 设置为浅灰色
                EditorGUILayout.BeginVertical(m_ItemBoxStyle);
                GUI.backgroundColor = itemColor;

                EditorGUILayout.BeginHorizontal();

                EditorGUILayout.LabelField($"ID: {id}", GUILayout.Width(80));

                // 检查是否处于编辑状态
                if (!m_EditingStates.TryGetValue(id, out bool editing)) editing = false;

                if (editing)
                {
                    var desProp = element.FindPropertyRelative("des");
                    EditorGUI.BeginChangeCheck();
                    string newDes = EditorGUILayout.TextField(desProp.stringValue);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(m_Target, "Edit RedDot Key Description");
                        desProp.stringValue = newDes;
                    }
                    if (GUILayout.Button("保存", GUILayout.Width(50)))
                    {
                        m_EditingStates[id] = false;
                        serializedObject.ApplyModifiedProperties();
                        EditorUtility.SetDirty(m_Target);
                    }
                }
                else
                {
                    EditorGUILayout.LabelField($"描述: {des}");
                    if (GUILayout.Button("编辑", GUILayout.Width(50)))
                    {
                        m_EditingStates[id] = true;
                    }
                }

                var deleteButtonColor = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.8f, 0.2f, 0.2f);
                if (GUILayout.Button("删除", GUILayout.Width(60)))
                {
                    if (EditorUtility.DisplayDialog("确认删除",
                        $"确定要删除 Key{id} ({des}) 吗？\n此操作可通过 Ctrl+Z 撤销。", "删除", "取消"))
                    {
                        Undo.RecordObject(m_Target, "Delete RedDot Key");
                        listProperty.DeleteArrayElementAtIndex(i);
                        m_EditingStates.Remove(id);
                        i--;
                    }
                }

                GUI.backgroundColor = deleteButtonColor;
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(5);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            if (serializedObject.hasModifiedProperties)
            {
                serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(m_Target);
            }

            EditorGUILayout.Space(10);
            var originalColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.2f, 0.8f, 0.2f);
            if (GUILayout.Button("生成枚举", GUILayout.Height(200)))
            {
                GenerateEnum();
            }

            GUI.backgroundColor = originalColor;
        }

        private void AddNewItem()
        {
            if (m_Target == null) return;

            var serializedObject = new SerializedObject(m_Target);
            var listProperty = serializedObject.FindProperty("m_AllRedDotList");

            // 检查ID是否已存在
            for (int i = 0; i < listProperty.arraySize; i++)
            {
                var element = listProperty.GetArrayElementAtIndex(i);
                if (element.FindPropertyRelative("id").intValue == m_NewId)
                {
                    EditorUtility.DisplayDialog("错误", "ID已存在", "确定");
                    return;
                }
            }

            var newData = new RedDotKeyData(m_NewId, m_NewDes);

            Undo.RecordObject(m_Target, "Add RedDot Key");
            listProperty.arraySize++;
            var newElement = listProperty.GetArrayElementAtIndex(listProperty.arraySize - 1);
            newElement.FindPropertyRelative("id").intValue = m_NewId;
            newElement.FindPropertyRelative("des").stringValue = m_NewDes;

            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(m_Target);

            // 清空输入并重新初始化ID
            m_NewDes = "";
            InitializeNewId();
        }

        private void GenerateEnum()
        {
            if (m_Target == null) return;
            if (string.IsNullOrEmpty(m_ExportPath))
            {
                EditorUtility.DisplayDialog("错误", "请先设置导出路径", "确定");
                return;
            }

            // 构建完整的文件路径
            string enumFilePath = Path.Combine(m_ExportPath, "ERedDotKeyType.cs");
            
            var sb = new StringBuilder();

            sb.AppendLine("");
            sb.AppendLine("using System.ComponentModel;");
            sb.AppendLine("namespace MH");
            sb.AppendLine("{");
            sb.AppendLine("    public enum ERedDotKeyType");
            sb.AppendLine("    {");
            sb.AppendLine("        [Description(\"None\")]");
            sb.AppendLine("        None = 0,");
            sb.AppendLine("");

            var serializedObject = new SerializedObject(m_Target);
            var listProperty = serializedObject.FindProperty("m_AllRedDotList");

            for (int i = 0; i < listProperty.arraySize; i++)
            {
                var element = listProperty.GetArrayElementAtIndex(i);
                var id = element.FindPropertyRelative("id").intValue;
                var des = element.FindPropertyRelative("des").stringValue;

                sb.AppendLine($"        [Description(\"{des}\")]");
                sb.AppendLine($"        Key{id} = {id},");
                sb.AppendLine("");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            // 确保目录存在
            var directoryPath = Path.GetDirectoryName(enumFilePath);
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            // 写入到文件而不是文件夹
            File.WriteAllText(enumFilePath, sb.ToString());
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("提示", "枚举生成成功", "确定");
        }
    }
}