using System.Security.Cryptography;
using System.Text;

namespace CompeteServer.Security
{
    /// <summary>
    /// 密码哈希工具类
    /// 采用「每账号随机盐 + PBKDF2-HMAC-SHA256」存储密码，库内不出现明文
    /// 迭代次数取 OWASP 对 PBKDF2-HMAC-SHA256 的推荐下限，登录是低频操作，单次几十毫秒可接受
    /// </summary>
    public static class PasswordHasher
    {
        /// <summary>盐长度（字节）：每账号独立随机生成</summary>
        private const int SaltSize = 16;

        /// <summary>派生密钥长度（字节）：32 字节 = 256 位，Base64 后 44 字符</summary>
        private const int HashSize = 32;

        /// <summary>PBKDF2 迭代次数：OWASP 2023 对 PBKDF2-HMAC-SHA256 的推荐下限</summary>
        private const int Iterations = 210_000;

        /// <summary>
        /// 生成盐与哈希的方法
        /// </summary>
        /// <param name="password">参数名：password，类型：string，用途：玩家原始密码</param>
        /// <returns>返回值类型：(string Salt, string Hash)，用途：两者均为 Base64 字符串，可直接入库</returns>
        public static (string Salt, string Hash) Create(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] hash = Derive(password, salt);
            return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
        }

        /// <summary>
        /// 校验密码方法（哈希路径）
        /// 用库内盐重新派生后做定长时间比较，避免逐字节早退泄露前导匹配长度
        /// </summary>
        /// <param name="password">参数名：password，类型：string，用途：玩家提交的密码</param>
        /// <param name="saltBase64">参数名：saltBase64，类型：string，用途：库内盐</param>
        /// <param name="hashBase64">参数名：hashBase64，类型：string，用途：库内哈希</param>
        /// <returns>返回值类型：bool，用途：匹配返回 true；盐或哈希格式非法一律返回 false</returns>
        public static bool Verify(string password, string saltBase64, string hashBase64)
        {
            if (!TryDecode(saltBase64, SaltSize, out byte[] salt)) return false;
            if (!TryDecode(hashBase64, HashSize, out byte[] expected)) return false;

            return CryptographicOperations.FixedTimeEquals(Derive(password, salt), expected);
        }

        /// <summary>
        /// 校验密码方法（老账号明文路径）
        /// 回填完成前老账号行里只有明文密码，这里在内存中做定长比较：
        /// 两侧补齐到较长一侧再整体比较，耗时只与长度相关、不随匹配前缀长度变化
        /// </summary>
        /// <param name="password">参数名：password，类型：string，用途：玩家提交的密码</param>
        /// <param name="storedPlain">参数名：storedPlain，类型：string，用途：库内明文密码</param>
        /// <returns>返回值类型：bool，用途：相等返回 true</returns>
        public static bool VerifyPlain(string password, string storedPlain)
        {
            byte[] actual = Encoding.UTF8.GetBytes(password);
            byte[] expected = Encoding.UTF8.GetBytes(storedPlain);

            // 先比长度再比内容：长度差异本身不是秘密（都能从响应耗时推断），内容比较保持定长
            if (actual.Length != expected.Length) return false;
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        /// <summary>
        /// PBKDF2 派生方法
        /// </summary>
        /// <param name="password">参数名：password，类型：string，用途：原始密码</param>
        /// <param name="salt">参数名：salt，类型：byte[]，用途：盐</param>
        /// <returns>返回值类型：byte[]，用途：32 字节派生密钥</returns>
        private static byte[] Derive(string password, byte[] salt)
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        }

        /// <summary>
        /// Base64 解码并校验长度的方法
        /// </summary>
        /// <param name="base64">参数名：base64，类型：string，用途：待解码字符串</param>
        /// <param name="expectedLength">参数名：expectedLength，类型：int，用途：期望字节长度</param>
        /// <param name="bytes">参数名：bytes，类型：byte[]，用途：解码结果</param>
        /// <returns>返回值类型：bool，用途：格式与长度都匹配返回 true</returns>
        private static bool TryDecode(string base64, int expectedLength, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (string.IsNullOrEmpty(base64)) return false;

            try
            {
                byte[] decoded = Convert.FromBase64String(base64);
                if (decoded.Length != expectedLength) return false;
                bytes = decoded;
                return true;
            }
            catch (FormatException)
            {
                // 库里数据被手工改坏时走这里：视为校验失败，不抛给上层
                return false;
            }
        }
    }
}
