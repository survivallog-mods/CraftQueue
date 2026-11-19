using System;
using System.Collections.Generic;
using System.Linq;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;

namespace SurvivalLog.CraftQueue;

/// <summary>取料失败原因（spec §5.2/§5.6.1）。</summary>
public enum PickFail
{
    None = 0,
    Shortage,     // 链上（刨除预占）材料不足
    DrawerFull,   // 抽屉剩余格不够预占
    Variant,      // 配方含染料变体材料，v1 不支持（spec §7.5）
    NoRecipe,     // 配方 id 查无
    Partial,      // 只有堆叠数超过需求的实例（整件预占会超额销毁多余部分，v1 不拆堆——终审 C3）
}

/// <summary>
/// 取料选择器（spec §5.2）：入队时按「背包→柜子→抽屉(未预占)」选料搬进工作台抽屉并登记预占。
///
/// 取证基线（spec §3 与 2026-11-18 补证）：
/// - 物品跨包移动保实例 id（OnMoveItem(itemId, from, to)；官方 CachedItemIds 跨包消耗同证）
///   → 搬进抽屉后 id 不变，StartProduction 直接吃这些 id。
/// - 选择性搬运 = ItemManager.TryQuickMoveItems(List&lt;long&gt;, from, to, silent)（RVA 0x430395）。
/// - 染料判定 = DyeConfig.IsDyeMaterial(configId)（dump 实证 public static）。
/// - 柜子扫描 = CabinetCatalog 精简版：furn.InstanceId 即 owner、Config_Furniture.BagId&gt;0 即
///   储物容器、AgentManager.IsHomeMap 家图、排除功能包/家具（名单取自 CabinetEverywhere 实证）。
/// Ruling（ledger）：取料池不含台面与页签源（前台工作区保护）。
/// </summary>
internal static class MaterialPicker
{
    // 排除名单（CabinetEverywhere CabinetCatalog 同源实证）：
    // 功能包 BagId：1008 无人机货舱 / 1009 中控台 / 1010 备料格 / 2003 材料堆；
    // 家具级：自制工具台 313/80033/80164、载具 1024-1028/42033、染料桶 66196、材料堆 330/384/389/9126
    private static readonly HashSet<int> NonStorageBagIds = new() { 1008, 1009, 1010, 2003 };
    private static readonly HashSet<int> NonStorageFurnitureIds = new()
        { 313, 80033, 80164, 1024, 1025, 1026, 1027, 1028, 42033, 66196, 330, 384, 389, 9126 };

    private static List<long> _cabinetOwners = new();
    private static float _cabinetScanAt = -999f;

    private static GameCore.HotUpdate.Battle.Logic.ItemManager IM()
        => GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.Instance._ItemManager;

    private static AgentManager AM()
        => GameCore.HotUpdate.BaseSingleton<BattleLogicWorld>.Instance._AgentManager;

    private static ConfigManager CM()
    {
        if (!BaseSingleton<ConfigManager>.IsInstanceCreated) return null;
        try { return BaseSingleton<ConfigManager>.Instance; } catch { return null; }
    }

    private static long DrawerOwner()
    {
        try { return WorkbenchDrawerConfig.DrawerOwnerId; } catch { return 0; }
    }

    private static Il2CppSystem.Collections.Generic.List<long> ToIl2CppList(List<long> src)
    {
        var dst = new Il2CppSystem.Collections.Generic.List<long>(src.Count);
        foreach (var v in src) dst.Add(v);
        return dst;
    }

