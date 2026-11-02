/**
 * 说话风格切换工具（逐字搬 tools/style_tools.py）：set_agent_style。
 *
 * 这是 Agent 内部偏好（不是游戏数据）——玩家说「切换到毒舌风格」即可，
 * LLM 调本工具写 player_prefs，下一次 provider 请求（含本轮后续）立即用新风格
 * （prepareRequest 每轮现读风格并清缓存）。
 */
import type { AgentTool } from "@earendil-works/pi-agent-core";
import { Type } from "typebox";

import { cacheClear } from "../../db/qaCacheRepo.js";
import { setStyle } from "../../db/prefsRepo.js";
import { STYLES } from "../persona.js";
import { defineTool, type ToolDeps } from "./shared.js";

const STYLE_OPTIONS = Object.entries(STYLES)
  .map(([key, [name]]) => `${key}（${name}）`)
  .join("、");

const PARAMS = Type.Object({
  style: Type.String({ description: `风格键或中文名，可选：${STYLE_OPTIONS}` }),
});

export function makeStyleTool(deps: ToolDeps): AgentTool<typeof PARAMS> {
  return defineTool(
    {
      name: "set_agent_style",
      label: "切换风格",
      description: `切换守岸人的说话风格并持久保存。style 只能从这些里选：${STYLE_OPTIONS}。`,
      parameters: PARAMS,
      run: async (params) => {
        const raw = (params.style ?? "").trim().toLowerCase();
        const byName: Record<string, string> = {};
        for (const [key, [name]] of Object.entries(STYLES)) byName[name] = key;
        const key = byName[raw] ?? raw; // 中文名也能说（"毒舌损友" → sharp）

        if (!(key in STYLES)) {
          return `无效风格「${params.style}」，可以从这些里选：${STYLE_OPTIONS}。`;
        }
        setStyle(deps.playerId, key);
        cacheClear(deps.playerId); // 旧风格的缓存回复不再命中，换风格就换语气
        return `风格已切换为「${STYLES[key]![0]}」，从现在起就用这个语气说话。`;
      },
    },
    deps,
  );
}
