/**
 * 联调小工具（非测试）：直接调本机 Agent 服务的 /agent/chat。
 *
 * 用法：
 *   node tests/tools/call-agent.mjs <playerId> "<问题>" [port] [--context key=value]
 * 例：
 *   node tests/tools/call-agent.mjs 1 "我昵称是什么？" 8890
 *
 * 为什么不用 curl：PowerShell 会吞掉 JSON 里的双引号，中文也会按控制台编码转换。
 * 这里直接读 .env 取令牌、用 fetch 发 UTF-8 JSON，结果原样打印。
 */
import { readFileSync } from "node:fs";
import path from "node:path";

const ROOT = path.resolve(import.meta.dirname, "..", "..");

function envValue(key) {
  try {
    const text = readFileSync(path.join(ROOT, ".env"), "utf8");
    for (const line of text.split(/\r?\n/)) {
      const match = /^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$/.exec(line);
      if (match && match[1] === key) return match[2].trim();
    }
  } catch {
    /* 没有 .env 就当作未配置 */
  }
  return "";
}

const [playerArg, content, portArg, ...rest] = process.argv.slice(2);
if (!playerArg || !content) {
  console.error('用法: node tests/tools/call-agent.mjs <playerId> "<问题>" [port] [--context k=v]');
  process.exit(2);
}
const playerId = Number.parseInt(playerArg, 10);
const port = Number.parseInt(portArg ?? "8890", 10);

const context = {};
for (let i = 0; i < rest.length; i += 1) {
  if (rest[i] === "--context" && rest[i + 1]) {
    const [key, ...valueParts] = rest[i + 1].split("=");
    context[key] = valueParts.join("=");
    i += 1;
  }
}

const token = envValue("AGENT_INTERNAL_TOKEN");
const started = Date.now();
const response = await fetch(`http://127.0.0.1:${port}/agent/chat`, {
  method: "POST",
  headers: { "Content-Type": "application/json", ...(token ? { "X-Agent-Token": token } : {}) },
  body: JSON.stringify({ player_id: playerId, content, context }),
});
const text = await response.text();
console.log(`HTTP ${response.status} (${Date.now() - started}ms)`);
try {
  const data = JSON.parse(text);
  console.log(JSON.stringify(data, null, 2));
} catch {
  console.log(text);
}
