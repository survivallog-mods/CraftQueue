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

        Console.WriteLine(_fails == 0 ? "ALL GREEN" : $"{_fails} FAILED");
        Environment.Exit(_fails == 0 ? 0 : 1);
    }
}
