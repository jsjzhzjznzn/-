using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;

namespace MH
{
    /// <summary>
    /// 枚举扩展工具：读取枚举值上 [Description("描述")] 特性里配置的中文文本。
    /// 红点系统里用于把枚举名（如 Key1）显示成人话（如"主界面"），
    /// 编辑器窗口、Inspector 下拉列表都依赖它。
    /// </summary>
    public static class EnumExtensions
    {
        // 缓存反射结果，避免每次调用都反射（Description 不会变，反射一次后永久缓存）
        private static readonly Dictionary<Enum, string> _descriptionCache = new Dictionary<Enum, string>();

        /// <summary>获取枚举值的 [Description] 描述文本；未打该特性时回退为枚举名字符串</summary>
        public static string GetDescription(this Enum value)
        {
            // 先查缓存，命中直接返回
            if (_descriptionCache.TryGetValue(value, out string cached))
                return cached;

            // 反射获取枚举字段，读取其上的 DescriptionAttribute
            FieldInfo field = value.GetType().GetField(value.ToString());
            if (field == null)
            {
                // 找不到字段（如枚举值非法）时回退为枚举名，并写入缓存
                string result = value.ToString();
                _descriptionCache[value] = result;
                return result;
            }

            DescriptionAttribute attribute = Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute)) as DescriptionAttribute;
            string description = attribute == null ? value.ToString() : attribute.Description;
            _descriptionCache[value] = description;  // 写入缓存
            return description;
        }
    }
}
