using System;
using System.Collections.Generic;
using GameCore.HotUpdate;
using GameCore.HotUpdate.Battle.Logic;
using GameCore.HotUpdate.ReduxUI;

namespace SurvivalLog.CraftQueue;

/// <summary>
/// 前台拦截（E 方案，spec §5.6）：官方前台「点配方自动取料」若将动用队列预占料 → 拦截并提示。
///
/// 拦截点取证修正（2026-11-18 实现期，ledger Ruling）：
/// - 前台取料发生在 **RecipeClick**（RA_ToolTable_RecipeClick → ApplyFillRecipe →
///   SelectItemsForRecipeLinked，DESIGN §2.1 官方链）——「制造」(Make) 只消费台面已放料、
///   不再取料 → 拦 RecipeClick 单点即覆盖主路径；CraftAgain（再造一次）会重取料，但刚做完
///   同配方意味着链上料充足，撞预占概率极低且派发校验兜底（Blocked 标红），v1 不拦。
/// - recipeKey 形态=材料 configId 排序串（ToolTableManager.ToProductionKey：List.Sort+拼接，
///   反汇编实证）→ 反查=遍历 Config_ProductionList 用官方同源函数生成 key 比对，缓存。
/// - webui payload：`{"recipeKey":"..."}`（ToolTable.html:3697 实证）。
/// </summary>
internal static class CraftGate
{
    private static bool _installed;
    private static Dictionary<string, int> _keyToRecipe;   // recipeKey → Config_ProductionList.ID（懒构建）

    /// <summary>最近一次放行的前台配方 key（QueueUi 入队消费——官方 qty 行无配方信息）。</summary>
    internal static string LastRecipeKey = "";

    internal static void Install()
    {
        if (_installed) return;
        try
        {
            var original = HarmonyLib.AccessTools.Method(
                typeof(Ac_ToolTable_RecipeClick), "SendAction");
            if (original == null)
            {
                CraftQueuePlugin.Log.LogWarning("[CraftQueue.Gate] RecipeClick 钩未装上（方法缺失，前台拦截失效=退轻防御）");
                return;
            }
            var h = new HarmonyLib.Harmony("com.local.survivallog.craftqueue.gate");
            h.Patch(original, prefix: new HarmonyLib.HarmonyMethod(typeof(CraftGate), nameof(Prefix)));
            var again = HarmonyLib.AccessTools.Method(typeof(Ac_ToolTable_CraftAgain), "SendAction");
            if (again != null)
                h.Patch(again, prefix: new HarmonyLib.HarmonyMethod(typeof(CraftGate), nameof(PrefixCraftAgain)));
            _installed = true;
            CraftQueuePlugin.Log.LogInfo("[CraftQueue.Gate] 前台取料拦截钩已挂（RecipeClick+CraftAgain）");
        }
        catch (Exception e)
        {
            CraftQueuePlugin.Log.LogWarning("[CraftQueue.Gate] 钩安装异常: " + e.Message);
        }
    }

    /// <summary>返回 false=拦截本次点配方（官方自动取料不发生）。</summary>
    private static bool Prefix(string JsonData)
    {
        try
        {
            // 诊断日志已退役（结论：native detour 未触发，配方捕获走 js 侧台面现料）
            if (QueueState.ReservedItemIds.Count == 0) return true;   // 无预占零干预
            var key = ExtractRecipeKey(JsonData);
            if (key == null) return true;
            var recipeId = ResolveRecipe(key);
            if (recipeId > 0) LastRecipeKey = key;         // 放行前记录（QueueUi 入队用）
            if (recipeId <= 0) return true;                            // 认不出的 key 放行（官方自己会失败）
            var cfg = MaterialPicker.ProductionCfg(recipeId);
            if (cfg?.MaterialList == null || cfg.MaterialList.Count == 0) return true;

            // 需求计数（重复出现=数量）
            var need = new Dictionary<int, int>();
            foreach (var m in cfg.MaterialList)
            {
                need.TryGetValue(m, out var n);
                need[m] = n + 1;
            }
            var bench = BenchOwner();                                       // 台面计入可用量（终审 I8）
            var lack = new List<string>();
            foreach (var kv in need)
            {
                if (MaterialPicker.CountAvailExcludingReserved(kv.Key, bench) < kv.Value)
                    lack.Add("#" + kv.Key);
            }
            if (lack.Count == 0) return true;                          // 刨除预占后仍够：放行，无感
            Ac_PopText_AddPopText.SendAction("材料已被队列任务占用（缺 " + string.Join("、", lack) + "），请补充材料或取消队列任务");
            CraftQueuePlugin.Log.LogInfo("[CraftQueue.Gate] 拦截前台取料 recipe=" + recipeId + " 缺 " + string.Join(",", lack));
            return false;
        }
        catch (Exception e)
        {
            CraftQueuePlugin.Log.LogWarning("[CraftQueue.Gate] 判定异常放行: " + e.Message);
            return true;                                              // 判定失败宁可放行（官方兜底）
        }
    }

