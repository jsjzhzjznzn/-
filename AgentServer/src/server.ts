/**
 * 服务入口（对应 Python 版的 `uvicorn app:app --host 127.0.0.1 --port 8898`）。
 *
 * 只做「进程级装配」：启动 HTTP 服务、注册优雅退出与兜底错误日志。
 * 端口默认 8898（= .NET 的 CS_AGENT_PYTHON_URL 默认地址）；开发期用 AGENT_PORT 覆盖。
 */
import process from "node:process";

import { flushLogs, log } from "./core/logger.js";
import { shutdown, startServer } from "./http/server.js";

// 未捕获异常不能静默（Python 版由 uvicorn 记录）
process.on("uncaughtException", (err) => {
  log.error("uncaughtException", err);
});
process.on("unhandledRejection", (err) => {
  log.error("unhandledRejection", err);
});

// 被强杀/异常退出时也把缓冲日志落盘（exit 钩子只能做同步操作）
process.on("exit", () => flushLogs());

for (const sig of ["SIGINT", "SIGTERM"] as const) {
  process.on(sig, () => {
    void shutdown(sig).then(() => process.exit(0));
  });
}

startServer();
