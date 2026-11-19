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
    /// 为一件任务选料并预占：选背包→柜子→抽屉(未预占)中该配方的全部材料，搬进抽屉，
    /// 登记进 QueueState.ReservedItemIds（经调用方任务对象）。返回物品 id 列表（空=失败）。
    /// </summary>
    public static List<long> PickAndReserve(Il2CppSystem.Collections.Generic.List<int> materialIds, out PickFail fail)
    {
        fail = PickFail.None;
        var result = new List<long>();
        var im = IM();
        var drawerOwner = DrawerOwner();
        if (materialIds == null || materialIds.Count == 0)
        {
            fail = PickFail.NoRecipe;
            return result;
        }
        // 染料变体：v1 直接拒绝（spec §7.5，材料实例不唯一确定）
        for (int mi = 0; mi < materialIds.Count; mi++)
        {
            bool isDye = false;
            try { isDye = DyeConfig.IsDyeMaterial(materialIds[mi]); } catch { }
            if (isDye) { fail = PickFail.Variant; return result; }
        }

        // 需求计数（重复出现=数量）
        var need = new Dictionary<int, int>();
        for (int mi = 0; mi < materialIds.Count; mi++)
        {
            need.TryGetValue(materialIds[mi], out var n);
            need[materialIds[mi]] = n + 1;
        }

        // 抽屉容量（近似口径：剩余格 ≥ 材料种类数）
        var drawerItems = im.GetItemDataList(drawerOwner);
        int drawerUsed = drawerItems?.Count ?? 0;
        int drawerCap = QueueState.DrawerCap;
        if (drawerCap - drawerUsed < need.Count)
        {
            QueueState.DrawerUsed = drawerUsed;
            fail = PickFail.DrawerFull;
            return result;
        }

        // 逐 owner 选料（顺序：背包 → 柜子 → 抽屉；抽屉候选剔除已预占）
        var pickedBySrc = new Dictionary<long, List<long>>();
        var remaining = new Dictionary<int, int>(need);
        bool overStackOnly = false;
        foreach (var owner in CollectOwners())
        {
            if (remaining.Count == 0) break;
            Il2CppSystem.Collections.Generic.List<ItemData> items = null;
            try { items = im.GetItemDataList(owner); } catch { }
            if (items == null) continue;
            // 该 owner 内按需挑选：同 configId 的物品按堆叠数扣减，跨物品累计
            foreach (var it in items)
            {
                if (remaining.Count == 0) break;
                long iid; int cid, cnt;
                try { iid = it.InstanceId; cid = it.ItemConfigId; cnt = it.ItemCount; } catch { continue; }
                if (!remaining.TryGetValue(cid, out var left) || left <= 0) continue;
                bool reserved = QueueState.ReservedItemIds.Contains(iid);
                if (owner == drawerOwner && reserved) continue; // 抽屉只剔预占；其他源物品不可能预占（预占只在抽屉）
                // 终审 C3：官方 ForceRemoveItem 按整件消耗——堆叠数超过剩余需求的实例不可整件预占
                //（否则多余部分被销毁）。只选 cnt <= left 的实例；全超则 Partial（提示整理/拆堆）。
                if (cnt > left) { overStackOnly = true; continue; }
                remaining[cid] = left - cnt;
                if (remaining[cid] <= 0) remaining.Remove(cid);
                if (!pickedBySrc.TryGetValue(owner, out var list))
                    pickedBySrc[owner] = list = new List<long>();
                list.Add(iid);
            }
        }
        if (remaining.Count > 0)
        {
            fail = overStackOnly ? PickFail.Partial : PickFail.Shortage;
            return result;
        }

        // 搬运进抽屉并核验数量（同 owner=抽屉的不搬）
        foreach (var kv in pickedBySrc)
        {
            if (kv.Key == drawerOwner)
            {
                result.AddRange(kv.Value);
                continue;
            }
            int moved = 0;
            try { moved = im.TryQuickMoveItems(ToIl2CppList(kv.Value), kv.Key, drawerOwner, true); } catch { }
            if (moved != kv.Value.Count)
            {
                // 搬运不完整（抽屉半路满等）：把已搬进抽屉的这批留给玩家（不预占），本轮失败
                CraftQueuePlugin.Log.LogWarning(
                    $"[CraftQueue.Picker] 搬运不完整 owner=0x{kv.Key:X} 选={kv.Value.Count} 搬={moved}，放弃本次取料");
                fail = PickFail.DrawerFull;
                return new List<long>();
            }
            result.AddRange(kv.Value);
        }
        return result;
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