    /// <summary>提取 {"recipeKey":"xxx"} 的 key（手写，零依赖；格式不对返回 null=放行）。</summary>
    private static string ExtractRecipeKey(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        const string tag = "\"recipeKey\"";
        int p = json.IndexOf(tag, StringComparison.Ordinal);
        if (p < 0) return null;
        int c = json.IndexOf(':', p + tag.Length);
        if (c < 0) return null;
        int q1 = json.IndexOf('"', c + 1);
        if (q1 < 0) return null;
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return null;
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    /// <summary>当前打开工作台的台面 owner（前台判定口径用；界面未开=0）。</summary>
    internal static long BenchOwner()
    {
        try
        {
            if (!BaseSingleton<GameCore.HotUpdate.ReduxUI.ReduxUISystem>.IsInstanceCreated) return 0;
            var st = BaseSingleton<GameCore.HotUpdate.ReduxUI.ReduxUISystem>.Instance
                .GetStateByUI(QueueUi.ToolTableStateType)?.TryCast<GameCore.HotUpdate.ReduxUI.State_Web_ToolTable>();
            var rp = st?.WorkbenchOwnerId; return rp != null ? rp.Value : 0;
        }
        catch { return 0; }
    }

    /// <summary>「再造一次」入口（终审 I9：CraftAgain 重取料可吃预占料，同口径拦）。</summary>
    private static bool PrefixCraftAgain()
    {
        try
        {
            if (QueueState.ReservedItemIds.Count == 0) return true;
            var recipeId = LastRecipeId;                        // 再造=最近配方
            if (recipeId <= 0) return true;
            var cfg = MaterialPicker.ProductionCfg(recipeId);
            if (cfg?.MaterialList == null || cfg.MaterialList.Count == 0) return true;
            var need = new Dictionary<int, int>();
            foreach (var m in cfg.MaterialList) { need.TryGetValue(m, out var n); need[m] = n + 1; }
            var bench = BenchOwner();
            foreach (var kv in need)
                if (MaterialPicker.CountAvailExcludingReserved(kv.Key, bench) < kv.Value)
                {
                    Ac_PopText_AddPopText.SendAction("材料已被队列任务占用，请补充材料或取消队列任务");
                    return false;
                }
        }
        catch { }
        return true;
    }

    /// <summary>QueueUi 入队用：最近前台配方 → 配方 ID（解析失败 0）。</summary>
    internal static int LastRecipeId => string.IsNullOrEmpty(LastRecipeKey) ? 0 : ResolveRecipe(LastRecipeKey);

    /// <summary>recipeKey → 配方 ID：官方同源 ToProductionKey 全表生成比对（懒构建缓存）。
    /// Enqueue 直取路径共用（2026-11-19：入队不再依赖台面摆料——Gate 拦摆料时入队仍可用）。</summary>
    internal static int ResolveRecipe(string key)
    {
        if (_keyToRecipe != null)
            return _keyToRecipe.TryGetValue(key, out var id) ? id : 0;
        var map = new Dictionary<string, int>();
        try
        {
            if (BaseSingleton<ConfigManager>.IsInstanceCreated)
            {
                var dict = BaseSingleton<ConfigManager>.Instance._Config_ProductionList_Dict;
                if (dict != null)
                {
                    foreach (var kv in dict)
                    {
                        var cfg = kv.Value;
                        if (cfg?.MaterialList == null) continue;
                        try
                        {
                            var k = ToolTableManager.ToProductionKey(cfg);
                            if (!string.IsNullOrEmpty(k)) map[k] = kv.Key;
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception e)
        {
            CraftQueuePlugin.Log.LogWarning("[CraftQueue.Gate] key 反查表构建异常: " + e.Message);
        }
        _keyToRecipe = map;
        return map.TryGetValue(key, out var rid) ? rid : 0;
    }
}
