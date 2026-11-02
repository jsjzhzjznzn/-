using System.Collections.Generic;
using System.Text;

namespace Miemie.DialogSystem
{
    /// <summary>
    /// 全局事实黑板：集成层绑定器（背包/任务等）把玩法状态写进来，
    /// 对话开始时经 SeedInto 播种进图内黑板——条件判断链路（ECondition/MeetCondition/编辑器/导表）零改动。
    /// 依赖方向：gameplay→facts→dialogue，对话模块不引用任何玩法系统。
    /// 键名约定：item_{itemId}_count（背包绑定器）；新增事实源照此命名。
    /// </summary>
    public static class DialogueFacts
    {
        static readonly DialogueVariablesBlackBoard global = new();
        static readonly Dictionary<string, EDialogueVariableType> keyTypes = new();

        /// <summary>全局事实黑板（绑定器请走 SetXxxFact 保证类型登记）</summary>
        public static DialogueVariablesBlackBoard Global => global;

        /// <summary>写布尔事实（登记类型，供播种时做类型一致校验）</summary>
        public static void SetBoolFact(string name, bool value)
        {
            keyTypes[name] = EDialogueVariableType.Bool;
            global.SetBool(name, value);
        }

        /// <summary>写整数事实</summary>
        public static void SetIntFact(string name, int value)
        {
            keyTypes[name] = EDialogueVariableType.Int;
            global.SetInt(name, value);
        }

        /// <summary>写浮点事实</summary>
        public static void SetFloatFact(string name, float value)
        {
            keyTypes[name] = EDialogueVariableType.Float;
            global.SetFloat(name, value);
        }

        public static int GetIntFact(string name) => global.GetInt(name);
        public static bool GetBoolFact(string name) => global.GetBool(name);
        public static float GetFloatFact(string name) => global.GetFloat(name);

        /// <summary>
        /// 对话开始时播种：只覆盖"图内声明过且类型与事实登记一致"的变量。
        /// 图没声明的名字不写（保持图自包含）；类型不符跳过（防误覆盖）；
        /// 没有任何绑定器时为无操作，框架单独使用不受影响。
        /// </summary>
        public static void SeedInto(DialogueVariablesBlackBoard target, IReadOnlyList<DialogueVariableData> declaredList)
        {
            if (target == null || declaredList == null) return;

            foreach (var declared in declaredList)
            {
                if (declared == null || string.IsNullOrEmpty(declared.name)) continue;
                if (!keyTypes.TryGetValue(declared.name, out var factType) || factType != declared.variableType)
                    continue;

                switch (factType)
                {
                    case EDialogueVariableType.Bool:
                        target.SetBool(declared.name, global.GetBool(declared.name));
                        break;
                    case EDialogueVariableType.Int:
                        target.SetInt(declared.name, global.GetInt(declared.name));
                        break;
                    case EDialogueVariableType.Float:
                        target.SetFloat(declared.name, global.GetFloat(declared.name));
                        break;
                }
            }
        }

        /// <summary>当前事实表快照（诊断日志用）</summary>
        public static string Describe()
        {
            var sb = new StringBuilder();
            foreach (var pair in keyTypes)
            {
                if (sb.Length > 0) sb.Append(", ");
                switch (pair.Value)
                {
                    case EDialogueVariableType.Bool: sb.Append($"{pair.Key}={global.GetBool(pair.Key)}"); break;
                    case EDialogueVariableType.Int: sb.Append($"{pair.Key}={global.GetInt(pair.Key)}"); break;
                    case EDialogueVariableType.Float: sb.Append($"{pair.Key}={global.GetFloat(pair.Key)}"); break;
                }
            }
            return sb.Length > 0 ? sb.ToString() : "(空)";
        }
    }
}
