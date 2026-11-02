namespace SkierFramework
{
    public enum UIType
    {
		Dating = 0,
		Xuanze = 1,
		room = 2,
		jiaru = 3,
		bloodpanel = 4,
		SmokeView = 5,
		LeaderboardView = 6,
		BattleLobbyView = 7,
		JoinRoomView = 8,
		BattleRoomView = 9,
		FriendView = 10,
		HomeView = 11,
		QuestView = 12,
		MailView = 13,
		WorldChatView = 14,
		// 15 原为 ChallengeView：旧的 5 秒点击挑战面板已删除，挑战改为独立场景。
		// 现在 15 由击杀竞速 HUD 复用，避免扩大 UIType 的取值范围。
		ChallengeHudView = 15,
		AgentView = 16,
		BagView = 17,
		SettingsView = 18,
		// 活动面板：内容由热更包构建，视图本身只提供框架落点（见 ActivityView）
		ActivityView = 19,
		// 登录面板：从「场景里直接实例化」改为框架加载（UIConfig 路径仍是 Assets/Game/UI/Login/LoginPanel.prefab，
		// 依旧走 ui 包，热更语义不变）；LOGIN 场景的 LoginSceneBootstrap 只负责 Open/Close
		LoginView = 20,
		// 对战好友邀请：房主侧的好友选择面板（列表 + 邀请按钮 + 待回应状态）
		BattleInviteView = 21,
		// 收到对战邀请的弹窗（服务端推送即弹，任何界面都能收到）
		BattleInvitePopupView = 22,
		// 对话面板（原神式底部对话条 + 选项）：从场景内画布迁入框架，由导演开合
		DialogueView = 23,
		// 成就面板：独立于任务面板（系列分组/条件/奖励/领取/隐藏成就）
		AchievementView = 24,
		// 角色页：原神式全屏展示（角色列 + 3D 预览 + 属性 + 装备武器），二级页在同一预制体内切换
		CharacterView = 25,
		// 大世界 HUD：任务追踪 + 功能热键 + 血条 + 数字键切人（dashijie 场景常驻）
		DashijieHudView = 26,
		// 大世界地图：全屏底图 + 玩家标记 + 滚轮缩放/拖拽，暂停世界
		DashijieMapView = 27,
		// 共鸣者影像簿（玩家档案页）：主页左上玩家名片入口，视频背景 + 资料面板 + 角色立绘
		ProfileView = 28,
		// 方舟卡牌主界面：关卡列表 + 卡组编辑器入口 + 退出活动（MainMenuScene 由场景引导打开）
		ArkCardsMainView = 29,
		// 方舟卡牌卡组编辑器：卡池网格 + 保存/返回（DeckEditorScene 由场景引导打开）
		ArkCardsDeckEditorView = 30,
		Max = 31,
    }
}
