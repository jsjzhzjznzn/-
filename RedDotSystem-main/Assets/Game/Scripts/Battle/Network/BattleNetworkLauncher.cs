using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using Cysharp.Threading.Tasks;
using SkierFramework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

/// <summary>
/// 对战联机对象（NetworkManager + UnityTransport + RoomState）的创建/启动/销毁入口。
///
/// 移植自 compete 项目的"联机.prefab"流程，传输层由 UOS KCP 换成 NGO 1.13 自带的
/// UnityTransport，固定端口 7777，房主即服务端（Listen Server）。
/// </summary>
public static class BattleNetworkLauncher
{
    public const ushort DefaultPort = 7777;
    public const string DefaultAddress = "127.0.0.1";

    /// <summary>Resources 相对路径（联机预制体不在 YooAsset 收集范围内，直接走 Resources）</summary>
    private const string PrefabPath = "Battle/BattleNetworking";

    private static GameObject _root;

    public static GameObject Root => _root;
    public static NetworkManager NetworkManager => _root != null ? _root.GetComponent<NetworkManager>() : null;
    public static UnityTransport Transport => _root != null ? _root.GetComponent<UnityTransport>() : null;

    public static bool HasSession
    {
        get
        {
            var nm = NetworkManager;
            return nm != null && (nm.IsServer || nm.IsClient);
        }
    }

    public static bool IsHost => NetworkManager != null && NetworkManager.IsServer;
    public static bool IsConnectedClient => NetworkManager != null && NetworkManager.IsConnectedClient;

    /// <summary>确保联机对象存在（DDOL）。失败时返回 false 并给出原因。</summary>
    public static UniTask<string> EnsureInstanceAsync()
    {
        if (_root != null)
        {
            var existing = _root.GetComponent<NetworkManager>();
            if (existing != null)
            {
                return UniTask.FromResult<string>(null);
            }
            _root = null;
        }

        var prefab = Resources.Load<GameObject>(PrefabPath);
        if (prefab == null)
        {
            return UniTask.FromResult("找不到联机预制体 Resources/" + PrefabPath);
        }
        var go = UnityEngine.Object.Instantiate(prefab);

        _root = go;
        _root.name = "BattleNetworking";
        UnityEngine.Object.DontDestroyOnLoad(_root);

        if (_root.GetComponent<NetworkManager>() == null || _root.GetComponent<UnityTransport>() == null)
        {
            UnityEngine.Object.Destroy(_root);
            _root = null;
            return UniTask.FromResult("联机预制体缺少 NetworkManager 或 UnityTransport 组件");
        }
        return UniTask.FromResult<string>(null);
    }

    /// <summary>创建房间：本机作为服务端 + 本地客户端（Listen Server）。</summary>
    public static async UniTask<string> StartHostAsync()
    {
        string error = await EnsureInstanceAsync();
        if (!string.IsNullOrEmpty(error)) return error;

        var nm = NetworkManager;
        if (nm.IsServer || nm.IsClient)
        {
            return "联机会话已存在，请先退出当前房间";
        }

        var transport = Transport;
        if (transport != null)
        {
            transport.SetConnectionData(DefaultAddress, DefaultPort, "0.0.0.0");
        }

        if (!nm.StartHost())
        {
            ShutdownAndDestroy();
            return "StartHost 失败：端口可能被占用";
        }

        Debug.Log($"[BattleNetwork] 房间已创建（Host），端口 {DefaultPort}");
        return null;
    }

    /// <summary>加入房间：改连接地址后 StartClient。</summary>
    public static async UniTask<string> StartClientAsync(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return "主机地址为空";
        }
        string error = await EnsureInstanceAsync();
        if (!string.IsNullOrEmpty(error)) return error;

        var nm = NetworkManager;
        if (nm.IsServer || nm.IsClient)
        {
            return "联机会话已存在，请先退出当前房间";
        }

        var transport = Transport;
        if (transport == null)
        {
            ShutdownAndDestroy();
            return "联机对象缺少 UnityTransport";
        }

        transport.SetConnectionData(address.Trim(), DefaultPort);
        if (!nm.StartClient())
        {
            ShutdownAndDestroy();
            return $"StartClient 失败：{address}:{DefaultPort}";
        }

        Debug.Log($"[BattleNetwork] 正在加入房间 {address}:{DefaultPort}");
        return null;
    }

    /// <summary>
    /// 生成一次房间标识（邀请凭据的一部分，仅用于日志与取消归属）
    /// 不要求全局唯一：房间本身只活在房主进程里，重启即换新。
    /// </summary>
    public static string CreateRoomId()
    {
        return Guid.NewGuid().ToString("N").Substring(0, 12);
    }

    /// <summary>
    /// 取本机局域网 IPv4（邀请时自报给好友，好友用它直连 7777）
    ///
    /// 顺序：默认路由探测（UDP connect 不真正发包）→ 私网网卡 → 任意非回环 → 127.0.0.1。
    /// 没有网络的环境（离线开发机）会退到 127.0.0.1，同机双开仍然可用。
    /// </summary>
    public static string GetLocalIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is IPEndPoint endPoint && endPoint.Address != null)
            {
                string ip = endPoint.Address.ToString();
                if (!string.IsNullOrEmpty(ip)) return ip;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BattleNetwork] 默认路由探测 IP 失败：{e.Message}");
        }

        string fallback = null;
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    byte[] b = ua.Address.GetAddressBytes();
                    bool isPrivate = b[0] == 10
                        || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                        || (b[0] == 192 && b[1] == 168);
                    if (isPrivate) return ua.Address.ToString();
                    fallback ??= ua.Address.ToString();
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BattleNetwork] 遍历网卡取 IP 失败：{e.Message}");
        }
        return fallback ?? DefaultAddress;
    }

    /// <summary>关闭会话并销毁常驻联机对象。</summary>
    public static void ShutdownAndDestroy()
    {
        var nm = NetworkManager;
        if (nm != null)
        {
            try { nm.Shutdown(); }
            catch (System.Exception e) { Debug.LogWarning($"[BattleNetwork] Shutdown 异常：{e.Message}"); }
        }

        if (_root != null)
        {
            UnityEngine.Object.Destroy(_root);
            _root = null;
        }
    }
}
