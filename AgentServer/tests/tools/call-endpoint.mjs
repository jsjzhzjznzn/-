/**
 * 联调小工具（非测试）：调任意 Agent 端点（GET/POST），打印响应。
 *
 * 用法：
 *   node tests/tools/call-endpoint.mjs <path> [jsonBody] [port]
 * 例：
 *   node tests/tools/call-endpoint.mjs /agent/history '{"player_id":50,"limit":5}'
 *   node tests/tools/call-endpoint.mjs /agent/debug/50 "" 8890
 *   node tests/tools/call-endpoint.mjs /agent/proactive '{"player_id":50,"kind":1,"data":{"title":"测试"}}'
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
    /* 无 .env */
  }
  return "";
}

const [endpoint, bodyArg, portArg] = process.argv.slice(2);
if (!endpoint) {
  console.error('用法: node tests/tools/call-endpoint.mjs <path> [jsonBody|k=v&k=v] [port]');
  process.exit(2);
}
const port = Number.parseInt(portArg ?? "8890", 10);
const token = envValue("AGENT_INTERNAL_TOKEN");

// PowerShell 会把空参数传成字面量 ""，也会吞掉 JSON 里的双引号 —— 两种都要兼容：
//  - '""' / "''" / 缺省  → 无 body（GET）
//  - 以 { 开头            → 直接当 JSON
//  - a=1&b=xx            → 组装 JSON（数字自动转 number）
const raw = (bodyArg ?? "").replace(/^["']+|["']+$/g, "");
let body = "";
// "-" = 显式无 body（PowerShell 会直接丢掉空参数，导致位置参数错位，用它占位）
if (raw && raw !== "-") {
  body = raw.trimStart().startsWith("{")
    ? raw
    : JSON.stringify(
        Object.fromEntries(
          raw
            .split("&")
            .filter(Boolean)
            .map((pair) => {
              const [key, ...rest] = pair.split("=");
              const value = rest.join("=");
              return [key, /^-?\d+(\.\d+)?$/.test(value) ? Number(value) : value];
            }),
        ),
      );
}
const hasBody = body.length > 0;

const started = Date.now();
const response = await fetch(`http://127.0.0.1:${port}${endpoint}`, {
  method: hasBody ? "POST" : "GET",
  headers: {
    ...(hasBody ? { "Content-Type": "application/json" } : {}),
    ...(token ? { "X-Agent-Token": token } : {}),
  },
  ...(hasBody ? { body } : {}),
});
const text = await response.text();
console.log(`HTTP ${response.status} (${Date.now() - started}ms) ${hasBody ? `body=${body}` : ""}`);
try {
  console.log(JSON.stringify(JSON.parse(text), null, 2));
} catch {
  console.log(text);
}

