using System.Security.Cryptography;
using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 自动登录令牌仓库类
    /// remember me 的服务端账本：签发（密码登录勾选时）与校验（令牌登录时）
    /// 安全取舍：库里只存明文令牌的 SHA-256 哈希，明文只在签发响应里出现一次；
    /// 每玩家单活跃令牌（PRIMARY KEY player_id），重新签发即旧令牌作废，等效令牌轮换
    /// </summary>
    public class AutoLoginRepository
    {
        /// <summary>令牌有效期（秒），默认 7 天；env CS_AUTOLOGIN_TOKEN_TTL_SECONDS 可覆盖（联调测过期用）</summary>
        public static long TokenTtlSeconds
        {
            get
            {
                string? raw = Environment.GetEnvironmentVariable("CS_AUTOLOGIN_TOKEN_TTL_SECONDS");
                return long.TryParse(raw, out long seconds) && seconds > 0 ? seconds : 7 * 24 * 3600;
            }
        }

        /// <summary>
        /// 幂等建表方法
        /// 与 Migrations\013_AddAutoLogin.sql 等价，已有表无副作用
        /// </summary>
        public void EnsureTable()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                @"CREATE TABLE IF NOT EXISTS `auto_login_tokens` (
                  `player_id`       INT       NOT NULL COMMENT '玩家ID = accounts.Id（一玩家一活跃令牌）',
                  `token_hash`      CHAR(64)  NOT NULL COMMENT '明文令牌的 SHA-256 hex（不存明文）',
                  `created_at_ms`   BIGINT    NOT NULL COMMENT '签发时间（Unix 毫秒）',
                  `expires_at_ms`   BIGINT    NOT NULL COMMENT '过期时间（Unix 毫秒，滑动续期）',
                  `last_used_at_ms` BIGINT    NOT NULL COMMENT '最近一次令牌登录时间（Unix 毫秒）',
                  PRIMARY KEY (`player_id`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='自动登录令牌表（remember me，库内只存哈希）'",
                connection);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// 签发令牌方法
        /// 32 字节加密随机数作明文令牌，SHA-256 哈希入库；同玩家重复签发覆盖旧行（旧令牌立即作废）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <returns>返回值类型：(string Token, long ExpireMs)，用途：明文令牌（只回给客户端这一次）与过期时间</returns>
        public (string Token, long ExpireMs) IssueToken(int playerId)
        {
            byte[] raw = new byte[32];
            RandomNumberGenerator.Fill(raw);
            string token = Convert.ToHexString(raw);   // 64 字符 hex
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long expireMs = nowMs + TokenTtlSeconds * 1000;

            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                @"INSERT INTO auto_login_tokens (player_id, token_hash, created_at_ms, expires_at_ms, last_used_at_ms)
                  VALUES (@PlayerId, @TokenHash, @Now, @Expire, @Now)
                  ON DUPLICATE KEY UPDATE
                    token_hash = VALUES(token_hash),
                    created_at_ms = VALUES(created_at_ms),
                    expires_at_ms = VALUES(expires_at_ms),
                    last_used_at_ms = VALUES(last_used_at_ms)",
                connection);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.Parameters.AddWithValue("@TokenHash", HashToken(token));
            command.Parameters.AddWithValue("@Now", nowMs);
            command.Parameters.AddWithValue("@Expire", expireMs);
            command.ExecuteNonQuery();

            Console.WriteLine($"自动登录令牌已签发:玩家{playerId} 有效期{TokenTtlSeconds}s（明文不落日志）");
            return (token, expireMs);
        }

        /// <summary>
        /// 校验令牌方法
        /// 命中且未过期 → 滑动续期（expires_at 顺延 TTL）并返回新过期时间；不命中/已过期一律失败
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID（令牌归属校验）</param>
        /// <param name="token">参数名：token，类型：string，用途：客户端提交的明文令牌</param>
        /// <returns>返回值类型：(bool Valid, long ExpireMs)，用途：是否有效与续期后的过期时间（失败为0）</returns>
        public (bool Valid, long ExpireMs) ValidateToken(int playerId, string token)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string tokenHash = HashToken(token);

            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using (MySqlCommand selectCommand = new(
                "SELECT expires_at_ms FROM auto_login_tokens WHERE player_id = @PlayerId AND token_hash = @TokenHash",
                connection))
            {
                selectCommand.Parameters.AddWithValue("@PlayerId", playerId);
                selectCommand.Parameters.AddWithValue("@TokenHash", tokenHash);
                object? result = selectCommand.ExecuteScalar();
                if (result == null) return (false, 0);
                if (Convert.ToInt64(result) < nowMs) return (false, 0);   // 已过期
            }

            // 滑动续期：本次成功使用把过期时间顺延一个 TTL
            long newExpireMs = nowMs + TokenTtlSeconds * 1000;
            using (MySqlCommand updateCommand = new(
                "UPDATE auto_login_tokens SET expires_at_ms = @Expire, last_used_at_ms = @Now WHERE player_id = @PlayerId AND token_hash = @TokenHash",
                connection))
            {
                updateCommand.Parameters.AddWithValue("@Expire", newExpireMs);
                updateCommand.Parameters.AddWithValue("@Now", nowMs);
                updateCommand.Parameters.AddWithValue("@PlayerId", playerId);
                updateCommand.Parameters.AddWithValue("@TokenHash", tokenHash);
                updateCommand.ExecuteNonQuery();
            }

            Console.WriteLine($"自动登录令牌校验通过:玩家{playerId} 续期至+{TokenTtlSeconds}s");
            return (true, newExpireMs);
        }

        /// <summary>
        /// 令牌哈希方法
        /// SHA-256 hex（64 字符），与表列 token_hash 同口径
        /// </summary>
        /// <param name="token">参数名：token，类型：string，用途：明文令牌</param>
        /// <returns>返回值类型：string，用途：十六进制哈希字符串</returns>
        private static string HashToken(string token)
        {
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        }
    }
}
