namespace Game.Equipment
{
    using MiMieEventBus;

    /// <summary>
    /// 装备模块对外事件
    /// </summary>
    public static class EquipmentEvents
    {
        /// <summary>装备数据变化（列表到达 / 穿脱结果）</summary>
        public static readonly EventKey Changed = new EventKey("Equipment.Changed");

        /// <summary>提示文案（操作成功/失败的 message）</summary>
        public static readonly EventKey<string> Status = new EventKey<string>("Equipment.Status");

        /// <summary>穿脱结果（成功与否 + 文案）</summary>
        public static readonly EventKey<bool, string> Result = new EventKey<bool, string>("Equipment.Result");
    }
}
