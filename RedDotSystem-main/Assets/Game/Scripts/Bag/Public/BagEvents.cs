namespace Game.Bag
{
    using MiMieEventBus;

    /// <summary>
    /// 背包模块对外事件
    /// 模块在业务提交完成后发布，UI 与其它模块订阅；订阅令牌随对象 Dispose
    /// </summary>
    public static class BagEvents
    {
        /// <summary>背包数据变化（列表到达 / 任意操作成功后触发，UI 重渲染）</summary>
        public static readonly EventKey Updated = new EventKey("Bag.Updated");

        /// <summary>提示文案（操作成功/失败的 message）</summary>
        public static readonly EventKey<string> Status = new EventKey<string>("Bag.Status");
    }
}
