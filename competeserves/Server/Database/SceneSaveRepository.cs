using System.Text.Json;
using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 场景存档行（player_scene_saves）
    /// 一玩家一场景一行，覆盖式写入；位置/相机由客户端采集上报，服务端只做范围护栏
    /// </summary>
    public sealed class SceneSaveRow
    {
        /// <summary>玩家ID（会话绑定，不由客户端提供）</summary>
        public int PlayerId { get; init; }

        /// <summary>场景名（白名单见 <see cref="SceneSaveRepository.IsAllowedScene"/>）</summary>
        public string SceneName { get; init; } = string.Empty;

        public double PosX { get; init; }
        public double PosY { get; init; }
        public double PosZ { get; init; }

        /// <summary>朝向（绕 Y 轴欧拉角，度；写库前归一化到 -180..180）</summary>
        public double RotY { get; init; }

        /// <summary>第三人称相机水平角</summary>
        public double CamYaw { get; init; }

        /// <summary>第三人称相机垂直角</summary>
        public double CamPitch { get; init; }

        /// <summary>第三人称距离（0 = 未初始化，客户端恢复时用场景默认值）</summary>
        public double CamDistance { get; init; }

        /// <summary>退出时是否正在对话中</summary>
        public bool DialogueActive { get; init; }

        /// <summary>对话图资产名</summary>
        public string DialogueGraph { get; init; } = string.Empty;

        /// <summary>当前对话节点 ConfigId（0 = 无）</summary>
        public int DialogueNodeId { get; init; }

        /// <summary>对话变量快照 JSON（前向兼容；事实类变量恢复时由客户端重新播种）</summary>
        public string? DialogueVarsJson { get; init; }

        /// <summary>本段对话已发布过的 eventKey JSON 数组（恢复后抑制重复推进叙事任务）</summary>
        public string? DialogueFiredJson { get; init; }

        /// <summary>存档结构版本</summary>
        public int SaveVersion { get; init; } = SceneSaveRepository.CurrentSaveVersion;

        /// <summary>服务端最近写入时间（Unix 毫秒）</summary>
        public long UpdatedAtMs { get; init; }
    }

    /// <summary>
    /// 场景存档仓库类
    /// 「随时退出、回来还在原处」的服务端账本：进入场景拉取快照，事件点覆盖式上报。
    /// 只做范围护栏（防止脏数据撑爆列/破坏渲染），不承担位置模拟与反作弊职责。
    /// </summary>
    public class SceneSaveRepository
    {
        /// <summary>当前存档结构版本（改对话图/改快照字段时递增，客户端可据此做降级）</summary>
        public const int CurrentSaveVersion = 1;

        /// <summary>允许存档的场景白名单（扩场景只需在此加一行 + 客户端挂 Provider）</summary>
        private static readonly HashSet<string> AllowedScenes = new(StringComparer.Ordinal)
        {
            "dashijie",
        };

        /// <summary>场景名最大长度（与表列一致）</summary>
        public const int MaxSceneNameLength = 64;

        /// <summary>对话图名最大长度（与表列一致）</summary>
        public const int MaxGraphNameLength = 128;

        /// <summary>JSON 列最大长度（超出直接拒绝，避免超长脏数据入库）</summary>
        public const int MaxJsonLength = 8192;

        /// <summary>节点ID上限（护栏，避免任意大整数）</summary>
        public const int MaxNodeId = 1_000_000;

        /// <summary>坐标绝对值上限（米）</summary>
        public const double MaxCoordinate = 1000d;

        /// <summary>
        /// 场景是否在存档白名单内
        /// </summary>
        public static bool IsAllowedScene(string sceneName)
        {
            return !string.IsNullOrEmpty(sceneName) && AllowedScenes.Contains(sceneName);
        }

        /// <summary>
        /// 幂等建表方法
        /// 与 Migrations\015_AddSceneSaves.sql 等价，已有表无副作用
        /// </summary>
        public void EnsureTable()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                @"CREATE TABLE IF NOT EXISTS `player_scene_saves` (
                  `player_id`           INT          NOT NULL COMMENT '玩家ID = accounts.Id',
                  `scene_name`          VARCHAR(64)  NOT NULL COMMENT '场景名（白名单：dashijie）',
                  `pos_x`               DOUBLE       NOT NULL DEFAULT 0,
                  `pos_y`               DOUBLE       NOT NULL DEFAULT 0,
                  `pos_z`               DOUBLE       NOT NULL DEFAULT 0,
                  `rot_y`               DOUBLE       NOT NULL DEFAULT 0,
                  `cam_yaw`             DOUBLE       NOT NULL DEFAULT 0,
                  `cam_pitch`           DOUBLE       NOT NULL DEFAULT 0,
                  `cam_distance`        DOUBLE       NOT NULL DEFAULT 0,
                  `dialogue_active`     TINYINT      NOT NULL DEFAULT 0,
                  `dialogue_graph`      VARCHAR(128) NOT NULL DEFAULT '',
                  `dialogue_node_id`    INT          NOT NULL DEFAULT 0,
                  `dialogue_vars_json`  TEXT         NULL,
                  `dialogue_fired_json` TEXT         NULL,
                  `save_version`        INT          NOT NULL DEFAULT 1,
                  `updated_at_ms`       BIGINT       NOT NULL DEFAULT 0,
                  PRIMARY KEY (`player_id`, `scene_name`),
                  KEY `idx_updated` (`updated_at_ms`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='场景存档表（位置/相机/对话节点）'",
                connection);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// 读取存档方法
        /// 不存在的场景/没有行 → 返回 null（客户端按场景默认开局）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="sceneName">参数名：sceneName，类型：string，用途：场景名</param>
        /// <returns>返回值类型：SceneSaveRow?，用途：存档行（无档为 null）</returns>
        public SceneSaveRow? Get(int playerId, string sceneName)
        {
            if (playerId <= 0 || !IsAllowedScene(sceneName)) return null;

            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                @"SELECT pos_x, pos_y, pos_z, rot_y, cam_yaw, cam_pitch, cam_distance,
                         dialogue_active, dialogue_graph, dialogue_node_id,
                         dialogue_vars_json, dialogue_fired_json, save_version, updated_at_ms
                  FROM player_scene_saves
                  WHERE player_id = @PlayerId AND scene_name = @SceneName",
                connection);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.Parameters.AddWithValue("@SceneName", sceneName);

            using MySqlDataReader reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            return new SceneSaveRow
            {
                PlayerId = playerId,
                SceneName = sceneName,
                PosX = reader.GetDouble(0),
                PosY = reader.GetDouble(1),
                PosZ = reader.GetDouble(2),
                RotY = reader.GetDouble(3),
                CamYaw = reader.GetDouble(4),
                CamPitch = reader.GetDouble(5),
                CamDistance = reader.GetDouble(6),
                DialogueActive = reader.GetInt32(7) != 0,
                DialogueGraph = reader.GetString(8),
                DialogueNodeId = reader.GetInt32(9),
                DialogueVarsJson = reader.IsDBNull(10) ? null : reader.GetString(10),
                DialogueFiredJson = reader.IsDBNull(11) ? null : reader.GetString(11),
                SaveVersion = reader.GetInt32(12),
                UpdatedAtMs = reader.GetInt64(13),
            };
        }

        /// <summary>
        /// 覆盖式写入存档方法
        /// 先校验再 UPSERT；服务端时间戳总是取当前时间（客户端上报的 updated_at_ms 被忽略）
        /// </summary>
        /// <param name="row">参数名：row，类型：SceneSaveRow，用途：待写入快照（PlayerId 必须 > 0）</param>
        /// <returns>返回值类型：(bool Success, string Message, long UpdatedAtMs)，用途：写入结果与服务端时间</returns>
        public (bool Success, string Message, long UpdatedAtMs) Upsert(SceneSaveRow row)
        {
            string? invalid = Validate(row);
            if (invalid != null) return (false, invalid, 0);

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                @"INSERT INTO player_scene_saves
                    (player_id, scene_name, pos_x, pos_y, pos_z, rot_y, cam_yaw, cam_pitch, cam_distance,
                     dialogue_active, dialogue_graph, dialogue_node_id, dialogue_vars_json, dialogue_fired_json,
                     save_version, updated_at_ms)
                  VALUES
                    (@PlayerId, @SceneName, @PosX, @PosY, @PosZ, @RotY, @CamYaw, @CamPitch, @CamDistance,
                     @DialogueActive, @DialogueGraph, @DialogueNodeId, @DialogueVarsJson, @DialogueFiredJson,
                     @SaveVersion, @UpdatedAtMs)
                  ON DUPLICATE KEY UPDATE
                    pos_x = VALUES(pos_x),
                    pos_y = VALUES(pos_y),
                    pos_z = VALUES(pos_z),
                    rot_y = VALUES(rot_y),
                    cam_yaw = VALUES(cam_yaw),
                    cam_pitch = VALUES(cam_pitch),
                    cam_distance = VALUES(cam_distance),
                    dialogue_active = VALUES(dialogue_active),
                    dialogue_graph = VALUES(dialogue_graph),
                    dialogue_node_id = VALUES(dialogue_node_id),
                    dialogue_vars_json = VALUES(dialogue_vars_json),
                    dialogue_fired_json = VALUES(dialogue_fired_json),
                    save_version = VALUES(save_version),
                    updated_at_ms = VALUES(updated_at_ms)",
                connection);
            command.Parameters.AddWithValue("@PlayerId", row.PlayerId);
            command.Parameters.AddWithValue("@SceneName", row.SceneName);
            command.Parameters.AddWithValue("@PosX", row.PosX);
            command.Parameters.AddWithValue("@PosY", row.PosY);
            command.Parameters.AddWithValue("@PosZ", row.PosZ);
            command.Parameters.AddWithValue("@RotY", NormalizeAngle(row.RotY));
            command.Parameters.AddWithValue("@CamYaw", row.CamYaw);
            command.Parameters.AddWithValue("@CamPitch", row.CamPitch);
            command.Parameters.AddWithValue("@CamDistance", row.CamDistance);
            command.Parameters.AddWithValue("@DialogueActive", row.DialogueActive ? 1 : 0);
            command.Parameters.AddWithValue("@DialogueGraph", row.DialogueGraph);
            command.Parameters.AddWithValue("@DialogueNodeId", row.DialogueNodeId);
            command.Parameters.AddWithValue("@DialogueVarsJson", (object?)row.DialogueVarsJson ?? DBNull.Value);
            command.Parameters.AddWithValue("@DialogueFiredJson", (object?)row.DialogueFiredJson ?? DBNull.Value);
            command.Parameters.AddWithValue("@SaveVersion", CurrentSaveVersion);
            command.Parameters.AddWithValue("@UpdatedAtMs", nowMs);
            command.ExecuteNonQuery();

            return (true, "OK", nowMs);
        }

        /// <summary>
        /// 快照校验方法
        /// 返回 null 表示通过；否则返回面向玩家/排障的中文原因（不抛异常，不写库）
        /// </summary>
        /// <param name="row">参数名：row，类型：SceneSaveRow，用途：待校验快照</param>
        /// <returns>返回值类型：string?，用途：校验失败原因（通过为 null）</returns>
        public static string? Validate(SceneSaveRow row)
        {
            if (row.PlayerId <= 0) return "请先登录";
            if (!IsAllowedScene(row.SceneName)) return "不支持的场景";
            if (row.SceneName.Length > MaxSceneNameLength) return "场景名过长";

            if (!IsFinite(row.PosX) || !IsFinite(row.PosY) || !IsFinite(row.PosZ)) return "坐标非法";
            if (Math.Abs(row.PosX) > MaxCoordinate || Math.Abs(row.PosY) > MaxCoordinate || Math.Abs(row.PosZ) > MaxCoordinate)
                return "坐标超出范围";
            if (!IsFinite(row.RotY)) return "朝向非法";

            if (!IsFinite(row.CamYaw)) return "相机水平角非法";
            if (!IsFinite(row.CamPitch) || row.CamPitch < -89d || row.CamPitch > 89d) return "相机垂直角超出范围";
            if (!IsFinite(row.CamDistance)) return "相机距离非法";
            if (row.CamDistance != 0d && (row.CamDistance < 1d || row.CamDistance > 20d)) return "相机距离超出范围";

            if (row.DialogueGraph.Length > MaxGraphNameLength) return "对话图名过长";
            if (row.DialogueNodeId < 0 || row.DialogueNodeId > MaxNodeId) return "对话节点非法";

            if (row.DialogueVarsJson != null && row.DialogueVarsJson.Length > MaxJsonLength) return "对话变量超长";
            if (row.DialogueFiredJson != null && row.DialogueFiredJson.Length > MaxJsonLength) return "对话事件列表超长";

            return null;
        }

        /// <summary>
        /// 清洗 JSON 列方法
        /// 空串一律视为无；非法 JSON 丢弃为 null（位置等核心字段仍然保存，不因附件数据坏掉整包拒绝）
        /// </summary>
        /// <param name="json">参数名：json，类型：string?，用途：客户端上报的 JSON 文本</param>
        /// <returns>返回值类型：string?，用途：可入库的 JSON（不可入库为 null）</returns>
        public static string? SanitizeJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            if (json.Length > MaxJsonLength) return null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                return document.RootElement.GetRawText();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>角度归一化到 -180..180（避免长期累加后写入超大值）</summary>
        private static double NormalizeAngle(double degrees)
        {
            double value = degrees % 360d;
            if (value > 180d) value -= 360d;
            if (value <= -180d) value += 360d;
            return value;
        }

        /// <summary>是否为有限数（排除 NaN / Infinity）</summary>
        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
