/**
 * 登录主动简报（Phase 5 ②）：确定性模板组装，零 LLM 成本。
 * 逐字搬 app.py::_render_login_brief（数据源失败的段落自然省略）。
 */
import { INTERNAL_API_BASE, INTERNAL_TIMEOUT_MS } from "../core/config.js";
import { getJson } from "../core/http.js";

const PLAYER_BASE = `${INTERNAL_API_BASE}/internal/agent/player`;

interface BriefMail {
  unread?: number;
  claimable?: number;
}
interface BriefQuest {
  submittable?: { name?: unknown }[];
}
interface BriefGacha {
  banners?: { total_draws?: number; pity5_remaining?: number; title?: unknown }[];
}

/** 登录简报文案（模板逐字）。 */
export function renderLoginBrief(mail: BriefMail, quests: BriefQuest, gacha: BriefGacha): string {
  const parts: string[] = ["主人回来啦！守岸人给你报个到——"];

  const unread = Number(mail.unread ?? 0);
  const claimable = Number(mail.claimable ?? 0);
  if (unread > 0) {
    let line = `邮箱里有 ${unread} 封未读邮件`;
    if (claimable > 0) line += `，其中 ${claimable} 封的附件还没领`;
    parts.push(line + "，记得去邮箱看看。");
  } else {
    parts.push("邮箱干干净净，没有未读。");
  }

  const submittable = quests.submittable ?? [];
  if (submittable.length > 0) {
    const names = submittable
      .slice(0, 3)
      .map((quest) => String(quest?.name ?? "?"))
      .join("、");
    const more = submittable.length > 3 ? ` 等共 ${submittable.length} 个` : "";
    parts.push(`任务这边有可以提交的了：${names}${more}，交了就能领奖励。`);
  }

  for (const banner of gacha.banners ?? []) {
    // 只报真正抽过卡的池子（没抽过的人不打扰）
    if (Number(banner?.total_draws ?? 0) <= 0) continue;
    const remaining = Number(banner?.pity5_remaining ?? 0);
    if (remaining > 0) {
      parts.push(`唤取【${banner?.title ?? "?"}】距5星保底还差 ${remaining} 抽。`);
    }
  }

  parts.push("想聊什么随时叫我，我在的。");
  return parts.join("");
}

/** 单源失败不影响整条简报（对应 Python 的 safe_fetch → {}）。 */
async function safeFetch(playerId: number, kind: string): Promise<Record<string, unknown>> {
  try {
    return await getJson<Record<string, unknown>>(
      `${PLAYER_BASE}/${playerId}/${kind}`,
      INTERNAL_TIMEOUT_MS,
    );
  } catch {
    return {};
  }
}

/** 拉三个数据源（并行）并组装简报。 */
export async function buildLoginBrief(playerId: number): Promise<string> {
  const [mail, quests, gacha] = await Promise.all([
    safeFetch(playerId, "mail_summary"),
    safeFetch(playerId, "quests"),
    safeFetch(playerId, "gacha_status"),
  ]);
  return renderLoginBrief(mail as BriefMail, quests as BriefQuest, gacha as BriefGacha);
}
