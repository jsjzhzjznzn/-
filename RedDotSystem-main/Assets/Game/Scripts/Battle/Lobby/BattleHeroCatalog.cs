using UnityEngine;

/// <summary>
/// 对战英雄目录：CharId 必须与战斗场景 BattleSpawnManager.HeroPrefabList 的下标一致。
///
/// P3 说明：角色美术资源按计划在 P7 迁移，这里先用项目已有角色做 3D 预览占位。
/// P7 只需要把 PreviewPath 换到安比/艾莲的预览预制体，并同步预览取景参数即可，
/// 房间/联机/UI 逻辑不需要改动。
/// </summary>
public static class BattleHeroCatalog
{
    public const int Anbi = 0;
    public const int Ellen = 1;

    public struct HeroInfo
    {
        public int Id;
        public string DisplayName;
        /// <summary>预览预制体的 Resources 相对路径（Resources.Load 用，不走 YooAsset）</summary>
        public string PreviewPath;

        /// <summary>头像贴图资源路径（ResourceManager 加载，角色页/战斗 HUD 共用）</summary>
        public string PortraitPath;
        /// <summary>预览模型相对原点的下移量，让脚底对齐取景框底部</summary>
        public float PreviewOffsetY;
        /// <summary>预览正交相机的 half-height</summary>
        public float PreviewOrthSize;

        // ── 以下为角色页展示用元数据：项目当前没有元素 / 星级 / 等级养成系统，
        //    这些字段只用于角色页排版，不参与任何战斗数值计算。
        /// <summary>元素名（角色页顶部「元素 / 名字」）</summary>
        public string ElementName;
        /// <summary>星级 1-5</summary>
        public int Rarity;
        /// <summary>展示等级 / 等级上限 / 当前经验 / 升级所需经验</summary>
        public int Level;
        public int MaxLevel;
        public int Exp;
        public int ExpMax;
        /// <summary>角色页简介</summary>
        public string Description;
    }

    private static readonly HeroInfo[] Heroes =
    {
        new HeroInfo
        {
            Id = Anbi,
            DisplayName = "安比",
            PreviewPath = "Battle/Heroes/AnbiPreview",
            PortraitPath = "Assets/Game/UI/Battle/Portraits/Snipaste_2026-09-10_18-25-50",
            PreviewOffsetY = -0.79f,
            PreviewOrthSize = 0.88f,
            ElementName = "电元素",
            Rarity = 5,
            Level = 78,
            MaxLevel = 80,
            Exp = 135219,
            ExpMax = 252950,
            Description = "沉默寡言的少女，握紧手中的剑。\n无论何时，她都会站在你身前。",
        },
        new HeroInfo
        {
            Id = Ellen,
            DisplayName = "艾莲",
            PreviewPath = "Battle/Heroes/EllenPreview",
            PortraitPath = "Assets/Game/UI/Battle/Portraits/Snipaste_2026-09-10_18-25-22",
            PreviewOffsetY = -0.86f,
            PreviewOrthSize = 0.95f,
            ElementName = "冰元素",
            Rarity = 5,
            Level = 62,
            MaxLevel = 70,
            Exp = 84210,
            ExpMax = 198500,
            Description = "来自深海的女仆，寡言而克制。\n对每一件小事的认真，都是她的温柔。",
        },
    };

    public static int Count => Heroes.Length;

    public static bool IsValid(int charId)
    {
        return charId >= 0 && charId < Heroes.Length;
    }

    public static HeroInfo Get(int charId)
    {
        return IsValid(charId) ? Heroes[charId] : Heroes[0];
    }

    public static string GetDisplayName(int charId)
    {
        return IsValid(charId) ? Heroes[charId].DisplayName : "未选择";
    }
}
