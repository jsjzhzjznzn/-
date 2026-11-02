// 命名空间：CompeteServer.Entity
// 属于服务端实体层，存放运行时数据对象
namespace CompeteServer.Entity
{
    /// <summary>
    /// 玩家实体类
    /// 登录成功后在服务端内存中代表一名在线玩家
    /// 后续玩法状态（位置、血量、等级等）字段统一加在这里，客户端不持有权威值
    /// </summary>
    public class PlayerEntity
    {
        /// <summary>
        /// 玩家唯一ID
        /// 类型：int
        /// 用途：即数据库 accounts.Id，登录会话与各类系统的身份键
        /// </summary>
        public int PlayerId;

        /// <summary>
        /// 账号名
        /// 类型：string
        /// 用途：登录账号，展示与日志使用
        /// </summary>
        public string Account = string.Empty;

        /// <summary>
        /// 登录时间
        /// 类型：DateTime
        /// 用途：本次上线时间（UTC），统计与踢线检查可用
        /// </summary>
        public DateTime LoginTimeUtc;
    }
}
