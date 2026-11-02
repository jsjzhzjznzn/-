using Unity.Netcode;

/// <summary>
/// 角色音效的网络同步（P5）
///
/// 只同步"关键音效"：连招台词 + 武器挥击。
/// 受击 / 闪避不走 RPC —— 远端镜像端的状态机会各自本地播一次，再广播就是双声。
/// 脚步同样不同步（频率高、收益低）。
/// </summary>
public partial class Player
{
    /// <summary>
    /// 拥有者本地播完后广播给其它客户端；voiceIndex / weaponIndex 为 -1 表示该段没播
    /// </summary>
    public void BroadcastComboAudio(int comboIndex, int voiceIndex, int weaponIndex)
    {
        if (!IsSpawned || IsRemote)
            return;
        if (voiceIndex < 0 && weaponIndex < 0)
            return;

        PlayComboAudioClientRpc(comboIndex, voiceIndex, weaponIndex);
    }

    /// <summary>
    /// 远端：按同一份连招配置取回片段后本地播放（不触发伤害 / 特效）
    /// </summary>
    [ClientRpc]
    private void PlayComboAudioClientRpc(int comboIndex, int voiceIndex, int weaponIndex)
    {
        if (IsOwner)
            return;

        var Audio = ActorAudio;
        if (Audio == null)
            return;

        var Container = ComboStateMachine != null ? ComboStateMachine.ReusableData.currentCombo : null;
        if (Container == null)
            return;

        var Data = Container.GetComboData(comboIndex);
        if (Data == null)
            return;

        if (voiceIndex >= 0)
            Audio.PlayRemoteClip(Data.characterVoice, voiceIndex, true);
        if (weaponIndex >= 0)
            Audio.PlayRemoteClip(Data.weaponSound, weaponIndex, false);
    }
}
