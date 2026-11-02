using System;
using UnityEngine;

/// <summary>
/// 原神demo·NPC交互检测：玩家靠近进入范围，按 F 请求开始对话
/// 事件由 Director 消费；对话期间 Director 自行忽略重复请求
/// </summary>
public class GenshinInteractionZone : MonoBehaviour
{
    [SerializeField] private Transform player;
    [SerializeField] private float interactRange = 3f;
    [SerializeField] private KeyCode interactKey = KeyCode.F;

    public bool PlayerInRange { get; private set; }

    /// <summary> 进入/离开交互范围 </summary>
    public event Action<bool> RangeChanged;

    /// <summary> 范围内按下交互键 </summary>
    public event Action InteractRequested;

    private void Start()
    {
        // 场景未接线时自动兜底查找玩家
        if (player == null)
        {
            var found = GameObject.Find("545");
            if (found != null) player = found.transform;
        }
    }

    private void Update()
    {
        if (player == null) return;

        float sqrDist = (player.position - transform.position).sqrMagnitude;
        bool inRange = sqrDist < interactRange * interactRange;
        if (inRange != PlayerInRange)
        {
            PlayerInRange = inRange;
            RangeChanged?.Invoke(inRange);
        }

        // 暂停中不接受交互键（暂停面板关闭后恢复）
        if (!PauseManager.IsPaused && PlayerInRange && Input.GetKeyDown(interactKey))
            InteractRequested?.Invoke();
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, interactRange);
    }
}
