namespace Game.Equipment
{
    /// <summary>装备加成合计（只算装备部分，不含角色基础值）</summary>
    public struct EquipAttrTotals
    {
        /// <summary>攻击加成（数值）</summary>
        public float Attack;

        /// <summary>防御加成（数值）</summary>
        public float Defense;

        /// <summary>伤害系数</summary>
        public float DamageUp;

        /// <summary>减伤系数</summary>
        public float DamageDown;

        /// <summary>移速系数</summary>
        public float MoveSpeed;
    }
}