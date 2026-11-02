using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;
using UnityEngine.UI;

/// <summary>主界面顶部的“对战”入口：打开对战大厅（SkierFramework UIView）。</summary>
public class BattleEntryButton : MonoBehaviour
{
    [SerializeField] private Button button;

    private void Awake()
    {
        if (button == null) button = GetComponent<Button>();
    }

    private void OnEnable()
    {
        if (button != null) button.onClick.AddListener(OnClicked);
    }

    private void OnDisable()
    {
        if (button != null) button.onClick.RemoveListener(OnClicked);
    }

    private void OnClicked()
    {
        UIManager.Instance.Open(UIType.BattleLobbyView).Forget();
    }
}
