namespace MieMieFrameWork
{
    /// <summary>
    /// 框架日志薄封装：统一走项目现有的 GameLog（总开关与 Verbose 开关都生效）
    /// </summary>
    public static class FrameLog
    {
        /// <summary>
        /// 结构化上下文（对应源框架的日志上下文），本项目直接忽略，保留签名以便移植代码不改调用处
        /// </summary>
        public static void SetContext(string key, object value)
        {
        }

        public static void Info(string message, string category = "Frame")
        {
            GameLog.Info($"[{category}] {message}");
        }

        public static void Warn(string message, string category = "Frame")
        {
            GameLog.Warn($"[{category}] {message}");
        }

        public static void Error(string message, string category = "Frame")
        {
            GameLog.Error($"[{category}] {message}");
        }
    }
}
