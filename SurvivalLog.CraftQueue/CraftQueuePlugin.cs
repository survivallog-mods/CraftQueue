using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SurvivalLog.CraftQueue;

/// <summary>
/// 工作台制作队列（独立 mod）v0.1.0。
///
/// 功能：工作台「加入队列」→ 入队即锁料进工作台抽屉（预占集合防超发）→ 关界面后台串行制作
/// （官方 ToolTableManager.Update 原生异步计时）→ 成品自动入抽屉，满则逐件落地；家具包裹走
/// 官方原路送玄关；精力不足整队挂起恢复自动续跑；官方前台制作被预占拦截（E 方案）。
///
/// 设计文档（权威，含全部 RVA 取证与取舍记录）：
/// docs/superpowers/specs/2026-11-16-craft-queue-design.md
/// 实现计划：docs/superpowers/plans/2026-11-18-craft-queue.md
/// 红线：不改存档、不改游戏文件、不动官方前台制作行为；队列与预占集合均为会话内存。
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public sealed class CraftQueuePlugin : BasePlugin
{
    public const string Guid = "com.local.survivallog.craftqueue";
    public const string Name = "CraftQueue";
    public const string Version = "0.1.0";

    internal static new ManualLogSource Log;
    internal static ConfigEntry<bool> CfgEnabled;
    internal static ConfigEntry<bool> CfgVerbose;
    internal static ConfigEntry<bool> CfgSelfTest;

    public override void Load()
    {
        Log = base.Log;

        CfgEnabled = Config.Bind("General", "Enabled", true,
            "总开关：false = 不注册帧循环且零日志零行为，等效卸载");
        CfgVerbose = Config.Bind("Dev", "VerboseLog", false,
            "调试：打印取料/派发/收尾详细日志");
        CfgSelfTest = Config.Bind("Dev", "SelfTest", false,
            "自测：进档后自动找可做配方入队×2 并全容器取证（跑完一轮自动失效，需重开）");

        if (!CfgEnabled.Value)
        {
            Log.LogInfo("[CraftQueue] Enabled=false，本会话不加载");
            return;
        }

        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<QueueTicker>())
            ClassInjector.RegisterTypeInIl2Cpp<QueueTicker>();
        var go = new GameObject("CraftQueue.Ticker");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<QueueTicker>();
        CraftGate.Install();

        Log.LogInfo("[CraftQueue] ticker ready");
    }
}

/// <summary>帧循环宿主：后续任务把 FastPoll 链挂进 Update（AccessTicker 同款模式）。</summary>
internal sealed class QueueTicker : MonoBehaviour
{
    public QueueTicker(IntPtr ptr) : base(ptr) { }
    public QueueTicker() { }

    public void Update()
    {
        QueueRunner.FastPoll();
        QueueUi.FastPoll();
        if (CraftQueuePlugin.CfgSelfTest.Value) { SelfTest.FastPoll(); SelfTest.TraceTick(); }
    }
}
