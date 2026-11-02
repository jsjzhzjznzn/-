/**
 * 并发验收（P7，非测试）：验证「同玩家串行、不同玩家并行」与并发下无错误。
 *
 * 用法：node tests/tools/concurrency-probe.mjs [port]
 */
import { readFileSync } from "node:fs";
import path from "node:path";

const ROOT = path.resolve(import.meta.dirname, "..", "..");
const token = (/^AGENT_INTERNAL_TOKEN\s*=\s*(.*)$/m.exec(readFileSync(path.join(ROOT, ".env"), "utf8"))?.[1] ?? "").trim();
const port = Number.parseInt(process.argv[2] ?? "8898", 10);

async function ask(playerId, content) {
  const started = Date.now();
  const response = await fetch(`http://127.0.0.1:${port}/agent/chat`, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Agent-Token": token },
    body: JSON.stringify({ player_id: playerId, content, context: {} }),
  });
  const json = await response.json();
  return { playerId, status: response.status, ok: json.ok, elapsed: Date.now() - started, tools: json.used_tools };
}

console.log("── A 同玩家并发（应串行：总耗时≈两轮之和，无错误）──");
{
  const started = Date.now();
  const results = await Promise.all([
    ask(50, "用一句话说明背包容量是多少"),
    ask(50, "用一句话说明排行榜第一名多少分"),
  ]);
  const total = Date.now() - started;
  for (const r of results) console.log(`  player=${r.playerId} ok=${r.ok} 单轮耗时=${r.elapsed}ms tools=${JSON.stringify(r.tools)}`);
  console.log(`  并发总耗时=${total}ms（串行则≈两轮之和）`);
  console.log(`  两个都成功 = ${results.every((r) => r.ok === true)}`);
}

console.log("── B 不同玩家并发（应并行：总耗时≈最慢那一轮）──");
{
  const started = Date.now();
  const results = await Promise.all([
    ask(50, "用一句话说明副本怪物多久刷新"),
    ask(902, "用一句话说明副本怪物多久刷新"),
  ]);
  const total = Date.now() - started;
  for (const r of results) console.log(`  player=${r.playerId} ok=${r.ok} 单轮耗时=${r.elapsed}ms tools=${JSON.stringify(r.tools)}`);
  console.log(`  并发总耗时=${total}ms（并行则≈最慢那一轮）`);
  console.log(`  两个都成功 = ${results.every((r) => r.ok === true)}`);
}
