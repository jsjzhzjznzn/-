# 装备系统与角色展示 UI 设计方案

- 状态：P0–P3 已实现（2026-10-27），P4 扩展未开始
- 范围：武器装备（每角色 1 槽）+ 原神式角色展示/换装界面 + 战斗数值接入
- 相关文档：`Docs/Documents/Compete战斗系统与UI框架移植方案.md`、`Docs/Documents/成就系统设计方案.md`、`Assets/Documents/Project_Engineering_Audit_Report.md`

---

## 0. 结论摘要

本期做五件事：

1. **装备 = 独立实例**：配置（`equipment_config`）与实例（`equipment_instance`）分离，实例持有唯一 ID、强化等级与词条字段；背包格通过 `instance_id` 引用实例。
2. **穿戴关系单独成表**：`player_equipment(player_id, char_id, slot_type)` 是"谁穿了什么"的唯一真相；每角色 1 个武器槽，表结构带 `slot_type` 以备后续扩展。
3. **服务端权威**：穿脱、校验、归属、槽位冲突全部在 competeserves 单事务内完成；客户端只发请求与展示结果。
4. **属性走现有战斗账本**：装备加成以 `AttributeComponent` 的 Modifier 形式进入，来源可逐件追溯、卸下只撤该来源；不引入第二套属性系统。
5. **原神式角色页 + 主页入口**：入口是主页右上圆钮一排的最右一个（与好友/邮件等并列）；页面为全屏深色，左列角色头像、中央 3D 模型、右侧属性面板与武器卡，点武器卡进入武器选择二级页。本期只做「属性 + 武器」两块。

本期不做（字段/号段先留好）：

- 圣遗物与多槽位、装备强化、随机词条、武器外观挂点与武器技能。
- 装备生命值（需要新增 `AttrType.MaxHP` 并改造 `HealthModel`）。

---

## 1. 现状分析（代码事实）

| 模块 | 现状 | 对装备系统的约束 |
| --- | --- | --- |
| 物品配置 | `item_config(item_id, name, item_type, rarity, icon_key, description, max_stack)`；`item_type` 1 消耗品 / 2 材料 / 3 武器 | 装备专属字段（槽位、武器类型、基础属性）应另建表，不污染通用物品表 |
| 背包存储 | `player_items(player_id, slot_index, item_id, count)`，容量 `BagCapacity=500`，只存非空槽 | 装备需要 `instance_id` 列；装备行 `count` 恒为 1 |
| 现有武器 | 1012–1018 共 7 件，`max_stack=1`，覆盖长刃/迅刀/佩枪/臂铠/音感仪五类，稀有度 3/4/5 | 已有图标与名称，可直接作为首批装备配置种子 |
| 入包入口 | `BagRepository.Grant` 入包，满包转邮件；抽卡、任务奖励、签到、邮件附件在各自事务内调用同一份 `BagRepository.GrantIntoSlots` | 装备实例化收敛在 `GrantIntoSlots` 一处，四条入包路径同时生效，不需要各写一份 |
| 背包协议 | `BagSlotInfo{slot_index, item_id, count}`、`ItemConfigInfo{...item_type...}` | 追加 `int64 instance_id`（新增字段，旧客户端可忽略） |
| 客户端背包 | `BagManager` 单例快照 + `configs` 字典；`ItemIconLoader` 按 `icon_key` 取 Sprite；`BagUIController` 500 格虚拟列表 | 装备页复用同一套图标与稀有度样式 |
| 角色 | `PlayerSO.statsData{baseAttack, baseDefense, baseMaxHP}`；安比 `CharId=0`、艾莲 `CharId=1`（`BattleHeroCatalog`） | 装备按 `char_id` 归属；`PlayerSO` 增加 `characterId` 供单机场景识别 |
| 属性 | `AttributeComponent`（`AttrType`：DamageUp / DamageDown / MoveSpeed / Attack / Defense）；`MutableAttribute` = Base + Modifier 账本；`AddModifier` / `RemoveAllFromSource(object)` | 装备加成分两类：白值 Attack/Defense 用 `ModType.Add`；系数型同理 |
| 属性权威 | 仅服务端/NGO 房主写 Modifier；`Attack`、`DamageUp` 镜像给拥有者客户端 | 装备加成必须由房主/服务端应用，客户端不写账本 |
| 战斗 | NGO 1.13，房主即服务端；`BattleSpawnManager` 按 `CharId` 生成英雄；挑战场景单机 | 装备快照需要能同时喂给单机与联机 |
| UI 框架 | `UIManager` + `Assets/Game/UI/UIConfig.json` + `UIType`（当前 `Max=25`）；`UIView` + `[ControlBinding]` 按字段名绑定 | 新界面按现有注册流程接入 |
| 3D 预览 | `UIModelManager.LoadModelToRawImage(path, RawImage, canDrag, offset, rot, scale, isOrth, orthSizeOrFOV, cameraDistance)`；预览预制体 `Assets/Game/Resources/Battle/Heroes/{Anbi,Ellen}Preview.prefab` | 角色展示页直接复用，无需新相机/新渲染管线 |
| 服务端 | competeserves 8888；迁移目录 `Migrations/`，启动 `EnsureTable` + `Seed*` | 新表随启动幂等建表、幂等种子 |

结论：现有结构支持"装备独立实例 + 槽位表 + 属性来源账本"，改动集中在背包一处扩展（`instance_id`）与服务端新增一个装备域。

---

## 2. 领域模型

### 2.1 三个对象

| 对象 | 表 | 职责 | 例子 |
| --- | --- | --- | --- |
| 装备配置 | `equipment_config` | 描述"这一类装备"：槽位、武器类型、基础属性、可用角色、预览模型键 | `item_id=1014` 佩枪·xxx，攻击 +32 |
| 装备实例 | `equipment_instance` | 玩家真正拥有的那一件：唯一 ID、强化等级、词条、获得时间 | `instance_id=88012`，1014，强化 +2 |
| 穿戴关系 | `player_equipment` | 角色 × 槽位 → 实例 | 安比(0) 的 Weapon 槽 = 88012 |

背包仍由 `player_items` 管理持有：装备行 `count=1` 且带 `instance_id`。**背包管"有哪些"，槽位表管"穿在哪"，实例表管"这一件是什么"**，三者职责不重叠。

```
item_config (展示/通用)          equipment_config (装备专属)
      │ 1:1 by item_id                 │
      └──────────────┬─────────────────┘
                     │
        equipment_instance (instance_id, enhance_level, affix_json)
                     │ 1:N
      ┌──────────────┴───────────────┐
player_items(instance_id)     player_equipment(char_id, slot_type → instance_id)
   （在背包里/仓库中）                （正穿在角色身上）
```

