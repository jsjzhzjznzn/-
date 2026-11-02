using SkierFramework;
using UnityEngine;
#if UNITY_EDITOR
using UnityEngine.InputSystem;
#endif

/// <summary>
/// P4 单机战斗测试场景的引导：
///   - 初始化 UI 框架的层与 UI 相机（伤害飘字需要 DamageLayer）
///   - 创建 DamageTextManager
///   - 每秒打印玩家状态机/血量，便于 E2E 与手动观察
/// </summary>
[DefaultExecutionOrder(-1000)]
public class BattleTestBootstrap : MonoBehaviour
{
    public Player player;
    public HealthModel dummy;
    public float dummyMaxHP = 300f;
    public bool initUI = true;
    public bool logEverySecond = true;

    private float _logTimer;

    private void Start()
    {
        // 编辑器在失焦时会暂停 Player 循环，自动化测试（终端占焦点）会卡在 WaitForSeconds。
        Application.runInBackground = true;
#if UNITY_EDITOR
        // 编辑器 Play 默认只把键盘/鼠标输入发给聚焦的 Game View。
        // E2E 脚本与后台 Play 时 Game View 不聚焦，会表现为"按键没反应"。
        if (InputSystem.settings != null)
            InputSystem.settings.editorInputBehaviorInPlayMode =
                InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif

        if (initUI)
        {
            UIManager.Instance.Initialize();
            _ = DamageTextManager.MainInstance;
        }

        if (player == null) player = FindObjectOfType<Player>();
        if (dummy == null)
        {
            var dummyGo = GameObject.Find("Dummy");
            if (dummyGo != null) dummy = dummyGo.GetComponent<HealthModel>();
        }
        if (dummy != null) dummy.InitStats(dummyMaxHP, dummyMaxHP);

        Debug.Log($"[BattleTest] 就绪 player={(player != null)} dummy={(dummy != null)} dummyHP={(dummy != null ? dummy.CurrentHP.Value : -1)} uiCamera={(UIManager.Instance.UICamera != null)}");
    }

    private void Update()
    {
        if (!logEverySecond || player == null) return;

        _logTimer += Time.deltaTime;
        if (_logTimer < 1f) return;
        _logTimer = 0f;

        var playerHealth = player.GetComponent<HealthModel>();
        Debug.Log($"[BattleTest] move={player.MovementStateMachine?.CurrentState?.GetType().Name} " +
                  $"combo={player.ComboStateMachine?.CurrentState?.GetType().Name} " +
                  $"hp={(playerHealth != null ? playerHealth.CurrentHP.Value : -1):F0} " +
                  $"dead={player.IsDead} pos={player.transform.position}");
    }
}
