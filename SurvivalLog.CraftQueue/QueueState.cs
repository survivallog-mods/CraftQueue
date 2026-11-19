using System.Collections.Generic;
using System.Text;

namespace SurvivalLog.CraftQueue;

/// <summary>任务状态机（spec §5.1）。快照字符串映射：Queued→queued、Dispatched→making、Blocked→blocked、Done→done。</summary>
public enum TaskStatus
{
    Queued = 0,
    Dispatched = 1,
    Blocked = 2,
    Done = 3,
}

/// <summary>一条队列任务 = 配方 × 数量，逐件派发（spec §5.1）。</summary>
public sealed class CraftTask
{
    public int TaskId;
    public string RecipeKey = "";
    public string ProductName = "";
    public int ProductIconId;
    public int Left;                       // 剩余件数
    public int Total;                      // 总件数
    public readonly List<long> ReservedItemIds = new();   // 全任务（qty 份）已预占的材料物品 id 扁平并集（在抽屉）
    public readonly List<List<long>> ReserveGroups = new(); // 按份分组（组[0]=下一件；派发/完成按组流转——终审 P1）
    public readonly List<long> CurrentIds = new();        // 当前派发件的预占 id 子集（=消耗时移除的组）
    public TaskStatus Status = TaskStatus.Queued;
    public string BlockedReason = "";
    public bool IsFurniture;               // 家具包裹成品走官方原路送玄关（spec §4）
}

/// <summary>
/// 队列全局状态（会话内存，不入存档——套件红线）。运行字段（精力/抽屉）由 QueueRunner 每轮刷新，
/// SnapshotJson() 供 QueueUi 推送（字段名与原型 js 对齐，spec §5.7）。
/// </summary>
public static class QueueState
{
    private static int _nextId = 1;

    public static readonly List<CraftTask> Tasks = new();
    public static readonly HashSet<long> ReservedItemIds = new();

    public static float Stamina;
    public static float StaminaMax = 100f;
    public static bool StaminaPaused;
    public static int CanMake;                 // 精力还能做几件（runner 按 req 计算，js 直显）
    public static int JustFinishedTaskId;
    public static float JustDoneAt = -999f;                 // 最近一次队列完成时刻（弹窗拦截 3s 窗）      // 刚收尾的任务（js 据此自动清官方成品弹窗；推送后由下轮签名变化清零）
    public static int DrawerUsed;
    public static int DrawerCap = 120;

    public static int Add(CraftTask t)
    {
        t.TaskId = _nextId++;
        Tasks.Add(t);
        return t.TaskId;
    }

    public static CraftTask Find(int taskId) => Tasks.Find(t => t.TaskId == taskId);

    /// <summary>出队并重建全局预占集合（spec §5.5：取消/完成后料留抽屉，仅解除预占登记）。</summary>
    public static bool Remove(int taskId)
    {
        var t = Find(taskId);
        if (t == null) return false;
        Tasks.Remove(t);
        SyncReservedIds();
        return true;
    }

    /// <summary>全局预占集合 = 全部任务 ReservedItemIds 并集。任务预占变动后必调。</summary>
    public static void SyncReservedIds()
    {
        ReservedItemIds.Clear();
        foreach (var t in Tasks)
            foreach (var id in t.ReservedItemIds)
                ReservedItemIds.Add(id);
    }

    /// <summary>队列快照 JSON（手写拼接，零依赖；QueueUi 直接推送）。</summary>
    public static string SnapshotJson()
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"stamina\":").Append((int)Stamina)
          .Append(",\"staminaMax\":").Append((int)StaminaMax)
          .Append(",\"canMake\":").Append(CanMake)
          .Append(",\"justFinished\":").Append(JustFinishedTaskId)
          .Append(",\"paused\":").Append(StaminaPaused ? "true" : "false")
          .Append(",\"drawerUsed\":").Append(DrawerUsed)
          .Append(",\"drawerCap\":").Append(DrawerCap)
          .Append(",\"tasks\":[");
        for (int i = 0; i < Tasks.Count; i++)
        {
            var t = Tasks[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(t.TaskId)
              .Append(",\"name\":\"").Append(Escape(t.ProductName))
              .Append("\",\"icon\":").Append(t.ProductIconId)
              .Append(",\"left\":").Append(t.Left)
              .Append(",\"total\":").Append(t.Total)
              .Append(",\"status\":\"").Append(StatusStr(t.Status))
              .Append("\",\"reason\":\"").Append(Escape(t.BlockedReason))
              .Append("\",\"furniture\":").Append(t.IsFurniture ? "true" : "false")
              .Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string StatusStr(TaskStatus s) => s switch
    {
        TaskStatus.Queued => "queued",
        TaskStatus.Dispatched => "making",
        TaskStatus.Blocked => "blocked",
        _ => "done",
    };

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
