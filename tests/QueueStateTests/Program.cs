using System;
using SurvivalLog.CraftQueue;

namespace SurvivalLog.CraftQueue.Tests;

// 零依赖 console 测试：失败累积输出，非零退出码=RED。
internal static class Program
{
    private static int _fails;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { Console.WriteLine($"PASS {name}"); return; }
        _fails++;
        Console.WriteLine($"FAIL {name} {detail}");
    }

    private static void Main()
    {
        // ── Add 自增与快照基本结构 ──
        var t1 = new CraftTask { RecipeKey = "plank", ProductName = "木板", ProductIconId = 3, Left = 3, Total = 3 };
        var t2 = new CraftTask { RecipeKey = "cab", ProductName = "储物柜", ProductIconId = 9, Left = 2, Total = 2, IsFurniture = true };
        var id1 = QueueState.Add(t1);
        var id2 = QueueState.Add(t2);
        Check("Add 自增 TaskId", id1 >= 1 && id2 == id1 + 1, $"id1={id1} id2={id2}");
        Check("Add 回填 TaskId 字段", t1.TaskId == id1 && t2.TaskId == id2);

        // ── 预占集合并集 ──
        t1.ReservedItemIds.AddRange(new long[] { 101, 102 });
        t2.ReservedItemIds.AddRange(new long[] { 102, 103 });
        QueueState.SyncReservedIds();
        Check("SyncReservedIds 并集去重",
            QueueState.ReservedItemIds.Count == 3 &&
            QueueState.ReservedItemIds.Contains(101) &&
            QueueState.ReservedItemIds.Contains(102) &&
            QueueState.ReservedItemIds.Contains(103));

        // ── Remove 后预占释放 ──
        Check("Remove 存在任务返回 true", QueueState.Remove(id1));
        Check("Remove 后并集只剩 t2 的预占", QueueState.ReservedItemIds.Count == 2 && !QueueState.ReservedItemIds.Contains(101));
        Check("Remove 不存在任务返回 false", !QueueState.Remove(id1));
        Check("Find 按 id 命中", QueueState.Find(id2) == t2 && QueueState.Find(id1) == null);

        // ── 快照 JSON ──
        t2.Status = TaskStatus.Blocked;
        t2.BlockedReason = "预占材料被取走";
        QueueState.Stamina = 92; QueueState.StaminaMax = 100;
        QueueState.CanMake = 11; QueueState.JustFinishedTaskId = 7;
        QueueState.StaminaPaused = true;
        QueueState.DrawerUsed = 5; QueueState.DrawerCap = 120;
        var json = QueueState.SnapshotJson();
        Console.WriteLine("snapshot: " + json);
        Check("快照含顶层运行字段",
            json.Contains("\"stamina\":92") && json.Contains("\"staminaMax\":100") &&
            json.Contains("\"canMake\":11") && json.Contains("\"justFinished\":7") &&
            json.Contains("\"paused\":true") && json.Contains("\"drawerUsed\":5") && json.Contains("\"drawerCap\":120"));
        Check("快照任务字段",
            json.Contains("\"name\":\"储物柜\"") && json.Contains("\"icon\":9") &&
            json.Contains("\"left\":2") && json.Contains("\"total\":2") &&
            json.Contains("\"status\":\"blocked\"") && json.Contains("\"furniture\":true") &&
            json.Contains("\"reason\":\"预占材料被取走\""));

        // ── 名字转义 ──
        QueueState.Tasks.Clear(); QueueState.SyncReservedIds();
        QueueState.Add(new CraftTask { RecipeKey = "x", ProductName = "a\"b\\c", Left = 1, Total = 1 });
        var j2 = QueueState.SnapshotJson();
        Check("ProductName 引号反斜杠转义", j2.Contains("\"name\":\"a\\\"b\\\\c\""), j2);

        // ── 分组算法 GroupSets（三轮审查反例全覆盖）──
        System.Collections.Generic.Dictionary<int, int> U(params (int cid, int n)[] kv)
        {
            var d = new System.Collections.Generic.Dictionary<int, int>();
            foreach (var (cid, n) in kv) d[cid] = n;
            return d;
        }
        System.Collections.Generic.List<(long, int, int)> P(params (long id, int cid, int cnt)[] items) => new(items);
        // ① 恰好整份：[3,3]×qty2 unit3 → 两组各 [3]
        var r1 = MaterialPicker.GroupSets(P((1, 10, 3), (2, 10, 3)), U((10, 3)), 2);
        Check("分组:整份堆恰好两组", r1 != null && r1.Count == 2 && r1[0].Count == 1 && r1[0][0] == 1 && r1[1][0] == 2);
        // ② 组内多实例凑份（终审 P1-1 顺序无关）：[2,2,1,1]×qty2 unit3 → 份 [2,1] [2,1]
        var r2 = MaterialPicker.GroupSets(P((1, 10, 2), (2, 10, 2), (3, 10, 1), (4, 10, 1)), U((10, 3)), 2);
        Check("分组:混合粒度凑份顺序无关", r2 != null && r2.Count == 2 && r2[0].Count == 2 && r2[1].Count == 2, r2 == null ? "null" : $"{r2.Count}组");
        // ③ 超取上界（P1-1）：[2,2,2]×qty1 unit3 → null（不再 2+2=4 超取销毁）
        var r3 = MaterialPicker.GroupSets(P((1, 10, 2), (2, 10, 2), (3, 10, 2)), U((10, 3)), 1);
        Check("分组:组内cnt上界防超取", r3 == null);
        // ④ 总量够但分组不可分（审查反例）：[2,2,2]×qty2 unit3 → null
        var r4 = MaterialPicker.GroupSets(P((1, 10, 2), (2, 10, 2), (3, 10, 2)), U((10, 3)), 2);
        Check("分组:总量够但不可分=诚实null", r4 == null);
        // ⑤ 多 configId：木3+石2 ×qty2 → 每组含两种
        var r5 = MaterialPicker.GroupSets(P((1, 10, 3), (2, 11, 2), (3, 10, 3), (4, 11, 2)), U((10, 3), (11, 2)), 2);
        Check("分组:多材料组合", r5 != null && r5.Count == 2 && r5[0].Contains(1) && r5[0].Contains(2) && r5[1].Contains(3) && r5[1].Contains(4));
        // ⑥ 池不足：[3]×qty2 unit3 → null
        var r6 = MaterialPicker.GroupSets(P((1, 10, 3)), U((10, 3)), 2);
        Check("分组:池不足null", r6 == null);

        Console.WriteLine(_fails == 0 ? "ALL GREEN" : $"{_fails} FAILED");
        Environment.Exit(_fails == 0 ? 0 : 1);
    }
}
