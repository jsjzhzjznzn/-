using UnityEngine;

namespace KinematicCharacterController
{
    /// <summary>
    /// 标记特性：让被标注的字段在 Inspector 中显示为只读（看得见、但改不动）。
    ///
    /// 这是一个"纯标记"属性，类体为空、不携带任何数据。它唯一的作用是提供一个类型，
    /// 让编辑器端的 ReadOnlyPropertyDrawer 通过
    /// [CustomPropertyDrawer(typeof(ReadOnlyKCCAttribute))] 精确匹配到它，从而接管该字段的绘制。
    ///
    /// 为什么需要它（而不是直接用 [HideInInspector]）：
    /// 有些引用字段完全由组件自己接管，手改会导致内部状态不一致，但又需要让使用者看得见、
    /// 能确认引用是否挂对了。例如：
    ///   - KinematicCharacterMotor.Capsule  由 ValidateData() 里的 GetComponent 获取，方向和尺寸都由 Motor 托管
    ///   - PhysicsMover.Rigidbody           由 ValidateData() 获取，并强制 isKinematic / 关插值 / 改质心
    /// 一旦在 Inspector 里被换成别的对象，sweep 查询与刚体交互会立刻全线错乱。
    ///
    /// 为什么叫 ReadOnlyKCC 而不是 ReadOnly：
    /// 工程里常会引入第三方的 [ReadOnly] 特性，加 KCC 前缀可避免两个同名 Attribute 撞车。
    ///
    /// 运行时开销为零：PropertyAttribute 只在编辑器序列化/绘制时被反射查找，打包后并不存在；
    /// 真正干活的 ReadOnlyPropertyDrawer 位于 Editor 目录并被 #if UNITY_EDITOR 包裹，不会进入 Player。
    /// 但本类必须留在 Runtime 程序集——因为 KinematicCharacterMotor 要引用它。
    ///
    /// 用法：
    ///   [ReadOnlyKCC]
    ///   public CapsuleCollider Capsule;
    /// </summary>
    public class ReadOnlyKCCAttribute : PropertyAttribute
    {
        // 刻意保持为空：不带任何配置项，"打了这个标签"本身就是全部语义。
        // 若将来需要"仅在运行时只读"之类的开关，才在这里加字段。
    }
}
