using System;

/// <summary>新手引导单步定义（数据即流程：增删改步骤不需要动状态机）</summary>
[Serializable]
public class GuideStep
{
    public enum Kind
    {
        /// <summary>信息步：全屏暗罩 + 居中气泡 + 继续按钮</summary>
        Info,
        /// <summary>讲解步：遮罩挖洞高亮主页入口（洞内也拦截点击）+ 气泡 + 下一步按钮</summary>
        Explain,
    }

    public Kind kind;

    /// <summary>气泡标题（空则隐藏标题行）</summary>
    public string title;

    /// <summary>气泡正文</summary>
    public string text;

    /// <summary>
    /// 讲解步的目标：HomeView 上的 [ControlBinding] 字段名（如 "bagButton"）。
    /// 按字段名反射取按钮，不写死 transform 路径——主页改版不会失效。
    /// </summary>
    public string homeField;

    /// <summary>继续按钮文字（空 = 不显示按钮）</summary>
    public string continueLabel;
}

/// <summary>新手引导内容配置（zhuye 主界面·全入口讲解版）</summary>
public static class GuideSteps
{
    /// <summary>引导标识（服务端 player_guide_progress.guide_key）</summary>
    public const string GuideKey = "zhuye";

    /// <summary>引导所在的场景（主页入口都在这里）</summary>
    public const string ZhuyeSceneName = "zhuye";

    /// <summary>
    /// 玩家ID分界线：ID ≤ 此值的存量账号视为"老号"，不再自动开场（设置里可手动重看）。
    /// 本地/服务端都感知不到账号年龄，用自增ID近似"只有新注册账号才自动看到引导"
    /// （当前已知最大ID=39，要调整只改这一处）。
    /// </summary>
    public const int GuideCutoffPlayerId = 39;

    /// <summary>zhuye 主界面引导步骤表（顺序即屏幕阅读顺序：主行动 → 右上圆钮 → 左侧竖排 → 设置）</summary>
    public static readonly GuideStep[] Zhuye =
    {
        new GuideStep
        {
            kind = GuideStep.Kind.Info,
            title = "欢迎来到主页",
            text = "花两分钟认识这里的每个入口。\n随时可以跳过，下次会从你停下的地方继续。",
            continueLabel = "开始引导",
        },
        new GuideStep
        {
            kind = GuideStep.Kind.Explain,
            homeField = "mainCtaButton",
            title = "开始探索",
            text = "这里是主线入口，点击进入探索场景，推进剧情与对话。",
            continueLabel = "下一步",
        },
        new GuideStep
        {
            kind = GuideStep.Kind.Explain,
            homeField = "characterButton",
            title = "角色",
            text = "查看角色属性、等级与武器，也能切换当前角色。",
            continueLabel = "下一步",
        },
        new GuideStep
        {
            kind = GuideStep.Kind.Explain,
            homeField = "friendButton",
            title = "好友",
            text = "好友列表、添加好友、私聊都在这里，还能看到好友动态。",
            continueLabel = "下一步",
        },
        new GuideStep
        {
            kind = GuideStep.Kind.Explain,
            homeField = "mailButton",
            title = "邮件",
            text = "系统奖励与补偿会寄到这里；背包满时奖励也会转存成邮件。",
            continueLabel = "下一步",
        },
        new GuideStep
        {
            kind = GuideStep.Kind.Explain,
            homeField = "questButton",
            title = "任务",
            text = "接取任务、查看进度、领取奖励，章节会随完成度自动推进。",
            continueLabel = "下一步",
        },
        new GuideStep
        {
            kind = GuideStep.Kind.Explain,
            homeField = "achievementButton",
            title = "成就",
            text = "长期目标与生涯统计，达成后领奖励，部分成就是隐藏的。",
            continueLabel = "下一步",
        },
        new GuideStep
        {
            kind = GuideStep.Kind.Explain,
            homeField = "leaderboardButton",
            title = "排行榜",
            text = "挑战成绩的排名，能查到自己的最好成绩与当前名次。",
            continueLabel = "下一步",
        },
        new GuideStep
        {
            kind = GuideStep.Kind.Explain,
            homeField = "worldButton",
            title = "世界",
            text = "世界频道，和其他玩家一起聊天。",
            continueLabel = "下一步",
        },
        new GuideStep
        {
            kind = GuideStep.Kind.Explain,
            homeField = "agentButton",
            title = "阿福",
            text = "随时提问的助手，可以查玩法、问数据、记待办。",
            continueLabel = "下一步",
        },
        new GuideStep
        {
            kind = GuideStep.Kind.Explain,
            homeField = "bagButton",
            title = "背包",
            text = "道具与装备都收纳在这里，可以换位、使用、丢弃。",
            continueLabel = "下一步",
        },
        new GuideStep
        {
            kind = GuideStep.Kind.Explain,
            homeField = "battleButton",
            title = "对战",
            text = "和其他玩家联机竞技，创建房间或加入好友的房间。",
            continueLabel = "下一步",
        },
        new GuideStep
        {
            kind = GuideStep.Kind.Explain,
            homeField = "challengeButton",
            title = "挑战",
            text = "单人击杀挑战，结算成绩会进排行榜。",
            continueLabel = "下一步",
        },
        new GuideStep
        {
            kind = GuideStep.Kind.Explain,
            homeField = "activityButton",
            title = "活动",
            text = "限时活动入口：唤取、签到，以及迷雾信标小游戏。",
            continueLabel = "下一步",
        },
        new GuideStep
        {
            kind = GuideStep.Kind.Explain,
            homeField = "settingsButton",
            title = "设置",
            text = "音量、静音、全屏、退出登录，也可以在这里重看新手引导。",
            continueLabel = "下一步",
        },
        new GuideStep
        {
            kind = GuideStep.Kind.Info,
            title = "引导完成",
            text = "都认识完了，去点右下角的主按钮开始吧。\n老玩家可以在设置里重看这份引导。",
            continueLabel = "完成",
        },
    };

    /// <summary>讲解步总数（用于气泡里的「3 / 14」进度显示）</summary>
    public static int ExplainCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < Zhuye.Length; i++)
                if (Zhuye[i].kind == GuideStep.Kind.Explain) count++;
            return count;
        }
    }
}