### 2.2 关键取舍

| 决策点 | 方案 A（采用） | 方案 B（放弃） | 理由 |
| --- | --- | --- | --- |
| 装备与物品 | 新增 `equipment_config` + `equipment_instance` | 在 `item_config` 加装备列、同 `item_id` 视为同一件 | 同名两把武器强化/词条不同是必然需求；物品表保持通用 |
| 是否进背包 | 装备进背包，`player_items.instance_id` 引用 | 装备放独立仓库、不进背包 | 现有抽卡/掉落/邮件全部走背包 Grant，独立仓库要改四条链路 |
| 穿戴状态存哪 | 只存 `player_equipment` | 同时在实例行记 `equipped_char_id` | 单一真相，避免双写不一致；"是否已装备"用 join 派生 |
| 槽位数量 | 本期 1 个武器槽，表按 `slot_type` 设计 | 直接上 6 槽 | 先跑通"配置/实例/槽位"三层；扩槽只加配置与 UI 区块 |
| 属性系统 | 进 `AttributeComponent` | 接 EX-GAS | 战斗伤害结算只读 `AttributeComponent`；GAS 当前不参与战斗 |

---

## 3. 数据模型

### 3.1 表结构（DDL 草案）

```sql
-- 装备配置：与 item_config 同主键，一对一扩展
CREATE TABLE IF NOT EXISTS `equipment_config` (
  `item_id`       INT          NOT NULL,
  `slot_type`     INT          NOT NULL DEFAULT 1,   -- 1=武器（预留 2=护甲 3=饰品）
  `weapon_type`   INT          NOT NULL DEFAULT 0,   -- 1长刃 2迅刀 3佩枪 4臂铠 5音感仪
  `allowed_chars` VARCHAR(64)  NOT NULL DEFAULT '',  -- 空=全部；否则 "0,1"
  `attr_json`     TEXT         NOT NULL,             -- [{"type":3,"value":32},{"type":0,"value":0.05}]
  `model_key`     VARCHAR(128) NOT NULL DEFAULT '',  -- 将来武器外观模型键，本期空
  PRIMARY KEY (`item_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 装备实例：玩家真正拥有的那一件
