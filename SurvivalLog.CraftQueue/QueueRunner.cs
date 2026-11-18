using System;
using System.Collections.Generic;
using System.Linq;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using GameCore.HotUpdate.ReduxUI;

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
    private static float _drawerCountAt = -999f;
    private static float _resumeAt;                          // 完成→下一件派发的错峰时刻
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

    private static BattleLogicWorld _worldRef;          // 世界重绑检测（终审 I7：读档/切档清队列）

    private static void Poll()
    {
        var world = World();
        if (world == null) return;
        if (!ReferenceEquals(world, _worldRef))
        {
            // 新 BattleLogicWorld 实例=读档/切档：队列/预占/在制记录全部失效（spec §5.8 会话语义）
            _worldRef = world;
            if (QueueState.Tasks.Count > 0 || _busyFurnId != 0)
                CraftQueuePlugin.Log.LogInfo("[CraftQueue.Runner] 世界重建：队列清空（抽屉材料保留）");
            QueueState.Tasks.Clear();
            QueueState.ReservedItemIds.Clear();
            QueueState.StaminaPaused = false;
            QueueState.JustFinishedTaskId = 0;
            _busyFurnId = 0;
        }
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
        if (UnityEngine.Time.unscaledTime < _resumeAt) return;   // 完成错峰窗口
        var next = QueueState.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Queued);
        if (next != null) TryDispatch(next);
    }

    /// <summary>刷新快照运行字段（精力/抽屉占用/档位容量）。</summary>
    private static void RefreshRuntimeFields()
    {
        try
        {
            var ttm = TTM();
            if (ttm.GetCraftStamina(out var cur, out var req))             // 终审 I22：false 不更新
            {
                QueueState.Stamina = cur;
                if (req > 0) QueueState.CanMake = (int)(cur / req);
                if (cur > QueueState.StaminaMax) QueueState.StaminaMax = cur;   // 满值校准（精力≤上限）
            }
            var im = IM();
            // 抽屉计数降频（5s）：GetItemDataList 非零开销，占用变化慢；精力保持 0.5s
            if (UnityEngine.Time.unscaledTime - _drawerCountAt > 5f)
            {
                _drawerCountAt = UnityEngine.Time.unscaledTime;
                var items = im?.GetItemDataList(DrawerOwner());
                QueueState.DrawerUsed = items?.Count ?? 0;
            }
            // 抽屉容量 v1 固定最小档 120（5010=120/5012=132/5013=144；档位运行时读取路径
            // GetDrawerBagId 归属类不明，见 ledger Ruling，保守取最小档只影响高档位提前满判定）
        }
        catch { }
    }

    // ── 入队（QueueUi 调）────────────────────────────────────────────

    /// <summary>
    /// 入队（官方心智：台面有料即可做）。第一件=台面现料搬进抽屉预占；后续件按配方表取料。
    /// 台面=当前打开的工作台（CurrentFurnitureId）；配方=官方 GetProductionByMaterials 反推。
    /// </summary>
    internal static bool Enqueue(int qty, out string failMsg)
    {
        failMsg = "";
        qty = Math.Clamp(qty, 1, 99);
        var im = IM();
        var drawerOwner = DrawerOwner();
        // 台面（备料格 bag 1010）owner=独立 id（官方 State_Web_ToolTable.WorkbenchOwnerId），
        // 非家具 id——家具 id 查台面恒空（实机 CDP+日志联证）
        long benchOwner = CraftGate.BenchOwner();
        if (benchOwner == 0) { failMsg = "工作台未打开"; return false; }
        Il2CppSystem.Collections.Generic.List<ItemData> bench = null;
        try { bench = im.GetItemDataList(benchOwner); } catch { }
        if (bench == null || bench.Count == 0) { failMsg = "请先把材料放上工作台（点配方或拖材料）"; return false; }

        var benchIds = new List<long>();
        var matIds = new Il2CppSystem.Collections.Generic.List<int>();
        for (int i = 0; i < bench.Count; i++)
        {
            long id = 0; int cid = 0, cnt = 0;
            try { id = bench[i].InstanceId; cid = bench[i].ItemConfigId; cnt = bench[i].ItemCount; } catch { continue; }
            if (id == 0) continue;
            benchIds.Add(id);
            for (int k = 0; k < cnt; k++) matIds.Add(cid);
        }
        if (matIds.Count == 0) { failMsg = "请先把材料放上工作台"; return false; }

        Config_ProductionList cfg = null;
        try { cfg = CM().GetProductionByMaterials(matIds)?.Item1; } catch { }
        if (cfg == null)
        {
            var dbg = new System.Text.StringBuilder();
            for (int i = 0; i < matIds.Count && i < 12; i++) dbg.Append(' ').Append(matIds[i]);
            CraftQueuePlugin.Log.LogWarning($"[CraftQueue.Runner] 台面材料不构成配方（{matIds.Count} 件）：{dbg}");
            failMsg = "台面上的材料不构成配方";
            return false;
        }

        // 台面现料搬进抽屉=第一件预占；搬运不完整=抽屉满，放弃
        int moved = 0;
        try { moved = im.TryQuickMoveItems(ToIl2Cpp(benchIds), benchOwner, drawerOwner, true); } catch { }
        if (moved != benchIds.Count) { failMsg = "工作台抽屉放不下材料"; return false; }

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
            RecipeKey = "cfg:" + cfg.ID,        // 后续件取料按配方表
            ProductName = string.IsNullOrEmpty(name) ? "配方" : name,
            ProductIconId = icon,
            Left = qty,
            Total = qty,
            IsFurniture = isFurn,
            Status = TaskStatus.Queued,
        };
        t.ReservedItemIds.AddRange(benchIds);
        QueueState.Add(t);
        QueueState.SyncReservedIds();
        if (CraftQueuePlugin.CfgVerbose.Value)
            CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 入队 #{t.TaskId} {t.ProductName}×{qty}（台面现料 {benchIds.Count} 件已锁抽屉）");
        return true;
    }

    /// <summary>后续件/恢复件取料：RecipeKey="cfg:ID" → 配方表 MaterialList。</summary>
    private static Il2CppSystem.Collections.Generic.List<int> MatsOf(CraftTask t)
    {
        try
        {
            int cfgId = 0;
            if (t.RecipeKey != null && t.RecipeKey.StartsWith("cfg:") && int.TryParse(t.RecipeKey.Substring(4), out cfgId))
            {
                var cfg = CM()?.Get_Config_ProductionList(cfgId);
                if (cfg?.MaterialList != null) return cfg.MaterialList;
            }
        }
        catch { }
        return null;
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
            long openFurn = QueueUi.CurrentToolTableFurnitureId();
            foreach (var kv in map)
            {
                if (kv.Value == null) continue;
                if (kv.Key == openFurn) continue;                       // 避开当前打开的台：官方 UI 对当前台 Making 弹制作遮罩并锁界面——队列走别的台（单台=关界面后开工）
                if ((int)kv.Value.State != 0) continue;                 // 非 Idle=忙（含前台在制/没收成品）
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

        // WaitingReceive：正常完成收尾；NoReceive：产出接收异常（语义未实证）——照搬台面物品但任务挂起（终审 I13）
        // 计时日志：完成卡顿定位（>300ms 的段直接入日志）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ResetFurnState(_busyFurnId);
        if (sw.ElapsedMilliseconds > 300) CraftQueuePlugin.Log.LogWarning($"[CraftQueue.Runner] 收尾耗时 复位={sw.ElapsedMilliseconds}ms");
        var swp = System.Diagnostics.Stopwatch.StartNew();
        try { CollectProducts(t, _busyFurnId); }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Runner] 收尾异常: " + e.Message); }
        if (swp.ElapsedMilliseconds > 300) CraftQueuePlugin.Log.LogWarning($"[CraftQueue.Runner] 收尾耗时 成品搬运={swp.ElapsedMilliseconds}ms");
        _busyFurnId = 0;

        t.ReservedItemIds.Clear();
        QueueState.SyncReservedIds();
        bool bad = state == HandMadeState.NoReceive;
        if (bad)
        {
            t.Status = TaskStatus.Blocked;
            t.BlockedReason = "产出接收异常（NoReceive）";
            _busyFurnId = 0;
            return;
        }
        t.Left--;
        QueueState.JustFinishedTaskId = t.TaskId;            // js 据此自动清官方成品弹窗（终审 I4/I5）
        _resumeAt = UnityEngine.Time.unscaledTime + 1.5f;    // 错峰：给官方完成链（统计/notify）让路，治连续卡顿
        if (t.Left <= 0)
        {
            t.Status = TaskStatus.Done;
            QueueState.Remove(t.TaskId);
            CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 完成 #{t.TaskId} {t.ProductName}×{t.Total}");
        }
        else
        {
            var ids = MaterialPicker.PickAndReserve(MatsOf(t), out var fail);
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

    /// <summary>
    /// 成品收尾（2026-11-19 全容器取证定案）：官方 TryCreateItem 的成品进玩家背包（全局 owner），
    /// 随后游戏机器人归置会把它搬进柜子/抽屉（按标签）。队列语义=尽量进抽屉：
    /// ① 完成后 0.5s 轮询窗口内从背包抢搬抽屉；② 抽屉装不下→逐件掉落地面；
    /// ③ 背包已没有=机器人已归置→接受，日志说明。
    /// （旧逻辑查 furnId 台面/全容器扫描均废弃——成品不经台面。）
    /// </summary>
    private static void CollectProducts(CraftTask t, long furnId)
    {
        if (t.IsFurniture || t.ProductIconId <= 0) return;      // 家具包裹：官方直送玄关
        var im = IM();
        var drawerOwner = DrawerOwner();
        long bagOwner = 0;
        try { bagOwner = ActionDispatcher.GetLeadingRoleAgentId(); } catch { }
        if (bagOwner == 0 || im == null) return;

        var bagList = im.GetItemDataList(bagOwner);
        var found = new List<long>();
        if (bagList != null)
        {
            for (int i = 0; i < bagList.Count; i++)
            {
                try { if (bagList[i].ItemConfigId == t.ProductIconId) found.Add(bagList[i].InstanceId); } catch { }
            }
        }
        if (found.Count == 0)
        {
            CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 成品 #{t.ProductIconId} 不在背包——已被游戏归置（机器人/其他），接受该去向");
            return;
        }
        int moved = 0;
        try { moved = im.TryQuickMoveItems(ToIl2Cpp(found), bagOwner, drawerOwner, true); } catch { }
        if (moved == found.Count)
        {
            CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 成品入抽屉 ×{moved}");
            return;
        }
        var leftList = im.GetItemDataList(bagOwner);
        var dropIds = new List<long>();
        if (leftList != null)
        {
            for (int i = 0; i < leftList.Count; i++)
            {
                try { if (leftList[i].ItemConfigId == t.ProductIconId) dropIds.Add(leftList[i].InstanceId); } catch { }
            }
        }
        var dim = DIM();
        int dropped = 0;
        foreach (var id in dropIds)
            try { if (dim.DropItemFromBag(id)) dropped++; } catch { }
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
        var ids = MaterialPicker.PickAndReserve(MatsOf(t), out var fail);
        if (fail != PickFail.None || ids.Count == 0) return false;
        t.ReservedItemIds.AddRange(ids);
        t.BlockedReason = "";
        t.Status = TaskStatus.Queued;
        QueueState.SyncReservedIds();
        CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] #{t.TaskId} 补料恢复排队");
        return true;
    }

    /// <summary>按材料需求反查配方（MaterialList 计数一致；成品名/图标/家具标注，非关键路径）。</summary>
    private static Config_ProductionList FindCfgByMaterials(Il2CppSystem.Collections.Generic.List<int> mats)
    {
        try
        {
            var dict = CM()?._Config_ProductionList_Dict;
            if (dict == null || mats == null) return null;
            var need = new Dictionary<int, int>();
            for (int i = 0; i < mats.Count; i++) { need.TryGetValue(mats[i], out var n); need[mats[i]] = n + 1; }
            foreach (var kv in dict)
            {
                var ml = kv.Value?.MaterialList;
                if (ml == null || ml.Count != mats.Count) continue;
                var cnt = new Dictionary<int, int>();
                foreach (var m in ml) { cnt.TryGetValue(m, out var n); cnt[m] = n + 1; }
                bool same = cnt.Count == need.Count;
                if (same)
                    foreach (var nk in need) { cnt.TryGetValue(nk.Key, out var v); if (v != nk.Value) { same = false; break; } }
                if (same) return kv.Value;
            }
        }
        catch { }
        return null;
    }

    private static Il2CppSystem.Collections.Generic.List<long> ToIl2Cpp(List<long> src)
    {
        var dst = new Il2CppSystem.Collections.Generic.List<long>(src.Count);
        foreach (var v in src) dst.Add(v);
        return dst;
    }
}
