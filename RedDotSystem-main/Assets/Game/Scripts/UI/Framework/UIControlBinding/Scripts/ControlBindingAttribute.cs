using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SkierFramework
{
    public class ControlBindingAttribute : Attribute
    {
        /// <summary>
        /// 可选的子物体路径（相对面板根）。为空时迁移工具按“字段名/PascalCase”自动找同名节点；
        /// 字段名与节点名不一致（如 leaderboardScroll -> LeaderboardList）时用它显式指定。
        /// 运行时绑定仍使用字段名作为 UIControlData.name，此字段只给编辑器迁移工具用。
        /// </summary>
        public string path;

        public ControlBindingAttribute(string path = null)
        {
            this.path = path;
        }
    }
}