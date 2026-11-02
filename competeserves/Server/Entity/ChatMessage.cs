using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

// 命名空间：CompeteServer.Entity
// 属于服务端实体层，存放运行时数据对象
namespace CompeteServer.Entity
{
    /// <summary>
    /// 聊天消息实体类
    /// 对应 MongoDB 的 messages 集合，一条消息一个文档（不用内嵌数组，避免 16MB 文档上限）
    /// 身份键沿用 accounts.Id（与 PlayerEntity.PlayerId 同源）
    /// </summary>
    public class ChatMessage
    {
        /// <summary>
        /// 消息主键
        /// 类型：ObjectId
        /// 用途：MongoDB _id，查询为空时（default）由驱动在插入时自动生成
        /// </summary>
        [BsonId]
        public ObjectId Id;

        /// <summary>
        /// 会话键
        /// 类型：string
        /// 用途：双方玩家ID归一化后的字符串（小的在前，如 "3_7"），AB 与 BA 是同一个值
        /// 说明：由 ChatRepository.BuildConversationId 在服务端生成，绝不来自客户端
        /// </summary>
        [BsonElement("conversationId")]
        public string ConversationId = string.Empty;

        /// <summary>
        /// 发送者玩家ID
        /// 类型：int
        /// 用途：accounts.Id，服务端从会话身份推导，客户端无法伪造
        /// </summary>
        [BsonElement("senderId")]
        public int SenderId;

        /// <summary>
        /// 接收者玩家ID
        /// 类型：int
        /// 用途：accounts.Id
        /// </summary>
        [BsonElement("receiverId")]
        public int ReceiverId;

        /// <summary>
        /// 消息内容
        /// 类型：string
        /// 用途：文本消息正文；内容类型为表情时存的是表情键（客户端 Resources/biaoq 下的资源名）
        /// </summary>
        [BsonElement("content")]
        public string Content = string.Empty;

        /// <summary>
        /// 消息内容类型
        /// 类型：int
        /// 用途：0=文本（默认）；1=表情包（此时 Content 是表情键，不是正文）
        /// 说明：表情包上线（2026-10-08）之前写入的文档没有该字段，
        ///       驱动反序列化后即为 0=文本，老聊天记录天然兼容，无需数据迁移
        /// </summary>
        [BsonElement("contentType")]
        public int ContentType;

        /// <summary>
        /// 发送时间
        /// 类型：DateTime
        /// 用途：服务端 UTC 时间（客户端时钟不可信），消息排序与翻页游标都用它
        /// </summary>
        [BsonElement("sentAt")]
        public DateTime SentAtUtc;

        /// <summary>
        /// 是否已读
        /// 类型：bool
        /// 用途：接收方是否已读该条消息（未读数统计用）
        /// </summary>
        [BsonElement("read")]
        public bool Read;
    }
}
