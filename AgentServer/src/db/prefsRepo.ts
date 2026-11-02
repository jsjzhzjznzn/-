/**
 * 玩家偏好（player_prefs）：说话风格，按玩家持久化（Phase 5 ④）。
 * 逐句搬 storage.py 的 SQL。
 */
import { getDb } from "./sqlite.js";

export const STYLE_DEFAULT = "gentle";

/** 读玩家说话风格偏好（无记录=默认温和管家）。 */
export function getStyle(playerId: number, fallback = STYLE_DEFAULT): string {
  const row = getDb().prepare("SELECT style FROM player_prefs WHERE player_id=?").get(playerId) as
    | { style: string }
    | undefined;
  return row?.style ?? fallback;
}

/** 写玩家说话风格偏好（UPSERT）。 */
export function setStyle(playerId: number, style: string): void {
  getDb()
    .prepare(
      "INSERT INTO player_prefs(player_id, style, updated_ms) VALUES(?,?,?) " +
        "ON CONFLICT(player_id) DO UPDATE SET style=excluded.style, updated_ms=excluded.updated_ms",
    )
    .run(playerId, style, Date.now());
}