    /// <summary>
    /// 为任务选料并预占（2026-11-19 全量升级，用户实测定案：入队 qty 份=锁 qty 份料）：
    /// 选背包→柜子→抽屉(未预占)中该配方 ×qty 份的全部材料，**按份分组**（终审 P1 修复：贪心
    /// 凑份与总量可满足不等价——混合粒度堆时总量够但单份凑不出），搬进抽屉。
    /// 返回 List[qty]（每组=一份的实例 id 集）；空=失败（fail 说明原因）。
    /// 实例粒度不变量（C3，2026-11-20 定案）：官方消耗按整实例销毁——预占实例 cnt≤单件该
    /// configId 需求；大堆不选（SplitItem 实为 UseTimes 拆分对可堆叠材料恒失败、AddItem 恒
    /// 并堆——自动拆堆无安全官方 API，大堆 Partial 引导玩家手动拆）。
    /// </summary>
    public static List<List<long>> PickAndReserveGrouped(Il2CppSystem.Collections.Generic.List<int> materialIds, int qty, out PickFail fail)
    {
        fail = PickFail.None;
        var groups = new List<List<long>>();
        var im = IM();
        var drawerOwner = DrawerOwner();
        if (materialIds == null || materialIds.Count == 0)
        {
            fail = PickFail.NoRecipe;
            return groups;
        }
        // 染料变体：v1 直接拒绝（spec §7.5，材料实例不唯一确定）
        for (int mi = 0; mi < materialIds.Count; mi++)
        {
            bool isDye = false;
            try { isDye = DyeConfig.IsDyeMaterial(materialIds[mi]); } catch { }
            if (isDye) { fail = PickFail.Variant; return groups; }
        }

        // 单份需求（重复出现=数量）
        var unit = new Dictionary<int, int>();
        for (int mi = 0; mi < materialIds.Count; mi++)
        {
            unit.TryGetValue(materialIds[mi], out var u);
            unit[materialIds[mi]] = u + 1;
        }

        // 抽屉容量（近似口径：剩余格 ≥ 单份种类数——分组占格≥扁平，抽屉满由搬运核验兜底）
        var drawerItems = im.GetItemDataList(drawerOwner);
        int drawerUsed = drawerItems?.Count ?? 0;
        if (QueueState.DrawerCap - drawerUsed < unit.Count)
        {
            QueueState.DrawerUsed = drawerUsed;
            fail = PickFail.DrawerFull;
            return groups;
        }

        // 候选池（顺序：背包 → 柜子 → 抽屉未预占；单件粒度过滤）
        var pool = new List<(long id, int cid, int cnt, long owner)>();
        bool overStackOnly = false;
        foreach (var owner in CollectOwners())
        {
            Il2CppSystem.Collections.Generic.List<ItemData> items = null;
            try { items = im.GetItemDataList(owner); } catch { }
            if (items == null) continue;
            foreach (var it in items)
            {
                long iid; int cid, cnt;
                try { iid = it.InstanceId; cid = it.ItemConfigId; cnt = it.ItemCount; } catch { continue; }
                if (!unit.ContainsKey(cid) || cnt <= 0) continue;
                bool reserved = QueueState.ReservedItemIds.Contains(iid);
                if (owner == drawerOwner && reserved) continue; // 抽屉只剔预占；其他源不可能预占（预占只在抽屉）
                if (cnt > unit[cid]) { overStackOnly = true; continue; }   // 单件粒度不变量：大堆不选
                pool.Add((iid, cid, cnt, owner));
            }
        }

        // 按份分组（纯函数，组内 cnt≤left 上界防超取销毁放大——终审三轮 P1-1/P1-2）
        var sets = GroupSets(pool.ConvertAll(c => (c.id, c.cid, c.cnt)), unit, qty);
        if (sets == null)
        {
            // 凑不齐：曾有大堆被粒度过滤=Partial（引导拆堆），否则 Shortage
            fail = overStackOnly ? PickFail.Partial : PickFail.Shortage;
            return new List<List<long>>();
        }
        groups = sets;
        var pickedBySrc = new Dictionary<long, List<long>>();
        foreach (var g in groups)
            foreach (var id in g)
            {
                long owner = 0;
                foreach (var c in pool)
                    if (c.id == id) { owner = c.owner; break; }
                if (!pickedBySrc.TryGetValue(owner, out var list))
                    pickedBySrc[owner] = list = new List<long>();
                list.Add(id);
            }

        // 搬运进抽屉并核验数量（同 owner=抽屉的不搬）；不完整=已搬的留给玩家，本轮失败
        foreach (var kv in pickedBySrc)
        {
            if (kv.Key == drawerOwner) continue;
            int moved = 0;
            try { moved = im.TryQuickMoveItems(ToIl2CppList(kv.Value), kv.Key, drawerOwner, true); } catch { }
            if (moved != kv.Value.Count)
            {
                CraftQueuePlugin.Log.LogWarning(
                    $"[CraftQueue.Picker] 搬运不完整 owner=0x{kv.Key:X} 选={kv.Value.Count} 搬={moved}（已入抽屉的散料留给玩家），放弃本次取料");
                fail = PickFail.DrawerFull;
                return new List<List<long>>();
            }
        }
        return groups;
    }

    /// <summary>扁平包装（探测/SelfTest 用）：分组结果拍平。</summary>
    public static List<long> PickAndReserve(Il2CppSystem.Collections.Generic.List<int> materialIds, int qty, out PickFail fail)
    {
        var flat = new List<long>();
        foreach (var g in PickAndReserveGrouped(materialIds, qty, out fail))
            flat.AddRange(g);
        return flat;
    }

