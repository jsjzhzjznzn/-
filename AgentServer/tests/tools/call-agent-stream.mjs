/**
 * 联调小工具（非测试）：调 /agent/chat/stream，按到达顺序打印 ndjson 事件。
 *
 * 用法：
 *   node tests/tools/call-agent-stream.mjs <playerId> "<问题>" [port]
 *
 * 输出末尾给出「打字机视角」的净结果：reset 之后累积的文本应与 done.reply 一致
 * （reset 之前的前导文本在客户端会被清空）。
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

const [playerArg, content, portArg] = process.argv.slice(2);
if (!playerArg || !content) {
  console.error('用法: node tests/tools/call-agent-stream.mjs <playerId> "<问题>" [port]');
  process.exit(2);
}
const playerId = Number.parseInt(playerArg, 10);
const port = Number.parseInt(portArg ?? "8890", 10);
const token = envValue("AGENT_INTERNAL_TOKEN");

const started = Date.now();
const response = await fetch(`http://127.0.0.1:${port}/agent/chat/stream`, {
  method: "POST",
  headers: { "Content-Type": "application/json", ...(token ? { "X-Agent-Token": token } : {}) },
  body: JSON.stringify({ player_id: playerId, content, context: {} }),
});

console.log(`HTTP ${response.status} content-type=${response.headers.get("content-type")}`);

let buffers = "";
let visible = ""; // 打字机视角：reset 后清空
let resets = 0;
let deltas = 0;
let firstDeltaMs = null;
let done = null;
let error = null;

const decoder = new TextDecoder("utf-8");
let carry = "";
for await (const chunk of response.body) {
  carry += decoder.decode(chunk, { stream: true });
  let index;
  while ((index = carry.indexOf("\n")) >= 0) {
    const line = carry.slice(0, index).trim();
    carry = carry.slice(index + 1);
    if (!line) continue;
    const event = JSON.parse(line);
    switch (event.type) {
      case "delta":
        deltas += 1;
        firstDeltaMs ??= Date.now() - started;
        visible += event.text;
        console.log(`[delta +${Date.now() - started}ms] ${JSON.stringify(event.text)}`);
        break;
      case "reset":
        resets += 1;
        visible = "";
        console.log(`[reset +${Date.now() - started}ms]`);
        break;
      case "done":
        done = event;
        console.log(`[done +${Date.now() - started}ms] reply_len=${event.reply?.length}`);
        break;
      case "error":
        error = event;
        console.log(`[error +${Date.now() - started}ms] ${event.error}`);
        break;
      default:
        console.log(`[?] ${line}`);
    }
  }
}

console.log("\n── 汇总 ──");
console.log(`delta=${deltas} reset=${resets} 首段延迟=${firstDeltaMs}ms`);
console.log(`done.reply     = ${JSON.stringify(done?.reply ?? null)}`);
console.log(`打字机净结果   = ${JSON.stringify(visible)}`);
console.log(`两者一致       = ${done ? visible === done.reply : "（未收到 done）"}`);
if (error) console.log(`error          = ${error.error}`);
