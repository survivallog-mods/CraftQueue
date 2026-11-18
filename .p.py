import io, os
os.chdir(r'E:\Game\Mod\survivallog-mods\CraftQueue\SurvivalLog.CraftQueue')

s = io.open('QueueRunner.cs', encoding='utf-8').read()
start = s.index('    /// <summary>成品搬抽屉，装不下逐件掉落')
end = s.index('    /// <summary>直写复位（Ruling 见类头）')
new_cp = '''    /// <summary>
    /// 成品收尾（2026-11-19 全容器取证定案）：官方 TryCreateItem 的成品进**玩家背包**（全局 owner），
    /// 随后游戏机器人归置会把它搬进柜子/抽屉（按标签）。队列语义=尽量进抽屉：
    /// ① 完成后 0.5s 轮询窗口内从背包抢搬抽屉（机器人周期通常更慢）；
    /// ② 抽屉装不下→逐件掉落地面；③ 背包已没有=机器人已归置（柜子/抽屉）→接受，日志说明。
    /// （旧逻辑查 furnId 台面/全容器扫描均已废弃——台面 bag owner 与 furnId 不同域且成品根本不经台面。）
    /// </summary>
    private static void CollectProducts(CraftTask t, long furnId)
    {
        if (t.IsFurniture || t.ProductIconId <= 0) return;      // 家具包裹：官方直送玄关
        var im = IM();
        var drawerOwner = DrawerOwner();
        long bagOwner = 0;
        try { bagOwner = ActionDispatcher.GetLeadingRoleAgentId(); } catch { }
        if (bagOwner == 0 || im == null) return;

        // ① 背包找成品
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
        // ② 搬抽屉
        int moved = 0;
        try { moved = im.TryQuickMoveItems(ToIl2Cpp(found), bagOwner, drawerOwner, true); } catch { }
        if (moved == found.Count)
        {
            CraftQueuePlugin.Log.LogInfo($"[CraftQueue.Runner] 成品入抽屉 ×{moved}");
            return;
        }
        // ③ 抽屉装不下：残留逐件掉落（id 先快照再 Drop，防 Il2Cpp 列表失效）
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

'''
s = s[:start] + new_cp + s[end:]
# 调用处签名还原（t, furnId 不变）✓；删 DumpProductLocation（SelfTest 自带）
start2 = s.find('    /// <summary>成品去向运行时取证：遍历 Redux BagCache')
if start2 >= 0:
    end2 = s.index('    // ── 挂起恢复 / 阻塞恢复')
    s = s[:start2] + s[end2:]
io.open('QueueRunner.cs', 'w', encoding='utf-8', newline='\n').write(s)
print('collect reworked')
