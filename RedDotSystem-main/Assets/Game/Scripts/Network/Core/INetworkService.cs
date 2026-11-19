namespace Game.Network
{
    using System;
    using System.Threading.Tasks;

    using Common.Enum;
    using Common.Network;

    using Google.Protobuf;

    using MieMieFrameWork.Business;

    /// <summary>
    /// 网络传输服务：连接、收发、按消息类型分发
    /// 业务模块只依赖本接口，不再直接访问 ClientNetworkManager 或它的具名事件
    /// </summary>
    public interface INetworkService : IGameService
    {
        /// <summary>是否已连接服务器</summary>
        bool IsConnected { get; }

        /// <summary>连接服务器；失败返回 false，不抛异常</summary>
        Task<bool> ConnectAsync(string host, int port);

        /// <summary>主动断开，同时停止自动重连</summary>
        void Disconnect();

        /// <summary>发送一条协议消息</summary>
        Task SendAsync(MessageID id, IMessage message);

        /// <summary>
        /// 订阅一种回包；回调在主线程派发（每帧最多 64 包）
        /// 返回的令牌 Dispose 后取消订阅
        /// </summary>
        IDisposable Subscribe<TMessage>(Action<TMessage> handler) where TMessage : class, IMessage;
    }

    /// <summary>
    /// 订阅令牌：Dispose 即退订
    /// </summary>
    internal sealed class NetworkSubscription : IDisposable
    {
        private Action unsubscribe;

        public NetworkSubscription(Action unsubscribe)
        {
            this.unsubscribe = unsubscribe;
        }

        public void Dispose()
        {
            unsubscribe?.Invoke();
            unsubscribe = null;
        }
    }
}
