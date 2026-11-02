using System.Collections;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;

namespace SkierFramework
{
    /// <summary>
    /// 格斗式 HUD 血条面板（左=我方，右=敌方）。
    /// 数据源：双方角色身上的 HealthModel（BindableProperty 直订刷新填充条），
    /// 头像按角色身上的 Player.NetCharId 加载（服务端生成时写入，随生成同步到所有端）。
    /// 打开时机：Player.OnNetworkSpawn（IsOwner 端）；单机不打开。
    ///
    /// 移植自源工程 Assets/script/newui/bloodpanel.cs：
    /// 逻辑保持一致，只有头像资源路径改成目标工程 YooAsset 收集目录（Assets/Game/UI/Battle/Portraits）。
    /// </summary>
    public class bloodpanel : UIView
    {
        /// <summary>头像资源（CharId：安比=0，艾莲=1；YooAsset 收集器 GameUI 组）</summary>
        private const string PortraitAnBi = "Assets/Game/UI/Battle/Portraits/Snipaste_2026-09-10_18-25-50";
        private const string PortraitShaYu = "Assets/Game/UI/Battle/Portraits/Snipaste_2026-09-10_18-25-22";

        #region 控件绑定变量声明，自动生成请勿手改
#pragma warning disable 0649
        [ControlBinding]
        private Image bloodzhu;
        [ControlBinding]
        private Image renwu2;
        [ControlBinding]
        private Image renwu1;
        [ControlBinding]
        private Image bloodenemy;

#pragma warning restore 0649
        #endregion

        private Image _myFill;
        private Image _enemyFill;
        private HealthModel _myHealth;
        private HealthModel _enemyHealth;
        private Coroutine _bindCoroutine;

        public override void OnInit(UIControlData uIControlData, UIViewController controller)
        {
            base.OnInit(uIControlData, controller);

            _myFill = PrepareFillImage(bloodzhu, "zhublood");
            _enemyFill = PrepareFillImage(bloodenemy, "enemyblood");
        }

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);

            // 面板实例常驻 UIRoot（DDOL），跨局重开时先清掉上一局的绑定
            UnbindAll();
            _bindCoroutine = StartCoroutine(BindPlayers());
        }

        public override void OnClose()
        {
            base.OnClose();
            // 取消注册网络事件回调，避免内存泄漏
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            }
            UnbindAll();
        }

        #region 绑定

        /// <summary>
        /// 监听客户端连接事件，而不是轮询。当有新客户端连接时尝试绑定玩家。
        /// </summary>
        private void TryBindPlayers()
        {
            Player mine = null;
            Player enemy = null;

            foreach (var player in FindObjectsOfType<Player>())
            {
                if (player.IsOwner) mine = player;
                else if (player.IsSpawned) enemy = player;
            }

            if (mine != null && enemy != null)
            {
                BindSide(mine, ref _myHealth, _myFill, renwu1);
                BindSide(enemy, ref _enemyHealth, _enemyFill, renwu2);
            }
        }

        private IEnumerator BindPlayers()
        {
            // 先尝试绑定一次
            TryBindPlayers();

            // 如果还没绑定成功，注册客户端连接回调
            if (_myHealth == null || _enemyHealth == null)
            {
                if (NetworkManager.Singleton != null)
                    NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            }

            yield break;
        }

        private void OnClientConnected(ulong clientId)
        {
            // 有新客户端连接时重新尝试绑定
            if (_myHealth == null || _enemyHealth == null)
            {
                TryBindPlayers();
            }

            // 绑定成功后取消注册
            if (_myHealth != null && _enemyHealth != null && NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            }
        }

        private void BindSide(Player player, ref HealthModel bound, Image fill, Image portrait)
        {
            var health = player.GetComponent<HealthModel>();
            if (health == null)
            {
                Debug.LogError($"[bloodpanel] 角色 {player.name} 上没有 HealthModel，该侧血条无法绑定！");
                return;
            }

            bound = health;
            health.CurrentHP.OnValueChanged += OnHPChanged;
            health.MaxHP.OnValueChanged += OnHPChanged;
            RefreshFill(fill, health);
            LoadPortrait(portrait, player.NetCharId.Value).Forget();
        }

        private void UnbindAll()
        {
            if (_bindCoroutine != null)
            {
                StopCoroutine(_bindCoroutine);
                _bindCoroutine = null;
            }
            UnbindSide(ref _myHealth);
            UnbindSide(ref _enemyHealth);
        }

        private void UnbindSide(ref HealthModel bound)
        {
            if (bound == null) return;
            bound.CurrentHP.OnValueChanged -= OnHPChanged;
            bound.MaxHP.OnValueChanged -= OnHPChanged;
            bound = null;
        }

        /// <summary>任一侧血量变化都全量刷新（两条填充条各刷一次，开销可忽略）</summary>
        private void OnHPChanged(float oldVal, float newVal)
        {
            RefreshFill(_myFill, _myHealth);
            RefreshFill(_enemyFill, _enemyHealth);
        }

        #endregion

        private static void RefreshFill(Image fill, HealthModel health)
        {
            if (fill == null || health == null) return;
            fill.fillAmount = health.MaxHP.Value > 0f
                ? Mathf.Clamp01(health.CurrentHP.Value / health.MaxHP.Value)
                : 0f;
        }

        /// <summary>取血条底框下的填充条子 Image，并强制为水平 Filled 模式</summary>
        private static Image PrepareFillImage(Image barBg, string childName)
        {
            if (barBg == null) return null;
            var child = barBg.transform.Find(childName);
            var fill = child != null ? child.GetComponent<Image>() : null;
            if (fill == null)
            {
                Debug.LogError($"[bloodpanel] 血条底框 {barBg.name} 下找不到填充子节点 {childName}");
                return null;
            }
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            return fill;
        }

        private static async UniTaskVoid LoadPortrait(Image portrait, int charId)
        {
            if (portrait == null) return;
            string path;
            switch (charId)
            {
                case 0: path = PortraitAnBi; break;
                case 1: path = PortraitShaYu; break;
                default:
                    Debug.LogError($"[bloodpanel] 未知 CharId={charId}，头像不加载");
                    return;
            }
            var sprite = await ResourceManager.Instance.LoadAssetAsync<Sprite>(path);
            if (sprite != null) portrait.sprite = sprite;
        }
    }
}
