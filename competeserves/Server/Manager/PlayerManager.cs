using System.Linq;

// 命名空间：CompeteServer.Manager
// 属于服务端管理层，负责各类管理器组件的实现
namespace CompeteServer.Manager
{
    /// <summary>
    /// 玩家管理器类
    /// 负责管理所有在线玩家的实体数据（谁在线、在线几个、都是谁）
    /// 采用单例模式，确保全局只有一个玩家管理器实例
    /// 后续聊天/好友等系统从这里查询在线状态
    /// </summary>
    public class PlayerManager
    {
        // ──────────────────────────────
        // 单例实现
        // ──────────────────────────────

        /// <summary>
        /// 懒加载单例载体
        /// 用途：Lazy&lt;T&gt; 保证多线程首次访问只创建一次实例
        /// </summary>
        private static readonly Lazy<PlayerManager> LazyInstance = new(() => new PlayerManager());

        /// <summary>
        /// 公开静态属性 - 获取单例实例
        /// 用途：线程安全懒加载的全局访问点
        /// </summary>
        public static PlayerManager Instance => LazyInstance.Value;

        // ──────────────────────────────
        // 私有字段
        // ──────────────────────────────

        /// <summary>
        /// 在线玩家字典
        /// 类型：Dictionary&lt;int, PlayerEntity&gt;
        /// 用途：Key为玩家唯一ID（accounts.Id），Value为玩家实体对象
        /// </summary>
        private readonly Dictionary<int, Entity.PlayerEntity> players;

        /// <summary>
        /// 在线玩家集合线程锁
        /// 类型：object
        /// 用途：保护网络线程并发访问玩家集合（登录/断线来自不同会话线程）
        /// </summary>
        private readonly object sync = new object();

        // ──────────────────────────────
        // 构造函数
        // ──────────────────────────────

        /// <summary>
        /// 私有构造函数
        /// 初始化在线玩家字典，防止外部实例化，确保单例模式
        /// </summary>
        private PlayerManager()
        {
            players = new Dictionary<int, Entity.PlayerEntity>();
        }

        // ──────────────────────────────
        // 公共方法
        // ──────────────────────────────

        /// <summary>
        /// 添加玩家到在线列表的方法
        /// 玩家登录成功后调用此方法
        /// </summary>
        /// <param name="player">参数名：player，类型：PlayerEntity，用途：要添加的玩家实体对象</param>
        public void AddPlayer(Entity.PlayerEntity player)
        {
            lock (sync)
            {
                players[player.PlayerId] = player;
                Console.WriteLine($"玩家加入:{player.Account} | 当前在线:{players.Count}人 [{GetOnlineNamesLocked()}]");
            }
        }

        /// <summary>
        /// 从在线列表移除玩家的方法
        /// 玩家断开连接或下线时调用此方法
        /// </summary>
        /// <param name="id">参数名：id，类型：int，用途：要移除的玩家唯一ID</param>
        public void RemovePlayer(int id)
        {
            lock (sync)
            {
                if (players.Remove(id))
                {
                    Console.WriteLine($"玩家下线:Id={id} | 剩余在线:{players.Count}人 [{GetOnlineNamesLocked()}]");
                }
            }
        }

        /// <summary>
        /// 根据玩家ID获取玩家实体的方法
        /// 用于查找特定在线玩家（如好友私聊定向、怪物索敌）
        /// </summary>
        /// <param name="id">参数名：id，类型：int，用途：要查找的玩家唯一ID</param>
        /// <returns>返回值类型：PlayerEntity?，用途：在线返回实体，不在线返回null</returns>
        public Entity.PlayerEntity? GetPlayer(int id)
        {
            lock (sync)
            {
                return players.TryGetValue(id, out Entity.PlayerEntity? player) ? player : null;
            }
        }

        /// <summary>
        /// 获取在线玩家快照的方法
        /// 使用快照遍历，避免枚举期间集合被登录或断线线程修改
        /// </summary>
        /// <returns>返回值类型：IReadOnlyList&lt;PlayerEntity&gt;，用途：在线玩家只读快照</returns>
        public IReadOnlyList<Entity.PlayerEntity> GetAll()
        {
            lock (sync) return players.Values.ToArray();
        }

        /// <summary>
        /// 获取当前在线玩家数量的方法
        /// </summary>
        /// <returns>返回值类型：int，用途：当前在线玩家总数</returns>
        public int PlayerCount()
        {
            lock (sync) return players.Count;
        }

        /// <summary>
        /// 获取在线玩家名列表的方法（逗号分隔）
        /// 日志展示与后续"在线列表"功能使用
        /// </summary>
        public string GetOnlineNames()
        {
            lock (sync) return GetOnlineNamesLocked();
        }

        /// <summary>
        /// 获取在线玩家名列表的方法（需在锁内调用）
        /// </summary>
        private string GetOnlineNamesLocked()
        {
            return string.Join(", ", players.Values.Select(player => player.Account));
        }
    }
}
