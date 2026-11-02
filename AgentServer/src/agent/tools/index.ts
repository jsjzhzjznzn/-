/**
 * 工具清单与白名单分池（对应 tools/__init__.py 的 ToolRegistry 工具表）。
 *
 *  - 主图：全部工具（13 个）；
 *  - 子代理：只读工具（排除写操作 / 偏好 / 派发工具，防递归与越权代执行）。
 */
import type { AgentTool } from "@earendil-works/pi-agent-core";

import { WRITE_TOOLS } from "../../core/config.js";
import { makeGameTools, makeMailClaimTool } from "./game.js";
import { makeKnowledgeTool } from "./knowledge.js";
import { makeStyleTool } from "./style.js";
import { makeSubagentTool } from "./subagent.js";
import type { ToolDeps } from "./shared.js";
import { makeWeatherTool } from "./weather.js";

/** 主图工具集（P6 齐活：游戏数据 9 个 + 天气 + 风格 + 知识库 + 子代理）。 */
export function buildMainTools(deps: ToolDeps): AgentTool<any>[] {
  return [
    ...makeGameTools(deps),
    makeMailClaimTool(deps),
    makeWeatherTool(deps),
    makeKnowledgeTool(deps),
    makeStyleTool(deps),
    makeSubagentTool(deps),
  ];
}

/** 子代理工具池：只读 + 无递归（结构性防护：白名单里没有派发/写/偏好工具）。 */
export function buildSubagentTools(deps: ToolDeps): AgentTool<any>[] {
  return buildMainTools(deps).filter(
    (tool) =>
      !WRITE_TOOLS.has(tool.name) && tool.name !== "dispatch_subagent" && tool.name !== "set_agent_style",
  );
}
