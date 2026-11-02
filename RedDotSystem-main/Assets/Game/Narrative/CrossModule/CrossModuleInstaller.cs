using Miemie.DialogSystem;
using Miemie.DialogSystem.Quest;
using UnityEngine;

namespace Miemie.CrossModule
{
    /// <summary>
    /// 跨模块服务聚合体：由组合根一次性装配并推给消费者
    /// </summary>
    public sealed class CrossServices
    {
        public IDialogueCrossService Dialogue;
        public IQuestCrossService Quest;
    }

    /// <summary>
    /// 跨模块依赖消费者：实现本接口后即可被组合根注入
    /// 注入发生在所有模块 Awake 结束后（Installer 的 Start 先于其它脚本的 Start），
    /// 因此不要在 Awake 里使用注入的服务
    /// </summary>
    public interface ICrossModuleInjectable
    {
        void InjectCrossServices(CrossServices services);
    }

    /// <summary>
    /// 跨模块组合根：场景内唯一负责依赖装配的地方
    /// 各模块 Runner 自己用构造函数创建 Cross 服务并暴露 Cross 属性；
    /// 这里把服务“推”给显式登记的消费者，取代原 BusinessModuleHub 的按类型“拉取”
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [AddComponentMenu("Miemie/跨模块组合根 (CrossModuleInstaller)")]
    public class CrossModuleInstaller : MonoBehaviour
    {
        [Header("模块入口（留空则自动查找）")]
        [SerializeField] private DialogueRunner dialogueRunner;
        // 注意：全局命名空间存在另一个 QuestManager（本项目服务端任务系统），必须完全限定名
        [SerializeField] private Miemie.DialogSystem.Quest.QuestManager questManager;

        [Header("消费者登记表（实现 ICrossModuleInjectable 的组件拖到这里）")]
        [SerializeField] private MonoBehaviour[] consumers;

        public CrossServices Services { get; private set; }

        private void Start()
        {
            var runner = dialogueRunner != null ? dialogueRunner : FindObjectOfType<DialogueRunner>();
            var quest = questManager != null ? questManager : FindObjectOfType<Miemie.DialogSystem.Quest.QuestManager>();

            Services = new CrossServices
            {
                Dialogue = runner != null ? runner.Cross : null,
                Quest = quest != null ? quest.Cross : null,
            };

            if (consumers != null)
            {
                foreach (var consumer in consumers)
                {
                    if (consumer is ICrossModuleInjectable injectable)
                        injectable.InjectCrossServices(Services);
                }
            }

            Debug.Log($"[CrossModuleInstaller] 装配完成 Dialogue={Services.Dialogue != null} Quest={Services.Quest != null} 登记消费者={(consumers != null ? consumers.Length : 0)}");
        }

        /// <summary>
        /// 运行时补充注入：运行时生成的消费者不走登记表，由其所有者显式调用
        /// </summary>
        public void Inject(ICrossModuleInjectable consumer)
        {
            if (Services != null)
                consumer?.InjectCrossServices(Services);
        }
    }
}
