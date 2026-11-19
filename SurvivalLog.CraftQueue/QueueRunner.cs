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
    private static float _lastPauseProbe = -999f;
    private static float _resumeAt;                          // 完成→下一件派发的错峰时刻
    private static Dictionary<long, long> _preProductIds;    // 派发前成品 id 快照（完成 diff 用）
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

    private static float _lastQueueActivityAt = -999f;    // 队列最近活动（派发/完成）——同帧抵消的作用域
    private static BattleLogicWorld _worldRef;            // 世界重绑检测（读档/切档清队列）
    /// <summary>
    /// 同帧抵消官方完成冻结（终案）：TryCreateItem 完成时内部调 BLW.OnPause() 冻结世界（前台
    /// 「弹窗等收下」语义，xref 0x28CFCFC 实证）——队列台完成不该冻玩家。官方 Update 链先跑、
    /// 本 Ticker 同帧后跑：满足条件即在下一帧渲染前调官方 OnResume()（完整恢复路径），暂停
    /// 从未存活到可感知。判定与弹窗拦截同款：仅「暂停栈含 ToolTableManager + 队列活跃窗口 +
    /// 当前台非玩家前台事件（非 Making/WaitingReceive）」——玩家前台完成冻结（官方预期）、
    /// 愿望/结算冻结（WishManager/GameOver 同名调用）一概不碰。
    /// </summary>
    /// <summary>帧入口（QueueTicker.Update 每帧调：同帧抵消 + 帧级完成检测 + 0.5s 节流轮询）。</summary>
    internal static void FastPoll()
    {
        // 队列在制=活跃（2026-11-19 实证修复）：官方完成冻结可能发生在 Dispatched 全程任意时刻
        //（UI 开着时官方完成走「弹窗等收下」路径必 OnPause，两轮实机日志 True×N 实证）——
        // 原判定只认派发/完成瞬间 2s 窗，单件制作 >2s 后完成帧窗口必过期 → 同帧抵消从未生效。
        // 在制期间持续刷新活跃时间戳，窗口覆盖整个制作期（栈过滤 ToolTableManager 源不变，第二道保险仍在）。
        try
        {
            for (int i = 0; i < QueueState.Tasks.Count; i++)
                if (QueueState.Tasks[i].Status == TaskStatus.Dispatched)
                {
                    _lastQueueActivityAt = UnityEngine.Time.unscaledTime;
                    break;
                }
        }
        catch { }
        NeutralizePauseTick();                              // 同帧抵消官方完成冻结（每帧，先于一切）
        // 帧级快通道：在制任务完成检测（完成帧立即收尾，不等 0.5s 门闩）
        if (_busyFurnId != 0)
        {
            if (UnityEngine.Time.unscaledTime - _lastPauseProbe > 2f)
            {
                _lastPauseProbe = UnityEngine.Time.unscaledTime;
                try { CraftQueuePlugin.Log.LogWarning($"[CraftQueue.Runner][探针2] 制作中：IsPause={World()?.IsPause} timeScale={UnityEngine.Time.timeScale}"); } catch { }
            }
            try
            {
                var busy = QueueState.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Dispatched);
                if (busy != null)
                {
                    var st = TTM().GetToolTableState(_busyFurnId);
                    if (st != HandMadeState.Making && st != HandMadeState.Idle)
                    {
                        var sw0 = System.Diagnostics.Stopwatch.StartNew();
                        Poll();
                        sw0.Stop();
                        CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner][探针] 完成帧：mod 收尾 {sw0.ElapsedMilliseconds}ms，本帧 dt={UnityEngine.Time.unscaledDeltaTime * 1000f:F0}ms");
                        return;
                    }
                }
            }
            catch { }
        }
        if (UnityEngine.Time.unscaledTime < _nextPoll) return;
        _nextPoll = UnityEngine.Time.unscaledTime + 0.5f;
        try { Poll(); }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Runner] 轮询异常: " + e.Message); }
    }

    private static void NeutralizePauseTick()
    {
        try
        {
            var w = World();
            if (w == null || !w.IsPause) return;
            if (UnityEngine.Time.unscaledTime - _lastQueueActivityAt > 2f) return;   // 队列不活跃不碰
            var stack = w._lastPauseStack;
            if (stack == null || !stack.Contains("ToolTableManager")) return;        // 只认制作完成源
            var st = QueueUi.ToolTableStatePublic();
            var furn = st?.CurrentFurnitureId ?? 0;
            var cur = HandMadeState.Idle;
            if (furn != 0 && w._ToolTableManager != null)
                cur = w._ToolTableManager.GetToolTableState(furn);
            // 玩家前台完成/失败态保护（审查修：Making 期间 TTM 源暂停必来自队列台完成——玩家台
            // 在制时不存在「玩家自己的完成冻结」；原 Making 子句会把 B 完成的冻结误判为玩家事件
            // → 冤枉冻结到官方超时（主用例：前台自己做+队列后台跑））
            if (cur == HandMadeState.WaitingReceive || cur == HandMadeState.NoReceive) return;
            w.OnResume();
            if (CraftQueuePlugin.CfgVerbose.Value)
                CraftQueuePlugin.Log.LogInfo("[CraftQueue.Runner] 官方完成冻结已同帧抵消");
        }
        catch { }
    }

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
            QueueState.JustDoneAt = -999f;
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
        // ③ Blocked 任务恢复（先于新派发）：仅缺料型走补料重试；无台型等下一轮自然重试派发
        //（审查修3：无台型若走补料恢复会 0.5s 翻转循环——全容器扫描+双遍历+列表重渲染每轮发生）
        var blocked = QueueState.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Blocked && !t.BlockedReason.Contains("制作台"));
        if (blocked != null && TryRecoverBlocked(blocked)) return;
        var noTable = QueueState.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Blocked && t.BlockedReason.Contains("制作台"));
        if (noTable != null) { noTable.Status = TaskStatus.Queued; }   // 重试派发一次（失败再 Blocked，3s 错峰限频）

        // ④ 派发队首
        if (UnityEngine.Time.unscaledTime < _resumeAt) return;   // 完成错峰窗口
        var next = QueueState.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Queued);
        if (next != null)
        {
            TryDispatch(next);
            if (next.Status == TaskStatus.Queued && UnityEngine.Time.unscaledTime >= _resumeAt)
            {
                // 派发失败（非错峰窗原因）→ 无合格台：标红，不静默等待
                next.Status = TaskStatus.Blocked;
                next.BlockedReason = "无可用制作台（需要同型号或有储物格的空闲工作台）";
            }
        }
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
    /// 入队（官方心智：台面有料即可做；2026-11-19 扩展 recipeKey 直取）。第一件优先=台面现料
    /// 搬进抽屉预占（与所选配方一致时）；台面无料/不构成配方且带 recipeKey → MaterialPicker
    /// 全链（背包→柜子→抽屉未预占）直接取料——入队不再依赖台面摆料（Gate 拦摆料时仍可入队）。
    /// 台面=当前打开的工作台（WorkbenchOwnerId）；配方=官方 GetProductionByMaterials 反推。
    /// </summary>
    internal static bool Enqueue(int qty, string recipeKey, out string failMsg)
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

        // 路径 B（key 直取）：台面无料或台面料不构成所选配方 → 按配方表全链取料
        Config_ProductionList cfg = null;
        List<List<long>> groups = null;
        var benchIds = new List<long>();
        var matIds = new Il2CppSystem.Collections.Generic.List<int>();
        if (bench != null)
        {
            for (int i = 0; i < bench.Count; i++)
            {
                long id = 0; int cid = 0, cnt = 0;
                try { id = bench[i].InstanceId; cid = bench[i].ItemConfigId; cnt = bench[i].ItemCount; } catch { continue; }
                if (id == 0) continue;
                benchIds.Add(id);
                for (int k = 0; k < cnt; k++) matIds.Add(cid);
            }
        }
        if (matIds.Count > 0)
        {
            try { cfg = CM().GetProductionByMaterials(matIds)?.Item1; } catch { }
        }
        int keyRecipeId = string.IsNullOrEmpty(recipeKey) ? 0 : CraftGate.ResolveRecipe(recipeKey);
        if (cfg == null || (keyRecipeId > 0 && cfg.ID != keyRecipeId))
        {
            // 台面不构成配方/与所选配方不一致 → key 直取（清台面语义不引入：台面料留在台面由玩家自理）
            if (keyRecipeId <= 0)
            {
                var dbg = new System.Text.StringBuilder();
                for (int i = 0; i < matIds.Count && i < 12; i++) dbg.Append(' ').Append(matIds[i]);
                if (matIds.Count > 0)
                    CraftQueuePlugin.Log.LogWarning($"[CraftQueue.Runner] 台面材料不构成配方（{matIds.Count} 件）：{dbg}");
                failMsg = matIds.Count > 0 ? "台面上的材料不构成配方" : "请先在制造笔记选择配方";
                return false;
            }
            cfg = MaterialPicker.ProductionCfg(keyRecipeId);
            if (cfg?.MaterialList == null) { failMsg = "配方不可用"; return false; }
            // 全量直取：qty 份一次锁进抽屉（按份分组——终审 P1）
            groups = MaterialPicker.PickAndReserveGrouped(cfg.MaterialList, qty, out var pf);
            if (groups.Count == 0)
            {
                failMsg = pf switch
                {
                    PickFail.Shortage => $"材料不足（需 {qty} 份，背包/柜子/抽屉凑不齐）",
                    PickFail.DrawerFull => "工作台抽屉放不下材料",
                    PickFail.Variant => "含染料变体的配方暂不支持队列",
                    PickFail.Partial => "部分材料是超过单件用量的大堆，请先拆堆（shift 拖出一半）再入队",
                    _ => "取料失败",
                };
                return false;
            }
        }
        else
        {
            // 路径 A（台面现料一份 + 链上补足 qty-1 份，全量预占）：先补后搬——补料失败零副作用
            if (qty > 1)
            {
                var extra = MaterialPicker.PickAndReserveGrouped(cfg.MaterialList, qty - 1, out var pf);
                if (extra.Count == 0)
                {
                    failMsg = pf switch
                    {
                        PickFail.Shortage => $"材料不足（需 {qty} 份，台面 1 份+背包/柜子/抽屉凑不齐）",
                        PickFail.DrawerFull => "工作台抽屉放不下材料",
                        PickFail.Variant => "含染料变体的配方暂不支持队列",
                        PickFail.Partial => "部分材料是超过单件用量的大堆，请先拆堆（shift 拖出一半）再入队",
                        _ => "取料失败",
                    };
                    return false;
                }
                groups = extra;
            }
            else groups = new List<List<long>>();
            // 台面这一份最后搬进抽屉（搬运不完整=抽屉满：链上已搬的留在抽屉不预占，本轮失败）
            int moved = 0;
            try { moved = im.TryQuickMoveItems(ToIl2Cpp(benchIds), benchOwner, drawerOwner, true); } catch { }
            if (moved != benchIds.Count)
            {
                failMsg = "工作台抽屉放不下材料";
                CraftQueuePlugin.Log.LogWarning($"[CraftQueue.Runner] 台面搬运不完整（选 {benchIds.Count} 搬 {moved}），链上已入抽屉的留给玩家");
                return false;
            }
            groups.Add(benchIds);   // 台面这份=独立一组（粒度由官方摆料保证）
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
            RecipeKey = "cfg:" + cfg.ID,        // 后续件取料按配方表
            ProductName = string.IsNullOrEmpty(name) ? "配方" : name,
            ProductIconId = icon,
            Left = qty,
            Total = qty,
            IsFurniture = isFurn,
            Status = TaskStatus.Queued,
        };
        t.ReserveGroups.AddRange(groups);
        foreach (var g in groups) t.ReservedItemIds.AddRange(g);
        QueueState.Add(t);
        QueueState.SyncReservedIds();
        if (CraftQueuePlugin.CfgVerbose.Value)
            CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 入队 #{t.TaskId} {t.ProductName}×{qty}（全量预占 {t.ReservedItemIds.Count} 件/{groups.Count} 份已锁抽屉）");
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
            t.ReserveGroups.Clear();
            QueueState.SyncReservedIds();
            if (CraftQueuePlugin.CfgVerbose.Value)
                CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] #{t.TaskId} 预占物品丢失 → Blocked");
            return;
        }
        // 本件一份=组[0]（入队时按份分组选料，终审 P1：分组保证每份可整件消耗——官方按整实例销毁）
        if (t.ReserveGroups.Count == 0 || t.ReserveGroups[0].Count == 0)
        {
            t.Status = TaskStatus.Blocked;
            t.BlockedReason = "预占材料异常（分组为空）";
            CraftQueuePlugin.Log.LogWarning($"[CraftQueue.Runner] #{t.TaskId} 预占分组为空（预占 {t.ReservedItemIds.Count} 件）");
            return;
        }
        var one = t.ReserveGroups[0];
        var furn = FindIdleTable();
        if (furn == 0) return;                    // 无合格台（调用方转 Blocked）
        var ttm = TTM();
        ttm.StartProduction(furn, ToIl2Cpp(one));
        t.CurrentIds.Clear();
        t.CurrentIds.AddRange(one);
        _busyFurnId = furn;
        _dispatchTick = 0;
        _lastQueueActivityAt = UnityEngine.Time.unscaledTime;
        _preProductIds = t.ProductIconId > 0 && !t.IsFurniture ? ScanProductIds(t.ProductIconId, furn) : null;   // 单容器快照：完成后 diff（官方同款模式）
        t.Status = TaskStatus.Dispatched;
        if (CraftQueuePlugin.CfgVerbose.Value)
            CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 派发 #{t.TaskId}（本件 {one.Count} 件，剩余预占 {t.ReservedItemIds.Count}）→ 家具 0x{furn:X}");
    }


    /// <summary>
    /// 两级筛选（审查定案）：ToolTableMap 含全部家具（RegisterToolTable 无类型筛选，反汇编实证）——
    /// ① 逻辑层判据：Furniture 且 Config_Furniture.BagId>0（BagId=0 家具收成品=官方静默丢弃，
    /// TryAddItemToBag 无 fallback，反汇编实证）；② 优先与当前打开工作台同型号（AgentConfigId 相同）。
    /// 无合格台 → 0（任务方 Blocked「无可用制作台」）。
    /// </summary>
    private static long FindIdleTable()
    {
        try
        {
            var map = TTM().ToolTableMap;
            var am = World()?._AgentManager;
            var cm = CM();
            var fd = cm?._Config_Furniture_Dict;
            if (map == null || am == null || fd == null) return 0;
            long openFurn = QueueUi.CurrentToolTableFurnitureId();
            int openCfg = 0;
            try
            {
                foreach (var ag in am.agentList)
                {
                    var f0 = ag?.TryCast<Furniture>();
                    if (f0 != null && f0.InstanceId == openFurn) { openCfg = f0.AgentConfigId; break; }
                }
            }
            catch { }
            long sameCfg = 0, anyBag = 0;
            foreach (var kv in map)
            {
                if (kv.Value == null) continue;
                if (kv.Key == openFurn) continue;                       // 避开玩家正开的台
                if ((int)kv.Value.State != 0) continue;                 // 非 Idle=忙
                var furn = FindFurniture(am, kv.Key);
                if (furn == null) continue;
                Config_Furniture cfg = null;
                try { fd.TryGetValue(furn.AgentConfigId, out cfg); } catch { }
                var bagId = 0;
                try { if (cfg != null) bagId = cfg.BagId; } catch { }
                if (bagId <= 0) continue;                               // ① 无包家具=成品静默丢弃
                if (openCfg != 0 && furn.AgentConfigId == openCfg && sameCfg == 0) sameCfg = kv.Key;   // ② 同型号优先
                if (anyBag == 0) anyBag = kv.Key;
            }
            return sameCfg != 0 ? sameCfg : anyBag;
        }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Runner] 找空闲台异常: " + e.Message); }
        return 0;
    }

    private static Furniture FindFurniture(AgentManager am, long instanceId)
    {
        try
        {
            foreach (var ag in am.agentList)
            {
                var f = ag?.TryCast<Furniture>();
                if (f != null && f.InstanceId == instanceId) return f;
            }
        }
        catch { }
        return null;
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
        // 回滚 IsPause 直写（实机证伪：直接改标志毁掉官方恢复触发条件→输入锁残留一直卡死）。
        // 定案：官方把队列台完成当前台弹窗——UI 暂停计数 +1 等「收下」释放；推送层弹窗拦截让玩家
        // 永远收不了→暂停挂到官方超时（间断卡根因）。正解=成品入抽屉+复位后 mod 代发官方
        // 「收下」action（ResultConfirm 走官方完整释放路径，不直写标志）。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ResetFurnState(_busyFurnId);
        if (sw.ElapsedMilliseconds > 300) CraftQueuePlugin.Log.LogWarning($"[CraftQueue.Runner] 收尾耗时 复位={sw.ElapsedMilliseconds}ms");
        var swp = System.Diagnostics.Stopwatch.StartNew();
        try { CollectProducts(t, _busyFurnId); }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Runner] 收尾异常: " + e.Message); }
        if (swp.ElapsedMilliseconds > 300) CraftQueuePlugin.Log.LogWarning($"[CraftQueue.Runner] 收尾耗时 成品搬运={swp.ElapsedMilliseconds}ms");
        // 代发 ResultConfirm 会触发官方 GetNew→RefreshBagUI→ReturnBenchItemsToOrigins——
        // 全局单例不分台，把玩家前台台面的备料清回背包（用户实测问题②）。改为直写官方收下
        // 的等价复位（RA_ResultConfirm 0x2D38B70 三动作：ResultShow=false/PendingCraftAgain=false/
        // 非 BatchKeepBench 才发 GetNew——我们不发 GetNew=保留玩家台面；官方批量模式同款取舍）
        try
        {
            var st = QueueUi.ToolTableStatePublic();
            if (st != null)
            {
                st.ResultShow.Value = false;
                st.PendingCraftAgain = false;
            }
        }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Runner] 收下复位异常: " + e.Message); }
        _lastQueueActivityAt = UnityEngine.Time.unscaledTime;
        _busyFurnId = 0;

        t.Left--;
        QueueState.JustFinishedTaskId = t.TaskId;
        QueueState.JustDoneAt = UnityEngine.Time.unscaledTime;   // 弹窗拦截 3s 窗 + 面板刷新
        _resumeAt = UnityEngine.Time.unscaledTime + 3f;      // 错峰 3s：官方完成链（统计/notify/归置）主线程卡顿的来源，拉开间隔
        // 全量预占：只移除本件已消耗的分组（组[0]=CurrentIds），剩余份数原地保留（2026-11-19 升级）
        if (t.ReserveGroups.Count > 0) t.ReserveGroups.RemoveAt(0);
        foreach (var id in t.CurrentIds) t.ReservedItemIds.Remove(id);
        t.CurrentIds.Clear();
        QueueState.SyncReservedIds();
        bool bad = state == HandMadeState.NoReceive;
        if (bad)
        {
            t.Status = TaskStatus.Blocked;
            t.BlockedReason = "产出接收异常（NoReceive）";
            _busyFurnId = 0;
            return;
        }
        if (t.Left <= 0)
        {
            t.Status = TaskStatus.Done;
            QueueState.Remove(t.TaskId);
            CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 完成 #{t.TaskId} {t.ProductName}×{t.Total}");
        }
        else
        {
            t.Status = TaskStatus.Queued;                 // 剩余预占已就位，直接续排
        }
    }

    /// <summary>
    /// 成品收尾（2026-11-19 定案：快照 diff 法——派发前记录该 configId 全容器 id 集，
    /// 完成帧（&lt;100ms）对比新增=本件成品，无论官方把它写进哪个容器都精确搬进抽屉。
    /// 官方内部输出位是黑盒（[单例+0x78]，实测落在取料相关容器），不再对其做任何假设。
    /// </summary>
    private static void CollectProducts(CraftTask t, long furnId)
    {
        if (t.IsFurniture || t.ProductIconId <= 0) return;      // 家具包裹：官方直送玄关
        var cur = ScanProductIds(t.ProductIconId, furnId);
        var fresh = new List<long>();
        var owners = new Dictionary<long, long>();
        foreach (var kv in cur)
        {
            if (_preProductIds != null && _preProductIds.ContainsKey(kv.Key)) continue;
            fresh.Add(kv.Key);
            owners[kv.Key] = kv.Value;
        }
        if (fresh.Count == 0)
        {
            CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 成品 #{t.ProductIconId} 无新增实例（家具包裹已在玄关/官方未产出），去向日志见上轮取证");
            return;
        }
        // 按 owner 分组搬进抽屉
        var im = IM();
        var drawerOwner = DrawerOwner();
        var byOwner = new Dictionary<long, List<long>>();
        foreach (var id in fresh)
        {
            if (!byOwner.TryGetValue(owners[id], out var list)) byOwner[owners[id]] = list = new List<long>();
            list.Add(id);
        }
        int movedTotal = 0, droppedTotal = 0;
        foreach (var g in byOwner)
        {
            int moved = 0;
            try { moved = im.TryQuickMoveItems(ToIl2Cpp(g.Value), g.Key, drawerOwner, true); } catch { }
            movedTotal += moved;
            if (moved == g.Value.Count) continue;
            // 抽屉满：残留掉地面（id 先快照再 Drop）
            var leftIds = new List<long>();
            var lst = im.GetItemDataList(g.Key);
            if (lst != null)
                for (int i = 0; i < lst.Count; i++)
                {
                    long id = 0;
                    try { id = lst[i].InstanceId; } catch { }
                    if (id != 0 && fresh.Contains(id)) leftIds.Add(id);
                }
            var dim = DIM();
            foreach (var id in leftIds)
                try { if (dim.DropItemFromBag(id)) droppedTotal++; } catch { }
        }
        CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 成品入抽屉 ×{movedTotal}" + (droppedTotal > 0 ? $"，抽屉满落地 ×{droppedTotal}" : ""));
    }

    /// <summary>扫描某 configId：id → 所在 owner。owner>0=单容器（队列台，官方同款快照/diff 模式）；
    /// owner=0=全容器（仅诊断用）。</summary>
    private static Dictionary<long, long> ScanProductIds(int configId, long ownerOnly)
    {
        var result = new Dictionary<long, long>();
        try
        {
            var rus = GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.ReduxUI.ReduxUISystem>.Instance;
            GameCore.HotUpdate.ReduxUI.BaseReduxState raw = null;
            rus?.reduxStoreLayer?.dataTree?.TryGetValue(
                Il2CppInterop.Runtime.Il2CppType.From(typeof(GameCore.HotUpdate.ReduxUI.State_Data_Item)), out raw);
            var bagCache = raw?.TryCast<GameCore.HotUpdate.ReduxUI.State_Data_Item>()?.BagCache;
            var im = IM();
            if (bagCache == null || im == null) return result;
            foreach (var kv in bagCache)
            {
                if (ownerOnly != 0 && kv.Key != ownerOnly) continue;
                var lst = im.GetItemDataList(kv.Key);
                if (lst == null) continue;
                for (int i = 0; i < lst.Count; i++)
                {
                    try { if (lst[i].ItemConfigId == configId) result[lst[i].InstanceId] = kv.Key; } catch { }
                }
            }
        }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Runner] 成品扫描异常: " + e.Message); }
        return result;
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
        // 「启动制作被拒」=持续性校验失败（材料/配方类），重试必然再拒——0.5s 翻转循环
        //（终审 P1-2）；挂起等玩家补料/取消，不走恢复
        if (t.BlockedReason.Contains("被拒")) return false;
        // Left=0（最后一件 NoReceive 型 Blocked）无件可做——恢复只会与「分组为空」判定互翻
        //（四审变体）；静止挂起，玩家取消即清
        if (t.Left <= 0) return false;
        // 优先验证预占是否完好（料仍在抽屉）：完好直接续排（NoReceive 型 Blocked 料没丢——
        // 重取反而制造幽灵预占+翻转循环，终审 P2）；真丢才清掉按剩余份数重取
        var im = IM();
        var drawerOwner = DrawerOwner();
        bool intact = t.ReserveGroups.Count >= t.Left;
        if (intact)
        {
            foreach (var id in t.ReservedItemIds)
            {
                var it = im?.GetItemData(id);
                if (it == null || it.OwnerId != drawerOwner) { intact = false; break; }
            }
        }
        if (intact)
        {
            t.BlockedReason = "";
            t.Status = TaskStatus.Queued;
            CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] #{t.TaskId} 预占完好，恢复排队");
            return true;
        }
        t.ReservedItemIds.Clear();
        t.ReserveGroups.Clear();
        QueueState.SyncReservedIds();          // 先解除全局预占（终审 P2-1：防幽灵剔除致永久 Shortage）
        var groups = MaterialPicker.PickAndReserveGrouped(MatsOf(t), Math.Max(1, t.Left), out var fail);   // 全量口径：剩余份数
        if (fail != PickFail.None || groups.Count == 0) return false;
        foreach (var g in groups) t.ReserveGroups.Add(g);
        foreach (var g in groups) t.ReservedItemIds.AddRange(g);
        t.BlockedReason = "";
        t.Status = TaskStatus.Queued;
        QueueState.SyncReservedIds();
        CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] #{t.TaskId} 补料恢复排队（重取 {groups.Count} 份）");
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
