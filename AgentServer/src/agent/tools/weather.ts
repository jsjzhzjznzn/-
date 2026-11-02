/**
 * 天气工具（逐字搬 tools/weather.py）：Open-Meteo 免费 API（无 Key）。
 *
 * 流程：城市名（支持中文）→ geocoding 查经纬度 → forecast 查当前天气 → 中文天气描述。
 * 失败降级：城市查不到 → 可读文案；网络失败 → 抛异常（由 defineTool 转
 * `工具 get_weather 执行失败: {reason}`，与 Python 版链路一致）。
 */
import { Type } from "typebox";

import { WEATHER_TIMEOUT_MS } from "../../core/config.js";
import { defineTool, type ToolDeps } from "./shared.js";
import type { AgentTool } from "@earendil-works/pi-agent-core";

const GEO_URL = "https://geocoding-api.open-meteo.com/v1/search";
const FORECAST_URL = "https://api.open-meteo.com/v1/forecast";

/** WMO weather code → 中文（逐字） */
const WMO: Readonly<Record<number, string>> = {
  0: "晴", 1: "大致晴朗", 2: "多云", 3: "阴",
  45: "雾", 48: "雾凇",
  51: "小毛毛雨", 53: "毛毛雨", 55: "大毛毛雨", 56: "冻毛毛雨", 57: "强冻毛毛雨",
  61: "小雨", 63: "中雨", 65: "大雨", 66: "冻雨", 67: "强冻雨",
  71: "小雪", 73: "中雪", 75: "大雪", 77: "雪粒",
  80: "小阵雨", 81: "阵雨", 82: "强阵雨", 85: "小阵雪", 86: "大阵雪",
  95: "雷雨", 96: "雷雨伴冰雹", 99: "强雷雨伴冰雹",
};

const PARAMS = Type.Object({
  city: Type.String({
    description: "城市名，支持中文，例如\"济南\"。",
  }),
});

async function fetchJson(url: string, signal?: AbortSignal): Promise<any> {
  const timeout = AbortSignal.timeout(WEATHER_TIMEOUT_MS);
  const merged = signal ? AbortSignal.any([signal, timeout]) : timeout;
  const response = await fetch(url, { signal: merged });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return await response.json();
}

export function makeWeatherTool(deps: ToolDeps): AgentTool<typeof PARAMS> {
  return defineTool(
    {
      name: "get_weather",
      label: "天气查询",
      description:
        "查询指定城市的当前天气（天气现象/气温/湿度/风速）。city 传城市名，支持中文，例如\"济南\"。",
      parameters: PARAMS,
      run: async (params, _deps, signal) => {
        const city = params.city;
        const geo = await fetchJson(
          `${GEO_URL}?name=${encodeURIComponent(city)}&count=1&language=zh&format=json`,
          signal,
        );
        const loc = (geo?.results ?? [])[0];
        if (!loc) {
          return `没有找到名为「${city}」的城市，可以换个说法再试试（比如省名或更常见的写法）。`;
        }
        const forecast = await fetchJson(
          `${FORECAST_URL}?latitude=${loc.latitude}&longitude=${loc.longitude}` +
            "&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m",
          signal,
        );
        const current = forecast?.current ?? {};
        const desc = WMO[Number(current.weather_code ?? -1)] ?? "未知天气";
        return (
          `${loc.name ?? city}当前天气：${desc}，气温 ${current.temperature_2m ?? "?"}°C，` +
          `湿度 ${current.relative_humidity_2m ?? "?"}%，风速 ${current.wind_speed_10m ?? "?"} km/h。` +
          `（数据源 Open-Meteo，实时查询）`
        );
      },
    },
    deps,
  );
}
