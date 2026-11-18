using System;
using System.Collections.Generic;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;

namespace SurvivalLog.CraftQueue;

/// <summary>
/// 自动自测（Dev.SelfTest=true 时进档后自动跑一轮，跑完自禁用）：
/// 找一个背包现有材料可满足的配方 → 手工建任务入队（不走 UI）→ 观察制作推进 →
/// 完成后全容器取证 dump（成品真实去向）。产出全部入日志，零人工操作。
/// </summary>
internal static class SelfTest
{
    private static bool _done;
    private static float _startAt = -1f;
    private static int _productId;
    private static float _lastTrace;

    internal static void FastPoll()
    {
        if (_done) return;
        try
        {
            var world = GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.IsInstanceCreated
                ? GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.Instance : null;
            var am = world?._AgentManager;
            var agentList = am?.agentList;
            if (agentList == null || agentList.Count == 0) return;      // 未进档
            if (_startAt < 0) { _startAt = UnityEngine.Time.unscaledTime + 8f; return; }
            if (UnityEngine.Time.unscaledTime < _startAt) return;        // 进档稳定窗

            _done = true;                                                // 只跑一轮（后续 trace 由定时器）
            CraftQueuePlugin.Log.LogWarning("[CraftQueue.SelfTest] == 开始：环境 dump ==");
            var ttm = world._ToolTableManager;
            var map = ttm?.ToolTableMap;
            if (map == null) { CraftQueuePlugin.Log.LogWarning("[SelfTest] ToolTableMap 不可达"); return; }
            foreach (var kv in map)
                CraftQueuePlugin.Log.LogWarning($"[SelfTest] 工作台 furn=0x{kv.Key:X} state={kv.Value?.State}");

            // 全容器 dump
            DumpBags();

            // 找可做配方：材料可满足 + 非染料 + 非家具
            var cm = BaseSingleton<ConfigManager>.IsInstanceCreated ? BaseSingleton<ConfigManager>.Instance : null;
            var dict = cm?._Config_ProductionList_Dict;
            if (dict == null) { CraftQueuePlugin.Log.LogWarning("[SelfTest] 配方表不可达"); return; }
            var im = world._ItemManager;
            long bagOwner = 0;
            try { bagOwner = ActionDispatcher.GetLeadingRoleAgentId(); } catch { }
            foreach (var kv in dict)
            {
                var cfg = kv.Value;
                if (cfg?.MaterialList == null || cfg.MaterialList.Count == 0) continue;
                if (cfg.ProductID == null || cfg.ProductID.Count == 0) continue;
                bool ok = true;
                foreach (var m in cfg.MaterialList)
                {
                    bool dye = false;
                    try { dye = DyeConfig.IsDyeMaterial(m); } catch { }
                    if (dye) { ok = false; break; }
                }
                if (!ok) continue;
                var need = new Dictionary<int, int>();
                foreach (var m in cfg.MaterialList) { need.TryGetValue(m, out var n); need[m] = n + 1; }
                foreach (var nk in need)   // 全链计数（背包+柜子+抽屉，刨预占）——材料多在柜子
                {
                    if (MaterialPicker.CountAvailExcludingReserved(nk.Key) < nk.Value) { ok = false; break; }
                }
                if (!ok) continue;
                try { if (ToolTableManager.IsFurniturePackageItem(cfg.ProductID[0])) continue; } catch { }
                // 堆叠整件约束：需求 ≤ 实例堆数才行（PickFail.Partial 口径）——先按 PickAndReserve 实测
                var ids = MaterialPicker.PickAndReserve(cfg.MaterialList, out var fail);
                if (fail != PickFail.None || ids.Count == 0)
                {
                    CraftQueuePlugin.Log.LogWarning($"[SelfTest] 候选配方 #{kv.Key} 材料满足但取料失败 fail={fail}（换下一个）");
                    QueueState.ReservedItemIds.Clear();
                    continue;
                }
                _productId = cfg.ProductID[0];
                var t = new CraftTask
                {
                    RecipeKey = "cfg:" + kv.Key,
                    ProductName = "SelfTest#" + cfg.ProductID[0],
                    ProductIconId = _productId,
                    Left = 2, Total = 2,
                    Status = TaskStatus.Queued,
                };
                t.ReservedItemIds.AddRange(ids);
                QueueState.Add(t);
                QueueState.SyncReservedIds();
                CraftQueuePlugin.Log.LogWarning($"[SelfTest] 入队成功：配方 #{kv.Key} 成品 #{_productId} ×2（预占 {ids.Count} 件）——观察 90s");
                TraceLoop(90f);
                return;
            }
            CraftQueuePlugin.Log.LogWarning("[SelfTest] 背包现有材料找不到可做配方（堆叠约束后）——往背包放点基础材料再开 SelfTest");
        }
        catch (Exception e)
        {
            CraftQueuePlugin.Log.LogWarning("[CraftQueue.SelfTest] 异常: " + e);
        }
    }

