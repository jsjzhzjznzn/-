using UnityEngine;
/// <summary>
/// 命名空间：项目核心资源数据层
/// </summary>
    /// <summary>
    /// 玩家全套连招序列化数据类
    /// 统一存储玩家轻攻击、重攻击、处决、技能、终结技、切换技能所有连招配置资源
    /// 可序列化嵌入PlayerSO，集中管理角色全部战斗连招数据
    /// </summary>

    [System.Serializable]
    public class PlayerComboSOData
    {
        /// <summary>轻攻击连招容器（整套平A连击、前进攻击数据）</summary>
        [field: SerializeField, Header("轻攻击连招")] 
        public ComboContainerData lightCombo { get; private set; }

        /// <summary>重攻击连招容器（重击整套连击、闪避重击数据）</summary>
        //[field: SerializeField, Header("重攻击连招")] 
      //  public ComboContainerData heavyCombo { get; private set; }

        /// <summary>处决连招容器（倒地/破防后终结处决连招）</summary>
        //[field: SerializeField, Header("处决连招")] 
        //public ComboContainerData executeCombo { get; private set; }

        /// <summary>常规技能单套连招数据</summary>
        [field: SerializeField, Header("常规技能")] 
        public ComboData skillCombo { get; private set; }

        /// <summary>必杀终结技连招数据</summary>
        [field: SerializeField, Header("终结必杀技")] 
        public ComboData finishSkillCombo { get; private set; }

        /// <summary>形态/武器切换专用技能连招数据</summary>
        //[field:SerializeField,Header("切换形态技能")] 
       // public ComboData switchSkill { get; private set; }

        /// <summary>是否开启攻击自动索敌（仅单机生效；联网时朝向由网络同步）</summary>
        [field: SerializeField, Header("攻击自动索敌")]
        public bool autoAimEnabled { get; private set; } = true;

        /// <summary>自动索敌的搜索半径（米）：半径内最近的敌人会被作为朝向目标</summary>
        [field: SerializeField] public float autoAimRadius { get; private set; } = 5f;

        /// <summary>自动索敌的前方夹角上限（度）：敌人与角色正面的夹角超过它则不转向</summary>
        [field: SerializeField] public float autoAimMaxAngle { get; private set; } = 130f;

        /// <summary>索敌评分权重与附加分。注意：附加分是加法、可突破基础分 1.0 上限</summary>
        [field: SerializeField, Header("自动索敌评分权重")]
        public float autoAimDistanceWeight { get; private set; } = 0.5f;
        [field: SerializeField] public float autoAimAngleWeight { get; private set; } = 0.5f;
        [field: SerializeField] public float autoAimAttackingMeBonus { get; private set; } = 0.3f;

        /// <summary>目标粘滞分：上一刀的目标加分，避免连击中途切线</summary>
        [field: SerializeField] public float autoAimStickyBonus { get; private set; } = 0.35f;

        /// <summary>转向角速度上限（度/秒）：防止 180° 瞬转；&lt;=0 表示不限制</summary>
        [field: SerializeField] public float autoAimTurnSpeed { get; private set; } = 720f;

        /// <summary>玩家有方向输入且与目标夹角超过它（度）时，不吸附该目标（尊重玩家想打的方向；只影响首次选目标/粘刀）</summary>
        [field: SerializeField] public float autoAimInputRespectAngle { get; private set; } = 60f;
    }

