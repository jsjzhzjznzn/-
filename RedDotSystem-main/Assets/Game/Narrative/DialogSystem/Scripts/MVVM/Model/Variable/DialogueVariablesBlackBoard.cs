using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Sirenix.Serialization;
using UnityEngine;

namespace Miemie.DialogSystem
{
    /// <summary>
    /// 对话图上的黑板
    /// </summary>
    public class DialogueVariablesBlackBoard
    {
        [OdinSerialize]
        private Dictionary<string, bool> boolDict = new();

        [OdinSerialize]
        private Dictionary<string, float> floatDict = new();

        [OdinSerialize]
        private Dictionary<string, int> intDict = new();

        /// <summary>
        /// 存档用的变量快照（三段字典；JSON 键刻意短，控制存档体积）
        /// </summary>
        [Serializable]
        private sealed class VariableSnapshot
        {
            public Dictionary<string, bool> b;
            public Dictionary<string, int> i;
            public Dictionary<string, float> f;
        }

        /// <summary>
        /// 导出变量快照 JSON（场景存档落库用；事实类变量在恢复时会被重新播种覆盖）
        /// </summary>
        public string ToJson()
        {
            return JsonConvert.SerializeObject(new VariableSnapshot
            {
                b = boolDict,
                i = intDict,
                f = floatDict,
            });
        }

        /// <summary>
        /// 应用存档变量（在校验图声明之后、播种事实之前调用；非法 JSON 只告警不抛异常）
        /// </summary>
        public void ApplyJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;

            try
            {
                VariableSnapshot snapshot = JsonConvert.DeserializeObject<VariableSnapshot>(json);
                if (snapshot == null) return;

                if (snapshot.b != null)
                {
                    foreach (KeyValuePair<string, bool> item in snapshot.b)
                        SetBool(item.Key, item.Value);
                }
                if (snapshot.i != null)
                {
                    foreach (KeyValuePair<string, int> item in snapshot.i)
                        SetInt(item.Key, item.Value);
                }
                if (snapshot.f != null)
                {
                    foreach (KeyValuePair<string, float> item in snapshot.f)
                        SetFloat(item.Key, item.Value);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Dialogue] 存档变量解析失败，已忽略：{exception.Message}");
            }
        }

        /// <summary>
        /// 用变量声明初始化默认值
        /// </summary>
        public void InitDefaultVariables(IReadOnlyList<DialogueVariableData> variableList)
        {
            if (variableList == null)
                return;

            foreach (var item in variableList)
            {
                if (item == null || string.IsNullOrEmpty(item.name))
                    continue;

                switch (item.variableType)
                {
                    case EDialogueVariableType.Float:
                        SetFloat(item.name, item.defaultFloat);
                        break;
                    case EDialogueVariableType.Int:
                        SetInt(item.name, item.defaultInt);
                        break;
                    case EDialogueVariableType.Bool:
                        SetBool(item.name, item.defaultBool);
                        break;
                }
            }
        }

        /// <summary>
        /// 获取布尔值
        /// </summary>
        public bool GetBool(string variableName, bool defaultValue = false)
        {
            if (string.IsNullOrEmpty(variableName))
                return defaultValue;

            return boolDict.TryGetValue(variableName, out var value) ? value : defaultValue;
        }

        /// <summary>
        /// 设置布尔值
        /// </summary>
        public void SetBool(string variableName, bool value)
        {
            if (string.IsNullOrEmpty(variableName))
                return;

            boolDict[variableName] = value;
        }

        /// <summary>
        /// 获取浮点值
        /// </summary>
        public float GetFloat(string variableName, float defaultValue = 0f)
        {
            if (string.IsNullOrEmpty(variableName))
                return defaultValue;

            return floatDict.TryGetValue(variableName, out var value) ? value : defaultValue;
        }

        /// <summary>
        /// 设置浮点值
        /// </summary>
        public void SetFloat(string variableName, float value)
        {
            if (string.IsNullOrEmpty(variableName))
                return;

            floatDict[variableName] = value;
        }

        /// <summary>
        /// 获取整数值
        /// </summary>
        public int GetInt(string variableName, int defaultValue = 0)
        {
            if (string.IsNullOrEmpty(variableName))
                return defaultValue;

            return intDict.TryGetValue(variableName, out var value) ? value : defaultValue;
        }

        /// <summary>
        /// 设置整数值
        /// </summary>
        public void SetInt(string variableName, int value)
        {
            if (string.IsNullOrEmpty(variableName))
                return;

            intDict[variableName] = value;
        }
    }
}