    private static void TraceLoop(float seconds)
    {
        _tracing = true;
        _traceUntil = UnityEngine.Time.unscaledTime + seconds;
    }

    private static bool _tracing;
    private static float _traceUntil;

    internal static void TraceTick()
    {
        if (!_tracing || UnityEngine.Time.unscaledTime > _traceUntil) return;
        if (UnityEngine.Time.unscaledTime - _lastTrace < 2f) return;
        _lastTrace = UnityEngine.Time.unscaledTime;
        try
        {
            var world = GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.Instance;
            var map = world._ToolTableManager?.ToolTableMap;
            var sb = new System.Text.StringBuilder("tables:");
            if (map != null)
                foreach (var kv in map) sb.Append($" 0x{kv.Key:X}={kv.Value?.State}");
            sb.Append(" | queue:");
            foreach (var t in QueueState.Tasks) sb.Append($" #{t.TaskId}:{t.Status}:{t.Left}");
            CraftQueuePlugin.Log.LogWarning("[SelfTest]" + sb);
            if (QueueState.Tasks.Count == 0 && UnityEngine.Time.unscaledTime > _traceUntil - 80f)
            {
                _tracing = false;
                CraftQueuePlugin.Log.LogWarning("[SelfTest] 队列清空：成品取证 ↓");
                DumpProduct(_productId);
                OpenToolTableUi();            // 队列层验证完：开工作台 UI 供 CDP 驱动 UI 层验证
            }
        }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[SelfTest] trace 异常: " + e.Message); }
    }

