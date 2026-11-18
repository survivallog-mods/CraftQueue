using System;
using System.Collections.Generic;
using System.Linq;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;

namespace SurvivalLog.CraftQueue;

/// <summary>
/// 串行队列 runner（spec §5.3-5.5）：0.5s 节流轮询，一次派发一件。
///
/// 取证结论（spec §3 + 2026-11-18 实现期取证）：
/// - 计时=官方 ToolTableManager.Update（BattleLogicWorld 每帧）；mod 只轮询状态机。
/// - TryCreateItem 内部自写 ToolTableData.State：完成=2(WaitingReceive)、失败分支=3(NoReceive)
///   （反汇编 [rax+0x18] 写点实证）→ 完成检测=轮询 State≥WaitingReceive。
/// - 复位路径（风险点①①b Ruling）：官方 GetProduct/ForceEndProduction 均只 TryGetValue+notify，
///   复位逻辑在 notify 订阅方（UI 层）——队列侧等价复位=直写 ToolTableData.State=0(Idle)
///   （Il2CppInterop 生成程序集私有成员可达，CabinetEverywhere 多处先例）。CachedItemIds 同步清空。
/// - StartProduction 为 void：拒绝信号=下一轮 State 仍非 Making；用 GetCraftStamina 区分
///   精力不足（挂起）与其他校验失败（Blocked）。
/// - 工作台清单=ToolTableMap.Keys（家具 OnInit 经 RegisterToolTable 注册，无需自扫）。
/// </summary>
internal static class QueueRunner
{
    private static float _nextPoll;
    private static long _busyFurnId;          // 当前派发到的家具（0=无在制）
    private static int _dispatchTick;         // 拒绝判定窗口计数

    private static BattleLogicWorld World()
    {
        if (!BaseSingleton<BattleLogicWorld>.IsInstanceCreated) return null;
        try { return BaseSingleton<BattleLogicWorld>.Instance; } catch { return null; }
    }
    private static ToolTableManager TTM() => World()?._ToolTableManager;
    private static ItemManager IM() => World()?._ItemManager;
    private static DropItemManager DIM() => World()?._DropItemManager;
    private static ConfigManager CM()
    {
        if (!BaseSingleton<ConfigManager>.IsInstanceCreated) return null;
        try { return BaseSingleton<ConfigManager>.Instance; } catch { return null; }
    }

    private static long DrawerOwner()
    {
        try { return WorkbenchDrawerConfig.DrawerOwnerId; } catch { return 0; }
    }

