/**
 * Reflection 反思质检（逐字搬 reflection.py，方案 §11）。
 *
 * 定位：生成后的自评（v1 只有生成前的确定性守卫）。只在「用过数据类工具」的回合触发——
 * 拿着工具返回的真实数据对照检查答案，比自由文本自评确定性强（对照校验版 Reflexion）。
 *
 * 宽松原则（宁可放过不可误杀）：不确定一律判过；质检器自身任何异常 fail-open 放行
 * ——反思是增强不是闸门，不能因为质检基础设施抖动挡住正常回复。
 */
import { CRITIC_FORCE_FAIL, CRITIC_MAX_TOOL_CONTEXT_CHARS, TEMPERATURE_CRITIC } from "../core/config.js";
import { log } from "../core/logger.js";
import { completeText } from "../llm/oneShot.js";

const CRITIC_SYSTEM = "你是回复质检器，只输出一行 JSON，不要任何其它文字。";

/** 解析质检器输出的一行 JSON；解析失败按通过处理（fail-open）。 */
export function parseVerdict(text: string): { passed: boolean; reason: string } {
  let cleaned = (text ?? "").trim();
  if (cleaned.startsWith("```")) {
    cleaned = cleaned.replace(/`/g, "");
    if (cleaned.toLowerCase().startsWith("json")) cleaned = cleaned.slice(4);
    cleaned = cleaned.trim();
  }
  const start = cleaned.indexOf("{");
  const end = cleaned.lastIndexOf("}");
  if (start < 0 || end <= start) return { passed: true, reason: "" };

  let verdict: unknown;
  try {
    verdict = JSON.parse(cleaned.slice(start, end + 1));
  } catch {
    return { passed: true, reason: "" };
  }
  if (!verdict || typeof verdict !== "object") return { passed: true, reason: "" };
  const record = verdict as { pass?: unknown; reason?: unknown };
  if (record.pass === true || record.pass === undefined || record.pass === null) {
    return { passed: true, reason: "" };
  }
  const reason = String(record.reason ?? "回复未通过对照校验").trim().slice(0, 120);
  return { passed: false, reason: reason || "回复未通过对照校验" };
}

export interface ReflectOptions {
  /**
   * 已知事实（长期记忆 L3：称呼/偏好/习惯……）。
   *
   * 为什么必须给质检器看：实测误判案例（2026-10-18）——玩家 50 的长期记忆里写着
   * 「称呼：玩家自称蛋哥」，答案按阿福的口吻叫了「蛋哥」，而工具结果里的角色名是
   * pi_claim_e2e，于是质检器判「编造/矛盾」→ 用一条 38 字的敷衍重答顶掉了 381 字的
   * 正确成长路线。质检器看不到长期记忆，就会把合法事实当成编造。
   *
   * 注意：**只补长期记忆，不补更早的工具结果**——「复述过期游戏数字」是质检要守的
   * 防线（D2），把旧工具结果喂给它等于自废武功。
   */
  knownFacts?: readonly string[];
}

/**
 * 对照校验一次回复。
 *
 * @param question 玩家本轮问题
 * @param toolResults 本轮全部数据类工具结果（已含工具名前缀）
 * @param answer 待检答案
 * @returns passed=false 时 reason 是给【反思】批评注入的一句话病因
 */
export async function reflect(
  question: string,
  toolResults: readonly string[],
  answer: string,
  options: ReflectOptions = {},
  signal?: AbortSignal,
): Promise<{ passed: boolean; reason: string }> {
  if (CRITIC_FORCE_FAIL) {
    // 联调开关：确定性验证「判不过 → 重答一次」路径
    log.info("质检强制判不过（AGENT_CRITIC_FORCE_FAIL=1）");
    return { passed: false, reason: "（联调开关）回答与工具结果不一致" };
  }

  const toolsText = toolResults.join("\n").slice(0, CRITIC_MAX_TOOL_CONTEXT_CHARS);
  const knownFacts = (options.knownFacts ?? []).filter((fact) => fact.trim());
  const knownSection =
    knownFacts.length > 0
      ? `【已知事实】（长期记忆/玩家档案，允许直接引用，引用它们不算编造）\n` +
        knownFacts.map((fact) => `- ${fact}`).join("\n") +
        "\n\n"
      : "";

  const prompt =
    "你是游戏助手的回复质检器。对照下面的【工具结果】检查【答案】。\n\n" +
    `【问题】${question.slice(0, 500)}\n\n` +
    knownSection +
    `【工具结果】（真实查询数据）\n${toolsText}\n\n` +
    `【答案】${answer.slice(0, 1500)}\n\n` +
    "检查三点：\n" +
    "1. 答案中所有事实性数字（数量/名次/分数/温度等）是否都能在工具结果中找到，" +
    "或由工具结果简单加减得出，或属于游戏常识（背包容量500、5星保底30抽、" +
    "十连必出4星、邮箱100封、Top100、发言3秒冷却、1.3米自动拾取等）；" +
    "与工具结果矛盾的数字（包括复述更早对话里的旧数据）算错误。" +
    "**玩家的称呼/喜好这类来自长期记忆的信息不算错误**（上面【已知事实】里就有）。\n" +
    "2. 答案是否正面回答了玩家问的问题（答非所问算错误）。\n" +
    "3. 答案是否编造了工具结果里没有、玩家也没问的内容。\n" +
    "宽松原则：无法确定时一律判通过，宁可放过不可误杀。\n" +
    '只输出一行 JSON：{"pass": true} 或 {"pass": false, "reason": "一句话病因"}，' +
    "不要输出任何其它文字。";

  try {
    const text = await completeText(CRITIC_SYSTEM, prompt, TEMPERATURE_CRITIC, undefined, signal);
    return parseVerdict(text);
  } catch (err) {
    log.warn("反思质检异常（fail-open 放行）", err);
    return { passed: true, reason: "" };
  }
}

/** 【反思】批评文案（逐字；注入位置见 hooks.buildCanonicalContext）。 */
export function critiqueBlockText(reason: string): string {
  return (
    `【反思】你刚才的回复没有通过质检：${reason}。请立刻重新回答玩家的问题，` +
    "事实性数字必须与工具结果一致，禁止复述有问题的内容。"
  );
}