CREATE TABLE IF NOT EXISTS `equipment_instance` (
  `instance_id`   BIGINT       NOT NULL AUTO_INCREMENT,
  `player_id`     INT          NOT NULL,
  `item_id`       INT          NOT NULL,
  `enhance_level` INT          NOT NULL DEFAULT 0,
  `affix_json`    TEXT         NOT NULL,             -- 本期 "[]"，字段先行
  `created_at`    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`instance_id`),
  KEY `idx_player` (`player_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 穿戴关系：唯一真相
CREATE TABLE IF NOT EXISTS `player_equipment` (
  `player_id`   INT      NOT NULL,
  `char_id`     INT      NOT NULL,
  `slot_type`   INT      NOT NULL,
  `instance_id` BIGINT   NOT NULL,
  `updated_at`  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`player_id`, `char_id`, `slot_type`),
  KEY `idx_instance` (`instance_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 背包扩展：装备行引用实例
ALTER TABLE `player_items`
  ADD COLUMN `instance_id` BIGINT NULL DEFAULT NULL,
  ADD KEY `idx_instance` (`instance_id`);
```

约定：

- `attr_json.type` 是 `AttrType` 的整数值（当前枚举顺序：0 DamageUp / 1 DamageDown / 2 MoveSpeed / 3 Attack / 4 Defense）。**枚举只允许末尾追加**，配置里的数字才长期有效。
- `equipment_config.allowed_chars` 为空表示所有角色可用；否则是逗号分隔的 `char_id` 白名单。
- `player_equipment` 用 `(player_id, char_id, slot_type)` 主键天然阻止"同角色同槽穿两件"。
- 不建"角色拥有表"：当前 `CharId` 由 `BattleHeroCatalog` 静态定义（0/1），所有账号可用；将来做角色拥有时再加 `player_characters`，装备表不受影响。

### 3.2 迁移与兼容

1. `ALTER player_items ADD instance_id` 用 `information_schema` 判断后再执行（现有 `EnsureTable` 的幂等补列写法）。
2. 历史武器行回填实例：对 `player_items` 中 `item_type=3` 且 `instance_id IS NULL` 的行，逐行建 `equipment_instance` 并回写 `instance_id`。回填在 `Program.cs` 启动时执行一次，重复执行无副作用。
3. `BagSlotInfo` 追加 `int64 instance_id = 4`：非装备行为 0。
4. `GrantIntoSlots` 分支：`item_type=3` 时逐件新建设备实例、写 `instance_id`、`count=1`（实例行与背包行同事务）；装备不堆叠，同 `item_id` 多件在背包占多格。唤取、任务奖励、签到、邮件附件领取共用这一份实现。
5. 满包转邮件、邮件附件领取、抽卡、副本掉落全部经 `Grant`，装备实例化只有这一处实现。

### 3.3 首批配置种子（数值待策划确认）

| item_id | 名称 | 类型 | 稀有度 | 建议基础属性 |
| --- | --- | --- | --- | --- |
| 1012 | 长刃·初鸣 | 长刃 | 3 | Attack +24，DamageUp +0.04 |
| 1013 | 迅刀·疾风 | 迅刀 | 3 | Attack +22，MoveSpeed +0.03 |
| 1014 | 佩枪·惊雷 | 佩枪 | 4 | Attack +32，DamageUp +0.06 |
| 1015 | 臂铠·磐岩 | 臂铠 | 4 | Attack +30，DamageDown +0.05 |
| 1016 | 音感仪·潮声 | 音感仪 | 5 | Attack +44，DamageUp +0.10 |
| 1017 | 长刃·沧鳞 | 长刃 | 5 | Attack +48，DamageUp +0.08 |
| 1018 | 迅刀·夜行 | 迅刀 | 5 | Attack +46，MoveSpeed +0.06 |

- 全部武器本期 `allowed_chars=''`（两角色通用）；武器类型与角色不绑定（安比/艾莲都可用任意类型）。
- 数值只用于验证"装备确实影响伤害"，最终由策划定表。

---

## 4. 服务端设计

### 4.1 模块划分

| 文件 | 职责 |
| --- | --- |
| `Server/Migrations/0xx_AddEquipment.sql` | 建四张表/加列（同时由 `EnsureTable` 兜底） |
| `Server/Database/EquipmentRepository.cs` | 建表、种子、快照查询、穿戴/卸下（单事务）、战斗快照构建 |
| `Server/Handler/EquipmentHandler.cs` | 3501–3504 处理器，`MessageHandler` 路由 |
| `Server/Database/BagRepository.cs` | `Grant` 建实例；读写 `instance_id`；整理/换位保持实例引用 |
| `Server/Program.cs` | `EnsureTable` + `SeedEquipmentConfigs` + 历史武器回填 |

### 4.2 穿戴流程（防半成功）

```
EquipRequest(char_id, slot_type, instance_id)
  1. 会话身份校验（player_id 来自会话）
  2. 实例校验：存在、属于本人、item 在 equipment_config 中、slot_type 匹配
  3. 角色校验：char_id 在 allowed_chars（空=全部）
  4. 冲突校验：该实例是否已穿在其它角色/其它槽 → 转移策略（见 4.3）
  5. 单事务提交：SELECT ... FOR UPDATE（实例行 + 目标槽行）→ UPSERT 新槽 → 需要时清旧槽
  6. 回执：最新 slot 列表 + 变更实例 + 结果码
```

- **全量校验在写之前完成**：任何一条不通过直接返回失败码，不产生"主手换了副手没换"的中间态。
- 幂等：同一角色同槽重复穿同一实例 → 返回 `code=0`、`unchanged=true`，不改数据。
- 卸下：`instance_id=0` 表示清空该槽；槽本为空时同样返回成功。
- 失败码：`0 成功 / 1 未登录 / 2 实例不存在或不属于你 / 3 不是装备 / 4 槽位类型不匹配 / 5 角色不可用 / 6 存储错误`。

### 4.3 同一实例的跨角色冲突

原神式交互：给艾莲装备安比正在用的武器时，客户端弹确认"该武器正被安比装备，是否卸下并装备？"。

- 服务端只做原子转移：同一事务内删旧槽、写新槽；回执 `note` 说明"已从安比卸下"。
- 客户端负责二次确认，服务端不依赖客户端确认（即使直接请求也只会原子转移，不会出现两个角色同时持有）。
- 两个角色同槽的并发请求由主键 + `FOR UPDATE` 串行化。

### 4.4 查询与快照

- `3501` 一次性返回：装备配置表、该玩家全部实例、全部槽位。数量级为几十条，无需分页。
- `EquipmentRepository.BuildBattleSnapshot(playerId, charId)`：只返回该角色当前武器的 `{instance_id, item_id, enhance_level, attr[]}`，供战斗应用。
- 装备不参与排行榜/成就结算，不新增事件推送。

---

## 5. 属性接入

### 5.1 加成来源模型

`MutableAttribute` 的 Modifier 带 `source` 引用，按引用相等删除。每件装备一个来源对象：

```csharp
// 每件装备一个来源对象，装备/卸下/重建都靠它精准增删
sealed class EquipmentAttrSource
{
    public long InstanceId;
}

// 应用
foreach (var (type, value) in item.Attributes)
    attr.Get(type).AddModifier(new AttributeModifier(ModType.Add, value, source));

// 卸下 / 换装 / 角色重置
attr.Get(type).RemoveAllFromSource(source);
```

- 一次全量重建：先移除本次快照记录的所有 source，再逐件添加。这样重复应用不会叠加。
- 禁止按数值删除（两个来源都 +30 时会误删）。

### 5.2 应用时机与权威

| 场景 | 应用者 | 数据来源 |
| --- | --- | --- |
| 挑战场景（单机） | `Player` 自身（`ApplyStatsFromSO` 之后） | `EquipmentManager` 本地缓存（3501） |
| 联机对战 | NGO 房主在 `BattleSpawnManager.SpawnHeroFor` 后应用 | 房主收到的该座位玩家装备快照 |
| 客机 | 不写账本 | `AttributeComponent` 只在服务端写 |

- 快照传递：`RoomState` 现有自定义消息通道新增 `MsgEquipSnapshot`，各客户端准备阶段把所选角色的快照发给房主；房主存 `BattlePlayerInfo.EquipJson`，生成英雄时交给 `Player`。
- `Player` 增加"装备应用器"：`ApplyEquipmentSnapshot(snapshot)` / `ClearEquipmentSnapshot()`；离场与复活重置时清空来源。
- 角色识别：`PlayerSO` 增加 `int characterId`（默认 -1）。单机取 SO 值，联机以 `RoomState` 的 `CharId` 为准；两者不一致时以网络值为准并打警告日志。

### 5.3 属性白名单

本期支持（均已在 `AttrType` 中）：

| 类型 | 值 | 计算方式 |
| --- | --- | --- |
| Attack | 白值 | `(base + Σadd) × Πmul` |
| Defense | 白值 | 同上 |
| DamageUp | 系数 | `(1 + Σadd) × Πmul - 1` |
| DamageDown | 系数 | 同上 |
| MoveSpeed | 系数 | 同上（移动状态机是否读取见二期） |

`MaxHP` 不在 `AttrType` 中：装备生命词条需要新增 `AttrType.MaxHP` 并让 `HealthModel` 读最终值，本期不做。

### 5.4 与 EX-GAS 的边界

EX-GAS 已集成但不参与战斗结算。装备加成只进 `AttributeComponent`，**不向 GAS 写属性**，避免两套账本同时改同一数值。将来若要复用 GAS 的 GE/技能体系，装备只需作为"GE 来源"再翻译一次，实例与槽位模型不变。

---

## 6. 客户端设计

### 6.1 数据层

`Assets/Game/Scripts/Equipment/EquipmentManager.cs`（Bootstrap 单例，DDOL，照 `BagManager` 先例）：

| API | 说明 |
| --- | --- |
| `EnsureLoaded()` | 打开界面前拉 3501；已加载直接返回 |
| `Configs` / `Instances` / `Slots` | 只读快照（`Dictionary` + `List`） |
| `GetEquipped(int charId, int slotType)` | 取槽位实例（无则 null） |
| `GetWeaponInstances()` | 背包/仓库中所有武器实例（列表数据源，稀有度降序 → 攻击降序 → 实例ID升序） |
| `Equip(int charId, int slotType, long instanceId)` | 发送请求；成功后应用回执并触发事件 |
| `Unequip(int charId, int slotType)` | 等价 `Equip(..., 0)` |
| `GetEquipAttrBonus(int charId, long candidateInstanceId)` | 装备加成合计（UI 把角色基础白值加上它再显示；候选实例用于换装预览） |
| `BuildBattleSnapshot(int charId)` | 战斗用快照（本地缓存生成） |
| `event Changed` | 槽位/实例变化（UI 刷新） |
| `event EquipResult(bool, string)` | 穿戴结算（角色页据此在成功后收回二级页） |

- 登录成功清缓存（防串号）；断线保留缓存，重连后重拉；`Equip` 单在途，回执超时 5s 提示可重试。
- 装备变更成功后静默 `BagManager.RequestBag()`，让背包列表的"已装备"标注同步。

### 6.2 3D 预览

- 角色：`BattleHeroCatalog.Get(charId)` 提供预览路径与取景参数；预制体在 `Assets/Game/Resources/Battle/Heroes` 下，用 `Resources.Load` + `Instantiate` 后交给 `UIModelManager.LoadModelToRawImage(GameObject, RawImage, canDrag: true, offset: new Vector3(0, hero.PreviewOffsetY, 0), isOrth: true, orthSizeOrFOV: hero.PreviewOrthSize)`（与对战大厅 `BattleRoomView` 同一路径：预制体引用的 FBX 依赖不在 YooAsset 收集范围内）。
- 卸载必须先销毁自己 Instantiate 的实例，再 `UnLoadModelByRawImage(rawImage, recycleGo: false)`；关闭界面时执行，避免下次打开叠影。
- 切换角色先卸载再加载，避免模型叠影。
- 武器**本期不挂到模型上**：现有武器只有 80×80 / 竖版图标，没有 mesh 与挂点；槽位只显示图标。P4 再做 socket 挂接与外观替换。

### 6.3 界面结构（原神式，全屏深色）

参考分辨率 1920×1080。打开后是一层**不透明深色全屏页**（主页在下层、被完全遮住），不做侧栏、不保留主页可见。

| 区域 | 位置 / 尺寸 | 内容 |
| --- | --- | --- |
| 背景 | 全屏 | `#12100E`，alpha 0.96；径向暗角 + 顶部 1px 金色分隔线 |
| 顶部标题 | 左上 (48, 96) | 「角色」34px；右上 `closeButton` 48×48 |
| 左侧角色列 | x 48–168，y 220–560 | 竖排头像 96×96、间距 16（安比/艾莲）；选中=金色 2px 外框 + 内发光；头像下方 18px 角色名 |
| 中央模型区 | x 200–1150，y 120–980 | `RawImage` + `UIRenderToTexture`，模型脚底对齐地面线 y≈900；可拖动旋转；下缘居中显示角色名 + 武器类型 |
| 右侧属性面板 | x 1200–1872，y 96–430 | 六行：攻击力 / 防御力 / 伤害加成 / 受伤减免 / 移动速度 / 生命值上限 |
| 右侧武器卡 | x 1200–1872，y 454–620 | 卡片 672×166：图标 96、名称、稀有度星、强化 Lv、属性摘要；卡下两个按钮「更换」「卸下」 |
| 底部提示 | 左下 (48, -32) | 「拖动模型可旋转视角」 |

属性行显示规则：

- 白值（攻击/防御）：`基础 100` + `装备 +48` = `合计 148`，装备部分用绿色 `#7BD88F`。
- 系数（伤害加成/受伤减免/移动速度）：以百分比显示（`+6.0%`），基础恒 0%。
- 生命值上限：只显示基础值；装备不加血（见 5.3）。

武器选择二级页（覆盖在角色页之上，同一预制体内切换，不新增 `UIType`）：

| 区域 | 位置 / 尺寸 | 内容 |
| --- | --- | --- |
| 标题栏 | 左上 (200, 96) | 「选择武器」+ 返回按钮（返回角色页） |
| 左侧列表 | x 200–980，y 160–940 | `VirtualScrollList`，行高 96；行=图标 72 + 名称 + 稀有度 + `已装备：安比` |
| 右侧详情 | x 1020–1872，y 160–940 | 大图标 200×200、名称、稀有度、强化、属性块、`对比当前：↑攻击 +12 ↓移速 -2%`、`equipButton` 320×72 |
| 空态 | 列表区居中 | 「暂无武器，可通过唤取或副本获取」 |

列表排序：稀有度降序 → 攻击力降序 → `instance_id` 升序（稳定）。本期不做筛选与搜索。

交互流程：

1. 打开：`EquipmentManager.EnsureLoaded()` → 拉 3501（首次）→ 取上次查看角色（`PlayerPrefs: equipment_last_char`，默认 0）→ 加载预览 → 填属性与武器卡。
2. 切角色：点左侧头像 → 卸载旧模型 → 加载新模型（交叉淡入 0.15s）→ 刷新属性、武器卡、列表"已装备"标注；写回 `equipment_last_char`。
3. 拖动模型：`canDrag=true` 水平旋转，不做缩放（避免与滚轮列表冲突）。
4. 点武器卡或「更换」：进入二级页，列表只显示 `item_type=3` 的实例（当前这件也在列表里，标注"已装备：当前角色"）。
5. 选中列表行：右侧详情显示属性；`CompareText` 与当前武器逐项对比（↑绿 / ↓红 / 持平灰）。
6. 点「装备」：若该实例已穿在另一个角色身上 → 弹二次确认「该武器正被 XX 装备，是否卸下并装备？」→ 确认后发 3503 → 回执刷新槽位、属性数字 0.25s 滚动、列表标注 → `BagManager.RequestBag()` → 返回角色页。
7. 点「卸下」：`Equip(instanceId=0)` → 武器卡回空态「未装备武器」。
8. 关闭：`closeButton` 或 Esc → `UIManager.Close(UIType.CharacterView)`；二级页时 Esc 先回角色页。

动效（DOTween，默认 scaled；本页不暂停世界）：

- 打开：背景 alpha 0→0.96（0.18s）+ 右侧面板 x +40→0（0.22s，OutCubic）+ 模型 alpha 0→1（0.25s）。
- 切角色：旧模型 alpha→0 与 新模型 alpha→1 交叉（0.15s）。
- 属性数字：当前值→新值滚动（0.25s），装备部分绿色。
- 按钮：按下缩放到 0.94，抬起回弹。

视觉规格：

| 项 | 值 |
| --- | --- |
| 面板底 | `#1E1B17`，alpha 0.92；1px 描边 `rgba(217,178,92,0.35)` |
| 主文字 / 次文字 | `#F2EAD8` / `#A79B86` |
| 强调金 | `#D9B25C`（与主页 `GoldFrame` 一致）、深金 `#7E5B18` |
| 加成 / 降低 | `#7BD88F` / `#E06C6C` |
| 字体 | NotoSansSC SDF（项目全局回退） |
| 圆角 / 底图 | `Assets/Game/Resources/ui/Theme/panel_round.png`（九宫格）、`circle.png`、`btn_round.png`（均近白，用 tint 上色） |
| 图标 | 武器走 `ItemIconLoader`（404×560 竖版图标需 `preserveAspect` + 方形底框）；角色头像用 `Assets/Game/UI/Battle/Portraits` 现有图裁剪，缺图时回退单字色块 |

参考（2026-10-26 检索）：原神角色页为「左侧角色列表 + 中央 3D 模型 + 右侧分栏（属性/武器/圣遗物/命之座/天赋）」，武器与圣遗物以卡片 + 槽位呈现；点击武器卡进入武器背包选择页，选中后展示与当前武器的属性对比，底部替换，已被其他角色装备时提示。本期按自有数据面裁剪为「属性 + 武器」两块（来源：Genshin Impact Wiki Character/Menu、HoYoLAB 武器与圣遗物指南、站酷《浅谈原神美术风格与UI视觉包装》）。

### 6.4 主页入口（定稿）

入口加在**主页右上圆钮一排的最右一个**（与好友/邮件/任务/成就/排行榜/世界/阿福/背包并列）。

| 项 | 值 |
| --- | --- |
| 按钮名 / 绑定字段 | `characterButton` |
| 尺寸 / 间距 | 84×84，间距 10（沿用 `SocialSize` / `SocialGap`） |
| 位置 | 该排 index 0（最右），中心 x = -82，中心 y = -104 |
| 连锁位移 | 现有 8 个按钮整体左移 94px；9 个总宽 930，最左中心 x = -834，不越界 |
| 与设置键 | 设置键 `(-24,-24) 104×44`（底边 y=-68）与本排中心 y=-104 不重叠 |
| 图标 | `Assets/Game/UI/Home/Art/Icons/icon_character.png`（128 或 256 导入，风格同 `icon_bag`）；缺失时回退单字「色」 |

实现步骤（`HomeViewSkinBuilder` 已按字段名找按钮，新按钮需要构造与绑定）：

1. `HomeViewSkinBuilder.SocialEntries` 首位插入：
   `new Entry { field = "characterButton", title = "角色", glyph = "色", icon = "icon_character" }`。
2. 排位公式保持 `centerX = -(SocialRight + (Count-1-index)*(SocialSize+SocialGap) + SocialSize*0.5)`；Count 由 8 变 9 自动重排，其余按钮位置无需手调。
3. Builder 增加 `EnsureSocialButton`：`entryBar.Find(field) == null` 时创建 `GameObject(name, RectTransform, Image, Button)`，并入 `EntryBar`；随后调用现有 `EnsureCtrlBinding(contents, "characterButton", button)` 写 `UIControlData` 绑定（与 `mainCtaButton` 同款做法）。
4. `HomeView.cs` 增加 `[ControlBinding] public Button characterButton;`；`OnAddListener` / `OnRemoveListener` 成对注册 `OpenCharacter`；方法体 `UIManager.Instance.Open(UIType.CharacterView).Forget();`。
5. 打开角色页前先 `await UIManager.Instance.Close(UIType.BagView)`（互斥）；关闭后回主页，入口行位置不变。

### 6.5 框架实现要点（SkierFramework）

- `UIType.CharacterView = 25`，`Max = 26`；`UIConfig.json` 追加条目：`path = Assets/Game/UI/Character/CharacterView.prefab`、`isWindow = false`、`uiLayer = NormalLayer`、`isLuaUI = false`。
- `Assets/Game/Scripts/Equipment/CharacterView.cs : UIView`，类名与 `uiType` 同名；所有控件用 `[ControlBinding]` 字段，由 `UIControlData` 按名绑定（绑定表由 builder 写入）。
- 列表统一用 `VirtualScrollList`（固定行高 96），**不要**用 `GridLayoutGroup` + `ContentSizeFitter`（与手动摆位冲突）。
- 二级页用同一预制体内的 `PickerRoot` 显隐切换，不注册第二个 `UIType`，避免返回栈与互斥逻辑复杂化。
- 3D 预览：`UIModelManager.LoadModelToRawImage("Battle/Heroes/AnbiPreview", previewRaw, canDrag: true, offset: new Vector3(0, hero.PreviewOffsetY, 0), isOrth: true, orthSizeOrFOV: hero.PreviewOrthSize)`；取景参数来自 `BattleHeroCatalog`。切角色先 `UnLoadModelByRawImage` 再加载。
- 数据刷新：订阅 `EquipmentManager.Changed`；装备成功后同时触发 `BagManager.RequestBag()`，让背包页的"已装备"标注一致。
- UI 层级与相机：走框架 `NormalLayer` + `SkierUICamera`，不新建相机、不改 `UICameraService`。
- 预制体由 `Assets/Game/Scripts/Equipment/Editor/CharacterViewBuilder.cs` 生成，菜单 `Tools/Equipment/重建角色界面`，可重复执行且幂等；资源命名唯一（ui 包 `AddressByFileName`）：`CharacterView.prefab`、`CharacterWeaponRow.prefab` 等不得重名。

---

## 7. 协议设计（35xx）

新增 `protobuf/protos/EquipmentPacket.proto`（`package common.network`），`MessageID` 追加：

| 值 | 消息 | 方向 | 用途 |
| --- | --- | --- | --- |
| 3501 | `EquipmentListRequest` | C→S | 拉装备配置、实例、槽位（空体） |
| 3502 | `EquipmentListResponse` | S→C | 返回 `configs` / `instances` / `slots` |
| 3503 | `EquipRequest` | C→S | 穿戴或卸下（`instance_id=0` 为卸下） |
| 3504 | `EquipResponse` | S→C | 结果码 + 最新槽位与实例 |

消息体草案：

```proto
message EquipConfigInfo {
  int32  item_id        = 1;
  int32  slot_type      = 2;   // 1=武器
  int32  weapon_type    = 3;   // 1长刃 2迅刀 3佩枪 4臂铠 5音感仪
  string allowed_chars  = 4;   // 空=全部；"0,1"
  string attr_json      = 5;   // [{"type":3,"value":32}]
  string model_key      = 6;
}

message EquipInstanceInfo {
  int64  instance_id    = 1;
  int32  item_id        = 2;
  int32  enhance_level  = 3;
  string affix_json     = 4;
  int32  equipped_char  = 5;   // -1=未装备
  int32  equipped_slot  = 6;   // 0=未装备
}

message EquipSlotInfo {
  int32 char_id     = 1;
  int32 slot_type   = 2;
  int64 instance_id = 3;
}

message EquipmentListRequest {}
message EquipmentListResponse {
  bool   success  = 1;
  string message  = 2;
  repeated EquipConfigInfo   configs   = 3;
  repeated EquipInstanceInfo instances = 4;
  repeated EquipSlotInfo     slots     = 5;
}

message EquipRequest {
  int32 char_id     = 1;
  int32 slot_type   = 2;
  int64 instance_id = 3;   // 0=卸下
}

message EquipResponse {
  bool   success  = 1;
  string message  = 2;
  int32  code     = 3;     // 见 4.2 失败码
  bool   unchanged = 4;    // 幂等命中
  repeated EquipSlotInfo     slots     = 5;
  repeated EquipInstanceInfo instances = 6;
}
```

- `BagPacket.proto` 的 `BagSlotInfo` 追加 `int64 instance_id = 4;`（新增字段，历史消息兼容）。
- `build_proto.bat` 生成三端；Unity 生成目录为 `Assets/Game/Scripts/Generated`。
- 预留：`3505/3506` 强化、`3507/3508` 词条重铸；本期不定义消息体，避免占用号段后返工。

---

## 8. 文件影响面

| 侧 | 文件 | 改动 |
| --- | --- | --- |
| 协议 | `protobuf/protos/EquipmentPacket.proto` | 新增 |
| 协议 | `protobuf/protos/MessageID.proto` | 追加 3501–3504 |
| 协议 | `protobuf/protos/BagPacket.proto` | `BagSlotInfo` 追加 `instance_id` |
| 服务端 | `Server/Migrations/0xx_AddEquipment.sql` | 新增（建表 + 加列 + 回填说明） |
| 服务端 | `Server/Database/EquipmentRepository.cs` | 新增 |
| 服务端 | `Server/Handler/EquipmentHandler.cs` | 新增 |
| 服务端 | `Server/Handler/MessageHandler.cs` | 路由 3501/3503 |
| 服务端 | `Server/Database/BagRepository.cs` | `Grant` 建实例、读取/写入 `instance_id`、整理换位保引用 |
| 服务端 | `Server/Program.cs` | `EnsureTable` / `SeedEquipmentConfigs` / 回填 |
| 客户端 | `Assets/Game/Scripts/Equipment/EquipmentManager.cs` | 新增 |
| 客户端 | `Assets/Game/Scripts/Equipment/CharacterView.cs` | 新增（UIView） |
| 客户端 | `Assets/Game/Scripts/Equipment/EquipmentSlotView.cs`、`EquipmentRowView.cs` | 新增 |
| 客户端 | `Assets/Game/Scripts/Equipment/EquipmentAttrApplier.cs` | 新增（AttributeComponent 来源账本） |
| 客户端 | `Assets/Game/Scripts/Equipment/EquipmentBattleSnapshot.cs` | 新增（DTO + JSON） |
| 客户端 | `Assets/Game/Scripts/Bag/BagManager.cs` | 槽位带 `InstanceId`；装备行标注 |
| 客户端 | `Assets/Game/Scripts/Battle/Core/Player/Player.cs` | 应用/清理装备快照 |
| 客户端 | `Assets/Game/Scripts/Battle/Core/Player/PlayerSO.cs` | 增加 `characterId` |
| 客户端 | `Assets/Game/Scripts/Battle/Network/RoomState.cs` | 新增 `MsgEquipSnapshot` 与 `BattlePlayerInfo.EquipJson` |
| 客户端 | `Assets/Game/Scripts/Battle/Scene/BattleSpawnManager.cs` | 生成英雄后注入快照 |
| 客户端 | `Assets/Game/Scripts/UI/Framework/UIViewBase/UIType.cs` | `CharacterView=25`、`Max=26` |
| 客户端 | `Assets/Game/UI/UIConfig.json` | 新增条目 |
| 客户端 | `Assets/Game/Scripts/UI/Home/HomeView.cs` | 新增 `characterButton` 字段与打开逻辑 |
| 客户端 | `Assets/Game/Scripts/UI/Theme/Editor/HomeViewSkinBuilder.cs` | `SocialEntries` 首位加「角色」；缺失按钮自动创建并写 `UIControlData` 绑定 |
| 资源 | `Assets/Game/UI/Home/Art/Icons/icon_character.png` | 新增入口图标（128/256 导入） |
| 资源 | `Assets/Game/UI/Character/CharacterView.prefab` | 由 builder 生成 |
| 工具 | `Assets/Game/Scripts/Equipment/Editor/CharacterViewBuilder.cs` | 新增（菜单 `Tools/Equipment/重建角色界面`） |

---

## 9. 分期计划

| 阶段 | 内容 | 产出 | 完成判据 |
| --- | --- | --- | --- |
| P0 数据与协议 | 四表、种子、`BagSlotInfo.instance_id`、`Grant` 分支、历史回填、3501–3504 | 服务端可查可穿脱 | 协议级 E2E 全绿 |
| P1 客户端数据层 | `EquipmentManager`、`BagManager` 适配、缓存/重连 | 数据可用 | 打开界面数据正确、断线重连可恢复 |
| P2 角色展示 UI | 主页「角色」入口（右上圆钮最右）+ `CharacterView` 预制体与绑定、3D 预览、左侧角色列、属性面板、武器卡、武器选择二级页 | 可换装界面 | Play E2E + 录屏 |
| P3 战斗接入 | `PlayerSO.characterId`、快照应用器、单机与联机注入 | 数值生效 | 挑战/联机数值断言 |
| P4 扩展（本期不做） | 强化、词条、外观挂点、武器技能、多槽位 | — | — |

依赖关系：P0 → P1 → P2 → P3；P4 独立。

---

## 10. 验收

### 10.1 协议级（服务端）

| # | 用例 | 期望 |
| --- | --- | --- |
| 1 | 新账号拉 3501 | success=true；configs=7；instances=0；slots 为空 |
| 2 | 用 GM 发放 1014 | 生成 1 个实例；背包槽 `instance_id>0`；`count=1` |
| 3 | 穿 1014 到安比 | code=0；slots 含 (0,1,instance)；instances.equipped_char=0 |
| 4 | 重复穿同一件 | code=0 且 `unchanged=true`；数据不变 |
| 5 | 卸下（instance_id=0） | 槽清空；实例 equipped_char=-1 |
| 6 | 穿别人的实例 | code=2 |
| 7 | 穿非装备物品（如 1001 药剂） | code=3 |
| 8 | 槽位类型传 2 | code=4 |
| 9 | 角色传 9（不存在） | code=5 |
| 10 | 同一件武器从安比转到艾莲 | 原子转移；安比槽清空、艾莲槽写入；回执 note 说明 |
| 11 | 两角色各穿一件 | 各自 slots 独立，互不覆盖 |
| 12 | 背包整理/换位后 | 槽位引用不变（按 instance_id） |
| 13 | 满包时发放装备 | 走转存邮件；领取后生成实例 |
| 14 | 重登再拉 3501 | 槽位与实例一致 |
| 15 | 并发两次穿同一槽 | 事务串行；最终仅一条槽位记录 |

### 10.2 客户端 Play

| # | 用例 | 期望 |
| --- | --- | --- |
| 1 | 主页入口位置 | `characterButton` 在右上圆钮最右，中心 x=-82、尺寸 84；其余 8 个整体左移 94px，间距仍为 10、无重叠、无越界 |
| 2 | 点入口 | 1s 内打开；全屏深色遮住主页；左列两个头像、中央模型、右侧属性面板与武器卡齐全 |
| 3 | 左列切角色 | 点击头像切换；模型交叉淡入无叠影；属性、武器卡、列表标注同步刷新 |
| 4 | 拖动模型 | 可水平旋转；不影响按钮点击与列表滚动 |
| 5 | 空槽状态 | 武器卡显示"未装备武器"；属性只显示基础值 |
| 6 | 点武器卡 / 更换 | 进入二级页；列表只显示 `item_type=3` 实例；已装备项标注角色名 |
| 7 | 选中候选 | 详情与属性差值正确（↑绿 / ↓红 / 持平灰） |
| 8 | 装备 | 返回角色页；槽位、属性、列表标注同步刷新；无重复请求 |
| 9 | 跨角色装备 | 弹二次确认；确认后两角色槽位正确；取消则数据不变 |
| 10 | 卸下 | 槽位回空态，属性回落 |
| 11 | 二级页返回 / Esc | 二级页 Esc 先回角色页；角色页 Esc 关闭整页回主页 |
| 12 | 关闭重开 | 使用缓存，无重复请求；记住上次查看的角色 |
| 13 | 断线后重连 | 数据重拉，界面不残留旧值 |
| 14 | 未登录点入口 | 提示"请先登录"，不发请求 |
| 15 | 与背包互斥 | 打开角色页时背包已关闭；关闭后主页按钮行位置不变 |

### 10.3 战斗数值

| # | 用例 | 期望 |
| --- | --- | --- |
| 1 | 挑战场景装备 +48 攻击武器 | `AttributeComponent.GetValue(Attack)` = 基础 + 48；伤害数字同比上升 |
| 2 | 卸下后重进 | 属性回落到基础值 |
| 3 | 换武器 | 旧来源被移除，无叠加（连续换 3 次后仍等于单件值） |
| 4 | 联机房主侧 | 两座位各自装备生效；客机不写账本 |
| 5 | 客机装备为空 | 只吃基础属性，无报错 |
| 6 | 单机/联机角色识别 | `PlayerSO.characterId` 与 `RoomState.CharId` 一致时无警告 |

### 10.4 回归

- 背包：使用/丢弃/整理/换位/500 格虚拟列表。
- 抽卡：武器入包生成实例；重复 `request_id` 重放不重复建实例。
- 副本掉落：B 型拾取武器生成实例。
- 邮件：附件发武器、领取生成实例、满包转存。
- 成就/生涯：获得物品计数按件数（与现有口径一致）。

---

## 11. 风险与限制

| 风险 | 影响 | 处理 |
| --- | --- | --- |
| 装备数值由客户端自报给房主 | 联机可伪造属性 | 与现有"客户端算伤害"同级别；服务端校验归属，防作弊列入后续 |
| 装备与堆叠物品混存 | 同一 `item_id` 多实例在背包占多格 | 装备 `count=1` 且列表按实例展示；整理只合并可堆叠类型 |
| 武器无 3D 模型与挂点 | 展示页只有图标 | 本期不做外观；`model_key` 字段预留 |
| `MaxHP` 不在 `AttrType` | 装备不能加血 | 本期白名单排除；需要时新增枚举末尾值并改 `HealthModel` |
| 角色拥有体系缺失 | 所有账号可用两角色 | 装备按 `char_id` 归属不依赖拥有表；将来加 `player_characters` 不影响本设计 |
| 背包 `instance_id` 为空的历史数据 | 装备无法引用 | 启动一次性回填；回填失败时该装备按"不可穿戴"处理并记日志 |
| 多槽位扩展 | 需要改 UI 与配置 | `slot_type` 已在三层模型中；扩槽只加配置行与界面区块 |
| 角色数量增长 | 左列固定两枚头像，放不下更多角色 | 头像列按 `VirtualScrollList` 实现（本期两人直接摆），后续加角色不改布局 |
| 主页入口行变长 | 9 个圆钮总宽 930（1920 参考分辨率），继续加会挤占 | 后续入口改两排或缩小 `SocialSize`；本期不加第 10 个 |

---

## 12. 待确认

1. 3.3 的武器数值表是否按此占位（只用于验证伤害变化），最终表由策划给。
2. 跨角色装备同一件武器采用"UI 二次确认 + 服务端原子转移"是否接受（备选：直接拒绝并提示先卸下）。
3. 本期是否需要"新获得武器"红点（当前设计无红点）。
4. 角色头像用 `Assets/Game/UI/Battle/Portraits` 现有两张图裁剪即可，还是需要单独做两个 96×96 头像（安比没有确认专属图）？
5. 角色页是否显示"等级 / 好感度 / 元素"这类当前数据里没有的字段（当前只显示角色名 + 武器类型）。

本期按以下默认实施：3.3 的数值表作为占位（只用于验证伤害变化）；跨角色同一件武器采用"UI 二次确认 + 服务端原子转移"；不做"新获得武器"红点；角色头像复用 `Assets/Game/UI/Battle/Portraits` 现有图；角色页只显示角色名 + 武器类型。

---

## 13. 实施现状与实测

### 13.1 已落地

| 侧 | 内容 |
| --- | --- |
| 协议 | `EquipmentPacket.proto`（3501–3504）；`BagSlotInfo.instance_id`；`MessageID` 追加 3501/3502/3503/3504 |
| 服务端 | `EquipmentRepository`（建表/种子/历史回填/Load/SetSlot）、`EquipmentHandler`（列表与穿脱）、`MessageHandler` 路由、`Program` 启动初始化、`Migrations/018_AddEquipment.sql` |
| 背包 | `BagSlot`（ItemId/Count/InstanceId）替换原二元组，`LoadSlots`/`LoadSlotsForUpdate`/`SaveSlots` 读写 `instance_id`；`GrantIntoSlots` 对 `item_type=3` 逐件建实例 |
| 入包路径 | `Grant`、唤取（`GachaRepository`）、任务奖励（`QuestRepository`）、签到（`SignInRepository`）、邮件领取（`MailRepository`）、成就奖励（`AchievementRepository`）全部走同一份 `GrantIntoSlots` |
| 数据完整性 | 整理（`Tidy`）不合并装备、保留实例ID；换位（`SwapSlots`）整槽搬运；丢弃/任务交付耗尽时删除实例行；穿着的装备禁止使用/丢弃并提示先卸下 |

### 13.2 实测结论（2026-10-27，TestClient 对 8888）

| # | 用例 | 结果 |
| --- | --- | --- |
| 1 | 新账号拉 3501 | 配置 7 / 实例 0 / 槽位 0 |
| 2 | GM 发放 1014 | 背包 1 格、`instance_id=1595`、`count=1` |
| 3 | 穿到安比 | `code=0`，槽位 (0,1)→1595 |
| 4 | 重复穿同一件 | `code=0`、`unchanged=true`，数据不变 |
| 5 | 卸下（`instance_id=0`） | 槽位清空，另一角色槽位不受影响 |
| 6 | 穿不存在的实例 | `code=2` |
| 7 | 穿非装备物品的实例 | `code=3` |
| 8 | 槽位类型传 2 | `code=4` |
| 9 | 角色传 9 | `code=5` |
| 10 | 1014 从安比转到艾莲 | 原子转移，安比槽清空、艾莲槽写入，回执说明原角色 |
| 11 | 两角色各穿一件 | 两条槽位独立 |
| 12 | 整理 + 换位后 | 实例ID跟着槽位走，未丢失 |
| 13 | 满包（500 格）发放 1017 | 转存邮件；邮件领取后生成实例（实例数 0→1） |
| 14 | 重登再拉 3501 | 槽位与实例一致 |
| 15 | 穿着时丢弃 | 拒绝并提示"请先从角色身上卸下该装备"；未穿戴的正常丢弃且实例行同步删除 |

回归：唤取十连正常入包，3 件武器各生成独立实例；同一 `request_id` 重放不重复建实例。历史武器回填在本机库补建 593 件实例。

未覆盖：用例 15 的"并发两次穿同一槽"未做真并发（同账号双连接会被顶号），改为顺序重放验证，串行化由 `PlayerWriteLock` + `accounts` 主行锁 + 槽位主键保证。

### 13.3 客户端数据层（P1）

- `EquipmentManager`：登录成功即拉 3501（背包"已装备"标注不依赖打开角色页）；`EnsureLoaded` 顺带拉一次背包配置表（名称/图标/稀有度同源于 `item_config`）；断线保留缓存并标记待重拉；穿戴单在途 + 5 秒超时提示；穿成功后静默刷新背包。
- `attr_json` 用 `JsonUtility` 包装成对象解析，未知 `AttrType` 值跳过（服务端先加枚举不会炸客户端）。
- 背包：装备实例正穿在身上时格子金色边框、详情行追加"已装备：角色名"。

实测（Play，`e2e_equip_a`）：`IsLoaded=true`，武器实例 4 件，角色1 穿戴 1596（音感仪·初），加成 Attack=44 / DamageUp=0.10，快照 JSON 携带 2 条属性。

### 13.4 角色页（P2）

- `CharacterView`（`UIType.CharacterView=25`，`UIConfig.json` 已登记）：全屏深色，左列角色头像（`VirtualScrollList` + `BattleHeroCatalog.PortraitPath`）、中央 3D 预览、右侧属性面板与武器卡；武器选择二级页与跨角色确认框在同一预制体内切换，Esc 先回二级页再关整页；未登录打开只提示"请先登录"不发请求。
- `CharacterViewBuilder`（菜单 `Tools/Equipment/重建角色界面`）生成预制体，43 项 `[ControlBinding]`，按 `PlayerSO.CharacterId` 注入角色基础属性；`PlayerSO.characterId` 用普通序列化字段（自动属性背后的编译字段名无法按名读写）。
- 主页新增「角色」入口：`HomeViewSkinBuilder.SocialEntries` 首位，节点缺失时自动补建并写 `UIControlData`；打开前先关背包（互斥）；入口图标 `icon_character.png` 按 256 导入。

实测（Play，`e2e_equip_a`）：预览目标数=1（模型正常渲染），安比 攻击 144(+44)、武器卡"音感仪·初 ★★★★★"；切艾莲后武器与属性回落到基础值；武器页列表/详情/对比文字正确；跨角色装备弹出"该武器正被艾莲装备，是否卸下并装备？"，确认后角色0 拿到 1596、角色1 清空。录屏视检：布局无重叠越界、中文清晰、模型彩色正常。

### 13.5 战斗接入（P3）

- `PlayerSO.characterId`（0 安比 / 1 艾莲，两份 SO 已登记）；单机场景 `Player.Awake` 按本地快照应用装备加成，联机不读本地。
- `EquipmentAttrApplier`：一件装备一个来源对象，`Apply` 先撤销本应用器全部来源再重建（重复应用不叠加），`Clear` 按来源逐类型移除；`Player.ApplyEquipmentSnapshot` / `ClearEquipmentSnapshot`，`OnNetworkDespawn` 自动清空。
- 联机：`RoomState.MsgEquipSnapshot`（客户端上报 JSON）+ `PlayerState/BattlePlayerInfo.EquipJson`；房主选角色走 `ServerSetEquipSnapshot`，`BattleSpawnManager` 生成英雄后在角色属于同一 `CharId` 时注入，`RoomState` 换角色/开局重置时保留快照字段。

实测（Play，tiaozhan，角色 0）：基础攻击 100 + 音感仪 44 = **144**、DamageUp 0.10；同一快照重复应用 3 次仍为 144/0.10；换成 1017 快照后 148/0.08（替换而非累加）；`Clear` 后回落 100/0。

未覆盖：联机双座位的实际注入未在本机实测（需要两个客户端进程）；快照按座位下发的代码路径以编译与结构审查确认。
