/**
 * L3 长期记忆（Phase 5 ⑦）："记住我叫铁蛋"这类跨对话事实的抽取与落库。
 * 逐字搬 memory.py 的触发正则/抽取提示词/解析容错。
 *
 * - 触发：玩家消息命中"自述类"关键词才跑抽取（便宜 LLM 一次调用，省得每轮都白跑）；
 * - 抽取：从"玩家消息+阿福回复"里提 0-2 条关于玩家的持久事实 {key, content}；
 * - 落库：longterm_memory（同 key 覆盖=更新）；注入走 hooks.buildCanonicalContext；
 * - 无向量检索：条数上限内全量注入（一个玩家的长期事实天然很少，不需要 embedding）；
 * - 长期记忆可变 → 落库即清 qa_cache（身份类缓存回复会过期，与风格切换同款理由）。
 */
import {
  EXTRACT_MAX_CONCURRENCY,
  LTM_CONTENT_MAX_CHARS,
  LTM_KEY_MAX_CHARS,
  LTM_MAX_FACTS_PER_ROUND,
  TEMPERATURE_EXTRACT,
} from "../core/config.js";
import { log } from "../core/logger.js";
import { cacheClear } from "../db/qaCacheRepo.js";
import { putLongterm } from "../db/longtermRepo.js";
import { completeText } from "../llm/oneShot.js";

/** 自述类关键词：命中才值得跑一次抽取调用（确定性强，不烧无谓 token）。 */
const MEMORY_TRIGGERS =
  /我叫|叫我|记住|我的名字|我喜欢|我讨厌|我爱|我习惯|我平时|我常|我每天|喜欢|讨厌|称呼|叫我.*就行/;

/** 抽取用的系统提示（逐字）。 */
const EXTRACT_SYSTEM = "你是事实抽取器，只输出 JSON 数组，不要任何其它文字。";

interface Fact {
  key: string;
  content: string;
}

/** 解析抽取器输出的 JSON 数组；容错剥掉可能的 ```json 围栏。 */
export function parseFacts(text: string): Fact[] {
  let cleaned = (text ?? "").trim();
  if (cleaned.startsWith("```")) {
    cleaned = cleaned.replace(/`/g, "");
    if (cleaned.toLowerCase().startsWith("json")) cleaned = cleaned.slice(4);
    cleaned = cleaned.trim();
  }
  const start = cleaned.indexOf("[");
  const end = cleaned.lastIndexOf("]");
  if (start < 0 || end <= start) return [];
  let parsed: unknown;
  try {
    parsed = JSON.parse(cleaned.slice(start, end + 1));
  } catch {
    return [];
  }
  const out: Fact[] = [];
  if (Array.isArray(parsed)) {
    for (const item of parsed) {
      if (!item || typeof item !== "object") continue;
      const key = String((item as Fact).key ?? "").trim().slice(0, LTM_KEY_MAX_CHARS);
      const content = String((item as Fact).content ?? "").trim().slice(0, LTM_CONTENT_MAX_CHARS);
      if (key && content) out.push({ key, content });
    }
  }
  return out.slice(0, LTM_MAX_FACTS_PER_ROUND);
}

/**
 * 跑一次抽取并落库；返回落库条数（0=无值得记的事实）。失败静默（记忆是锦上添花）。
 * 注意：这里不做"关键词命中判定"之外的门槛——判定在 extractAndStore 开头。
 */
export async function extractAndStore(
  playerId: number,
  userText: string,
  replyText: string,
): Promise<number> {
  if (!MEMORY_TRIGGERS.test(userText)) return 0;

  const prompt =
    "从下面这轮对话里抽取关于【玩家本人】的持久事实（跨对话仍然成立的信息，" +
    '如称呼、喜好、习惯、作息），输出 JSON 数组，每项 {"key": 短主题(2-6字), "content": 事实一句话}。' +
    "没有值得记的就输出 []。不要记游戏数据（背包/任务会变），不要记守岸人说过的话。\n" +
    `玩家说：${userText.slice(0, 300)}\n守岸人回：${replyText.slice(0, 200)}`;

  try {
    const text = await completeText(EXTRACT_SYSTEM, prompt, TEMPERATURE_EXTRACT);
    const facts = parseFacts(text);
    for (const fact of facts) putLongterm(playerId, fact.key, fact.content);
    if (facts.length > 0) {
      cacheClear(playerId); // 身份/偏好类缓存回复会过期，落库即清
      log.info(`长期记忆更新 player=${playerId} 条数=${facts.length}（${facts.map((f) => f.key).join("、")}）`);
    }
    return facts.length;
  } catch (err) {
    log.warn(`长期记忆抽取失败 player=${playerId}`, err);
    return 0;
  }
}

/** 在跑的抽取任务数（进程内；后台任务不能无上限地打上游配额）。 */
let extractInFlight = 0;

/**
 * 回复送出后后台抽取（不阻塞响应；失败只影响本轮记忆，不影响对话）。
 *
 * 并发闸：前台已经拿到回复，没人等这个任务，所以超过上限时**直接丢弃**本轮抽取，
 * 而不是排队——排队只会把上游压力推迟到更晚、把内存留得更久。
 */
export function scheduleExtract(playerId: number, userText: string, replyText: string): void {
  if (extractInFlight >= EXTRACT_MAX_CONCURRENCY) {
    log.debug(`长期记忆抽取达并发上限（${EXTRACT_MAX_CONCURRENCY}），跳过 player=${playerId}`);
    return;
  }
  extractInFlight += 1;
  void extractAndStore(playerId, userText, replyText)
    .catch(() => undefined)
    .finally(() => {
      extractInFlight -= 1;
    });
}

/** 测试/诊断用：当前在跑的抽取数。 */
export function extractLoad(): number {
  return extractInFlight;
}