    /// <summary>帧入口（QueueTicker.Update 每帧调，内部 0.5s 节流）。</summary>
    internal static void FastPoll()
    {
        if (UnityEngine.Time.unscaledTime < _nextPoll) return;
        _nextPoll = UnityEngine.Time.unscaledTime + 0.5f;
        try { Poll(); }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Runner] 轮询异常: " + e.Message); }
    }

    private static void Poll()
    {
        if (TTM() == null) return;
        RefreshRuntimeFields();

        // ① 在制任务收尾检测（Review Focus 1：只认自己派发的 _busyFurnId，官方/前台任务不碰）
        var busy = QueueState.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Dispatched);
        if (busy != null)
        {
            CollectIfFinished(busy);
            return;                              // 串行：在制时不派发
        }
        if (_busyFurnId != 0) return;            // 拒绝判定窗口（见 CollectIfFinished）

        // ② 精力挂起：恢复检测
        if (QueueState.StaminaPaused)
        {
            TryRecoverStamina();
            return;
        }
        // ③ Blocked 任务补料自动恢复（先于新派发，保持队首语义）
        var blocked = QueueState.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Blocked);
        if (blocked != null && TryRecoverBlocked(blocked)) return;

        // ④ 派发队首
        var next = QueueState.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Queued);
        if (next != null) TryDispatch(next);
    }

    /// <summary>刷新快照运行字段（精力/抽屉占用/档位容量）。</summary>
    private static void RefreshRuntimeFields()
    {
        try
        {
            var ttm = TTM();
            ttm.GetCraftStamina(out var cur, out _);
            QueueState.Stamina = cur;
            if (cur > QueueState.StaminaMax) QueueState.StaminaMax = cur;   // 满值校准（精力≤上限）
            var im = IM();
            var items = im?.GetItemDataList(DrawerOwner());
            QueueState.DrawerUsed = items?.Count ?? 0;
            // 抽屉容量 v1 固定最小档 120（5010=120/5012=132/5013=144；档位运行时读取路径
            // GetDrawerBagId 归属类不明，见 ledger Ruling，保守取最小档只影响高档位提前满判定）
        }
        catch { }
    }

    // ── 入队（QueueUi 调）────────────────────────────────────────────

    internal static bool Enqueue(int recipeId, int qty, out string failMsg)
    {
        failMsg = "";
        qty = Math.Clamp(qty, 1, 99);
        var cfg = MaterialPicker.ProductionCfg(recipeId);
        if (cfg == null) { failMsg = "配方不存在"; return false; }

        var ids = MaterialPicker.PickAndReserve(recipeId, out var fail);
        if (fail != PickFail.None || ids.Count == 0)
        {
            failMsg = fail switch
            {
                PickFail.Shortage => "材料不足（背包/柜子/抽屉可用量不够）",
                PickFail.DrawerFull => "工作台抽屉放不下预占材料",
                PickFail.Variant => "含染料变体的配方暂不支持队列",
                PickFail.NoRecipe => "配方不存在",
                _ => "取料失败",
            };
            return false;
        }

        string name = ""; int icon = 0; bool isFurn = false;
        try
        {
            var pid = cfg.ProductID != null && cfg.ProductID.Count > 0 ? cfg.ProductID[0] : 0;
            if (pid > 0)
            {
                icon = pid;
                isFurn = ToolTableManager.IsFurniturePackageItem(pid);
                var item = CM().Get_Config_Item(pid);
                if (item != null)
                {
                    name = item.ItemName_Local;
                    if (string.IsNullOrEmpty(name)) name = item.ItemName;
                }
            }
        }
        catch { }

        var t = new CraftTask
        {
            RecipeId = recipeId,
            ProductName = string.IsNullOrEmpty(name) ? ("配方#" + recipeId) : name,
            ProductIconId = icon,
            Left = qty,
            Total = qty,
            IsFurniture = isFurn,
            Status = TaskStatus.Queued,
        };
        t.ReservedItemIds.AddRange(ids);
        QueueState.Add(t);
        QueueState.SyncReservedIds();
        if (CraftQueuePlugin.CfgVerbose.Value)
            CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 入队 #{t.TaskId} {t.ProductName}×{qty} 预占 {ids.Count} 件物品");
        return true;
    }

    // ── 取消（QueueUi 调）────────────────────────────────────────────

    internal static bool Cancel(int taskId, out string failMsg)
    {
        failMsg = "";
        var t = QueueState.Find(taskId);
        if (t == null) { failMsg = "任务不存在"; return false; }
        if (t.Status == TaskStatus.Dispatched)
        {
            var furn = _busyFurnId;
            try { TTM()?.ForceEndProduction(furn); } catch { }
            ResetFurnState(furn);                 // 官方 notify 链复位在 UI 层，队列侧直写等价复位
            _busyFurnId = 0;
            CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 取消制作中 #{t.TaskId}（ForceEnd+复位，料留抽屉）");
        }
        t.ReservedItemIds.Clear();                // 预占解除，料原地留抽屉（spec §5.5）
        QueueState.Remove(taskId);
        return true;
    }

    // ── 派发 ─────────────────────────────────────────────────────────

    private static void TryDispatch(CraftTask t)
    {
        var drawerOwner = DrawerOwner();
        var im = IM();
        // 预占校验：物品仍在抽屉（被手动/前台拿走→Blocked）
        bool lost = false;
        foreach (var id in t.ReservedItemIds)
        {
            var it = im?.GetItemData(id);
            if (it == null || it.OwnerId != drawerOwner) { lost = true; break; }
        }
        if (lost)
        {
            t.Status = TaskStatus.Blocked;
            t.BlockedReason = "预占材料被取走";
            t.ReservedItemIds.Clear();            // 预占已废（料不在抽屉）
            QueueState.SyncReservedIds();
            if (CraftQueuePlugin.CfgVerbose.Value)
                CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] #{t.TaskId} 预占物品丢失 → Blocked");
            return;
        }
        var furn = FindIdleTable();
        if (furn == 0) return;                    // 无空闲台：等待（台子被拆自然落此分支）
        var ttm = TTM();
        ttm.StartProduction(furn, ToIl2Cpp(t.ReservedItemIds));
        _busyFurnId = furn;
        _dispatchTick = 0;
        t.Status = TaskStatus.Dispatched;
        if (CraftQueuePlugin.CfgVerbose.Value)
            CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 派发 #{t.TaskId} → 家具 0x{furn:X}");
    }

    private static long FindIdleTable()
    {
        try
        {
            var map = TTM().ToolTableMap;
            if (map == null) return 0;
            foreach (var kv in map)
            {
                if (kv.Value == null) continue;
                if ((int)kv.Value.State != 0) continue;                 // 非 Idle=忙（含前台在制/没收成品）
                var bench = IM()?.GetItemDataList(kv.Key);
                if (bench != null && bench.Count > 0) continue;         // 台面有遗留物=前台没收，不占用
                return kv.Key;
            }
        }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Runner] 找空闲台异常: " + e.Message); }
        return 0;
    }

    // ── 收尾 ─────────────────────────────────────────────────────────

    private static void CollectIfFinished(CraftTask t)
    {
        var ttm = TTM();
        var state = ttm.GetToolTableState(_busyFurnId);
        if (state == HandMadeState.Making) return;                      // 仍在制作

        if (state == HandMadeState.Idle)
        {
            // 派发后 2 轮（1s）仍 Idle=StartProduction 被拒（void 无返回值）
            _dispatchTick++;
            if (_dispatchTick < 2) return;
            ttm.GetCraftStamina(out var cur, out var req);
            if (cur < req)
            {
                QueueState.StaminaPaused = true;
                t.Status = TaskStatus.Queued;
                _busyFurnId = 0;
                CraftQueuePlugin.Log.LogInfo("[CraftQueue.Runner] 精力不足，整队挂起");
            }
            else
            {
                t.Status = TaskStatus.Blocked;
                t.BlockedReason = "启动制作被拒（材料校验失败）";
                _busyFurnId = 0;
            }
            return;
        }

        // WaitingReceive / NoReceive：完成 → 成品收尾
        try { CollectProducts(_busyFurnId); }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Runner] 收尾异常: " + e.Message); }
        ResetFurnState(_busyFurnId);
        _busyFurnId = 0;

        t.ReservedItemIds.Clear();
        QueueState.SyncReservedIds();
        t.Left--;
        if (t.Left <= 0)
        {
            t.Status = TaskStatus.Done;
            QueueState.Remove(t.TaskId);
            CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 完成 #{t.TaskId} {t.ProductName}×{t.Total}");
        }
        else
        {
            var ids = MaterialPicker.PickAndReserve(t.RecipeId, out var fail);
            if (fail == PickFail.None && ids.Count > 0)
            {
                t.ReservedItemIds.AddRange(ids);
                QueueState.SyncReservedIds();
                t.Status = TaskStatus.Queued;
            }
            else
            {
                t.Status = TaskStatus.Blocked;
                t.BlockedReason = fail == PickFail.Variant ? "含染料变体" : "材料不足";
            }
        }
    }

    /// <summary>成品搬抽屉，装不下逐件落地（spec §5.4；家具包裹官方已送玄关、台面无物自然跳过）。</summary>
    private static void CollectProducts(long furnId)
    {
        var im = IM();
        var drawerOwner = DrawerOwner();
        var bench = im?.GetItemDataList(furnId);
        if (bench == null || bench.Count == 0) return;                  // 家具包裹：官方直送玄关
        var ids = new List<long>();
        for (int i = 0; i < bench.Count; i++)
        {
            long id = 0;
            try { id = bench[i].InstanceId; } catch { }
            if (id != 0) ids.Add(id);
        }
        if (ids.Count == 0) return;
        int moved = 0;
        try { moved = im.TryQuickMoveItems(ToIl2Cpp(ids), furnId, drawerOwner, true); } catch { }
        if (moved == ids.Count)
        {
            CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 成品入抽屉 ×{moved}");
            return;
        }
        // 装不下/半搬：残留逐件掉落地面（CollectBatchProducts 同款语义）
        var left = im.GetItemDataList(furnId);
        var dim = DIM();
        int dropped = 0;
        if (left != null)
        {
            for (int i = 0; i < left.Count; i++)
            {
                long id = 0;
                try { id = left[i].InstanceId; } catch { }
                if (id == 0) continue;
                try { if (dim.DropItemFromBag(id)) dropped++; } catch { }
            }
        }
        CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 抽屉装不下：入抽屉 {moved}，落地 {dropped}");
    }

    /// <summary>直写复位（Ruling 见类头）：State=Idle + 清预扣清单。</summary>
    private static void ResetFurnState(long furnId)
    {
        if (furnId == 0) return;
        try
        {
            var map = TTM().ToolTableMap;
            if (map != null && map.TryGetValue(furnId, out var data) && data != null)
            {
                data.State = 0;
                data.CachedItemIds?.Clear();
            }
        }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Runner] 复位异常: " + e.Message); }
    }

    // ── 挂起恢复 / 阻塞恢复 ──────────────────────────────────────────

    private static void TryRecoverStamina()
    {
        try
        {
            TTM().GetCraftStamina(out var cur, out var req);
            if (cur >= req)
            {
                QueueState.StaminaPaused = false;
                CraftQueuePlugin.Log.LogInfo("[CraftQueue.Runner] 精力恢复，队列续跑");
            }
        }
        catch { }
    }

    private static bool TryRecoverBlocked(CraftTask t)
    {
        t.ReservedItemIds.Clear();
        var ids = MaterialPicker.PickAndReserve(t.RecipeId, out var fail);
        if (fail != PickFail.None || ids.Count == 0) return false;
        t.ReservedItemIds.AddRange(ids);
        t.BlockedReason = "";
        t.Status = TaskStatus.Queued;
        QueueState.SyncReservedIds();
        CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] #{t.TaskId} 补料恢复排队");
        return true;
    }

    private static Il2CppSystem.Collections.Generic.List<long> ToIl2Cpp(List<long> src)
    {
        var dst = new Il2CppSystem.Collections.Generic.List<long>(src.Count);
        foreach (var v in src) dst.Add(v);
        return dst;
    }
}