    private static void DumpProduct(int pid)
    {
        try
        {
            var world = GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.Instance;
            var im = world._ItemManager;
            var rus = GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.ReduxUI.ReduxUISystem>.Instance;
            GameCore.HotUpdate.ReduxUI.BaseReduxState raw = null;
            rus?.reduxStoreLayer?.dataTree?.TryGetValue(
                Il2CppInterop.Runtime.Il2CppType.From(typeof(GameCore.HotUpdate.ReduxUI.State_Data_Item)), out raw);
            var bagCache = raw?.TryCast<GameCore.HotUpdate.ReduxUI.State_Data_Item>()?.BagCache;
            if (bagCache == null) { CraftQueuePlugin.Log.LogWarning("[SelfTest] BagCache 不可达"); return; }
            int owners = 0, hits = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in bagCache)
            {
                owners++;
                var lst = im?.GetItemDataList(kv.Key);
                if (lst == null) continue;
                for (int i = 0; i < lst.Count; i++)
                {
                    try
                    {
                        if (lst[i].ItemConfigId != pid) continue;
                        hits++;
                        if (sb.Length < 500) sb.Append($" [owner=0x{kv.Key:X} bag={kv.Value} id=0x{lst[i].InstanceId:X} cnt={lst[i].ItemCount}]");
                    }
                    catch { }
                }
            }
            CraftQueuePlugin.Log.LogWarning($"[SelfTest][成品取证] #{pid}: 扫 {owners} 容器，命中 {hits}。{sb}");
        }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[SelfTest] 取证异常: " + e.Message); }
    }

    /// <summary>官方 action 直开工作台 UI（等同走到台子交互；参数最小构造）。</summary>
    private static void OpenToolTableUi()
    {
        try
        {
            var world = GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.Instance;
            var map = world._ToolTableManager?.ToolTableMap;
            if (map == null) return;
            long furn = 0;
            foreach (var kv in map) { furn = kv.Key; break; }
            if (furn == 0) { CraftQueuePlugin.Log.LogWarning("[SelfTest] 无工作台可开"); return; }
            var bags = new Il2CppSystem.Collections.Generic.List<GameCore.HotUpdate.ReduxUI.Data_Bag>();
            var cfgIds = new Il2CppSystem.Collections.Generic.List<int>();
            GameCore.HotUpdate.ReduxUI.Ac_ToolTable_Open.SendAction(
                bags, GameCore.HotUpdate.HandMadeState.Idle, furn, 1, bags, cfgIds);
            // 最小构造不填 WorkbenchOwnerId（真实玩家路径官方会填）——自测补写：BagCache 里 bagId==1010 的 owner
            var st = QueueUiBenchState();
            var benchOwner = FindBenchBagOwner();
            if (st?.WorkbenchOwnerId != null && benchOwner != 0)
                st.WorkbenchOwnerId.Value = benchOwner;
            CraftQueuePlugin.Log.LogWarning($"[SelfTest] 工作台 UI 已请求打开 furn=0x{furn:X} benchOwner=0x{benchOwner:X}（CDP 接管 UI 验证）");
        }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[SelfTest] 开 UI 异常: " + e.Message); }
    }

    private static GameCore.HotUpdate.ReduxUI.State_Web_ToolTable QueueUiBenchState()
    {
        try { return QueueUi.ToolTableStatePublic(); } catch { return null; }
    }

    /// <summary>BagCache 里第一个 bagId==1010（工作台备料格）的 owner。</summary>
    private static long FindBenchBagOwner()
    {
        try
        {
            var rus = GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.ReduxUI.ReduxUISystem>.Instance;
            GameCore.HotUpdate.ReduxUI.BaseReduxState raw = null;
            rus?.reduxStoreLayer?.dataTree?.TryGetValue(
                Il2CppInterop.Runtime.Il2CppType.From(typeof(GameCore.HotUpdate.ReduxUI.State_Data_Item)), out raw);
            var bagCache = raw?.TryCast<GameCore.HotUpdate.ReduxUI.State_Data_Item>()?.BagCache;
            if (bagCache == null) return 0;
            foreach (var kv in bagCache)
                if (kv.Value == 1010) return kv.Key;
        }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[SelfTest] 找台面 owner 异常: " + e.Message); }
        return 0;
    }

    private static void DumpBags()
    {
        try
        {
            var world = GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.Instance;
            var im = world._ItemManager;
            var rus = GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.ReduxUI.ReduxUISystem>.Instance;
            GameCore.HotUpdate.ReduxUI.BaseReduxState raw = null;
            rus?.reduxStoreLayer?.dataTree?.TryGetValue(
                Il2CppInterop.Runtime.Il2CppType.From(typeof(GameCore.HotUpdate.ReduxUI.State_Data_Item)), out raw);
            var bagCache = raw?.TryCast<GameCore.HotUpdate.ReduxUI.State_Data_Item>()?.BagCache;
            if (bagCache == null) { CraftQueuePlugin.Log.LogWarning("[SelfTest] BagCache 不可达"); return; }
            foreach (var kv in bagCache)
            {
                var lst = im?.GetItemDataList(kv.Key);
                var n = lst?.Count ?? -1;
                var head = new System.Text.StringBuilder();
                if (lst != null)
                    for (int i = 0; i < lst.Count && i < 3; i++)
                        try { head.Append($" #{lst[i].ItemConfigId}x{lst[i].ItemCount}"); } catch { }
                CraftQueuePlugin.Log.LogWarning($"[SelfTest][bag] owner=0x{kv.Key:X} bagId={kv.Value} items={n}{head}");
            }
        }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[SelfTest] dump 异常: " + e.Message); }
    }
}