    /// <summary>
    /// 分组核心（纯函数，可单测）：共享候选池逐份扣减——每份每 configId 取实例凑满 unit，
    /// 组内只取 cnt≤left 的实例（上界防超取：官方按整实例销毁，超取=多余部分被销毁）；
    /// 池内已用实例标记 cnt=0。凑不齐任一份返回 null（零副作用——调用方判定 Partial/Shortage）。
    /// </summary>
    internal static List<List<long>> GroupSets(List<(long id, int cid, int cnt)> pool, Dictionary<int, int> unit, int qty)
    {
        var groups = new List<List<long>>();
        for (int j = 0; j < qty; j++)
        {
            var grp = new List<long>();
            var remaining = new Dictionary<int, int>(unit);
            bool short_ = false;
            foreach (var cid in unit.Keys)
            {
                int left = remaining[cid];
                for (int p = 0; p < pool.Count && left > 0; p++)
                {
                    if (pool[p].cnt <= 0 || pool[p].cnt > left || pool[p].cid != cid) continue;
                    grp.Add(pool[p].id);
                    left -= pool[p].cnt;
                    pool[p] = (pool[p].id, pool[p].cid, 0);
                }
                remaining[cid] = left;
                if (left > 0) { short_ = true; break; }
            }
            if (short_) return null;
            groups.Add(grp);
        }
        return groups;
    }

    /// <summary>配方配置（Enqueue 建任务用；null=查无）。</summary>
    public static Config_ProductionList ProductionCfg(int recipeId) => CM()?.Get_Config_ProductionList(recipeId);

    /// <summary>前台拦截用：链上（台面+背包+柜子+抽屉，刨除预占）某 configId 可用总数；
    /// extraOwner=前台判定时的台面（spec §5.6.1 台面计入可用量，终审 I8——官方取料顺序
    /// 台面最前，台面已有料本就减少本次取料需求；漏数=误拦正常前台摆料，2026-11-19 实证修复）。</summary>
    public static int CountAvailExcludingReserved(int configId, long extraOwner = 0)
    {
        var im = IM();
        var drawerOwner = DrawerOwner();
        int total = 0;
        if (extraOwner != 0)
        {
            Il2CppSystem.Collections.Generic.List<ItemData> bench = null;
            try { bench = im.GetItemDataList(extraOwner); } catch { }
            if (bench != null)
                foreach (var it in bench)
                {
                    try { if (it.ItemConfigId == configId) total += it.ItemCount; } catch { }
                }
        }
        foreach (var owner in CollectOwners())
        {
            Il2CppSystem.Collections.Generic.List<ItemData> items = null;
            try { items = im.GetItemDataList(owner); } catch { }
            if (items == null) continue;
            foreach (var it in items)
            {
                try
                {
                    if (it.ItemConfigId != configId) continue;
                    if (owner == drawerOwner && QueueState.ReservedItemIds.Contains(it.InstanceId)) continue;
                    total += it.ItemCount;
                }
                catch { }
            }
        }
        return total;
    }

    /// <summary>取料 owner 链：背包 → 柜子（5s 缓存扫描）→ 抽屉。台面/页签源不参与（Ruling）。</summary>
    private static List<long> CollectOwners()
    {
        var owners = new List<long>();
        long bagOwner = 0;
        try { bagOwner = ActionDispatcher.GetLeadingRoleAgentId(); } catch { }
        if (bagOwner != 0) owners.Add(bagOwner);
        owners.AddRange(CabinetOwners());
        var drawer = DrawerOwner();
        if (drawer != 0) owners.Add(drawer);
        return owners;
    }

    /// <summary>柜子 owner 清单（5s 缓存）：官方 GetFurnituresWithBag 枚举 + 功能包/家具排除。</summary>
    private static List<long> CabinetOwners()
    {
        var now = UnityEngine.Time.unscaledTime;
        if (now - _cabinetScanAt < 5f) return _cabinetOwners;
        _cabinetScanAt = now;
        var fresh = new List<long>();
        try
        {
            var am = AM();
            var cm = CM();
            var dict = cm?._Config_Furniture_Dict;
            int mapId = 0;
            try { mapId = am.GetHomeMapId(); } catch { }
            if (mapId == 0 || dict == null) return _cabinetOwners = fresh;
            Il2CppSystem.Collections.Generic.List<Furniture> withBag = null;
            try { withBag = am.GetFurnituresWithBag(mapId, true, false, false); } catch { }
            if (withBag == null) return _cabinetOwners = fresh;
            for (int i = 0; i < withBag.Count; i++)
            {
                Furniture furn = null;
                try { furn = withBag[i]; } catch { continue; }
                if (furn == null) continue;
                long ownerId; int cfgId;
                try { ownerId = furn.InstanceId; cfgId = furn.AgentConfigId; } catch { continue; }
                if (ownerId == 0 || NonStorageFurnitureIds.Contains(cfgId)) continue;
                Config_Furniture cfg = null;
                try { dict.TryGetValue(cfgId, out cfg); } catch { }
                int bagId = 0;
                try { if (cfg != null) bagId = cfg.BagId; } catch { }
                if (bagId <= 0 || NonStorageBagIds.Contains(bagId)) continue;
                fresh.Add(ownerId);
            }
        }
        catch (Exception e)
        {
            CraftQueuePlugin.Log.LogWarning("[CraftQueue.Picker] 柜子扫描异常: " + e.Message);
        }
        return _cabinetOwners = fresh;
    }
}
