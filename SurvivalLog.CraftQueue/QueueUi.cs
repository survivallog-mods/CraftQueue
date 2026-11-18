using System;
using System.IO;
using System.Reflection;
using GameCore.HotUpdate;
using GameCore.HotUpdate.ReduxUI;

namespace SurvivalLog.CraftQueue;

/// <summary>
/// WebUI 注入（spec §5.7）：queueui.page.js bootstrap + 队列快照推送 + Btn_Craft 包装通道。
///
/// 通道取证（CabinetEverywhere RailUi 同源 + 本 mod 实机两轮定位教训）：
/// - WebView = ReduxUISystem.Instance.GetWebUILayer().canvasWebViewPrefab.WebView（IWebView）；
///   ExecuteJavaScript 在 Root 主文档执行；bootstrap js 自跨 iframe#ToolTable。
/// - **单参 ExecuteJavaScript 在本机 Vuplex 组合下静默丢弃**（CE 十三批六追实证）——所有推送
///   必须走带回调重载；回调兼作注入自证（CQ_OK/CQ_BAD 标记）。
/// - **bootstrap 拼接必须去掉 IIFE 尾分号**：`window.__CQ__||((function(){...})();)` 括号内
///   含分号=SyntaxError，IIFE 从未执行（实机二轮定位）。
/// - js→C#：包装官方 Btn_Craft（State_Web_ToolTable 的 Action&lt;string&gt; 属性）：页面按钮发
///   官方 CRAFT 事件、payload 多带 cqEnqueue/cqCancel 标记——命中=队列语义（不透传防双发），
///   未命中=透传官方。UI 重开重建 state → 按实例引用幂等重包。
/// </summary>
internal static class QueueUi
{
    private static string _bootstrap;            // queueui.page.js 全文（EmbeddedResource 一次读）
    private static bool _bootstrapFailed;
    private static float _nextPoll;
    private static string _lastSig = "";         // 快照签名（变化才推）
    private static Il2CppSystem.Action<string> _ackCb;
    private static Il2CppSystem.Action<string> _drainCb;
    private static int _ackCount;

    // GetStateByUI 键=UI 类型（DrawerTabs 先例），非 State 类型
    internal static readonly Il2CppSystem.Type ToolTableStateType =
        Il2CppInterop.Runtime.Il2CppType.From(typeof(WebUI_ToolTable));

    // 单参 ExecuteJavaScript 本机静默丢弃——回调仅作送达/自证信号（CE _pushAckCb 同款纪律）
    private static Il2CppSystem.Action<string> Ack =>
        _ackCb ??= Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string>>(
            new Action<string>(r =>
            {
                _ackCount++;
                if (_ackCount == 1)
                    CraftQueuePlugin.Log.LogInfo("[CraftQueue.Ui] 首次 JS 注入回执: " +
                        (string.IsNullOrEmpty(r) ? "(空)" : r));
            }));

    internal static void FastPoll()
    {
        if (UnityEngine.Time.unscaledTime < _nextPoll) return;
        _nextPoll = UnityEngine.Time.unscaledTime + 0.5f;
        try { Poll(); }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Ui] 轮询异常: " + e.Message); }
    }

    private static void Poll()
    {
        var web = GetWebView();
        if (web == null) return;
        if (_bootstrap == null && !_bootstrapFailed) _bootstrap = ReadBootstrap();
        if (_bootstrap == null) { _bootstrapFailed = true; return; }   // 失败不刷屏重试

        // ⓪ 取走 js 命令队列（drain 返回值经回调回传——CE RailCommands 同款，不依赖官方事件路由）
        if (_drainCb == null)
            _drainCb = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string>>(
                new Action<string>(OnDrain));
        try { web.ExecuteJavaScript("window.__CQ__&&__CQ__.drain()", _drainCb); } catch { }

        // ① bootstrap 幂等注入（WebView 重建自动重挂；页面侧 IIFE 双重幂等）。
        //    尾分号必须去掉：括号内分号=SyntaxError（实机二轮定位）。
        var boot = _bootstrap.TrimEnd(';', ' ', '\r', '\n', '\t');
        // 逗号表达式尾部带自证标记：回执参数可判注入成败（CQ_OK/CQ_BAD）
        web.ExecuteJavaScript("(window.__CQ__||(" + boot + "),window.__CQ__?'CQ_OK':'CQ_BAD')", Ack);

        // ② 快照推送：签名变化才推（含精力/可做件数/刚完成标记；制作中百分比由 js 本地补间）
        var snap = QueueState.SnapshotJson();
        var sig = (int)QueueState.Stamina + "|" + QueueState.CanMake + "|" + QueueState.StaminaPaused + "|" +
                  QueueState.DrawerUsed + "|" + QueueState.JustFinishedTaskId + "|" +
                  QueueState.Tasks.Count + "|" + (QueueState.Tasks.Count > 0 ? QueueState.Tasks[0].TaskId + ":" + (int)QueueState.Tasks[0].Status + ":" + QueueState.Tasks[0].Left : "");
        if (sig == _lastSig) return;
        _lastSig = sig;
        QueueState.JustFinishedTaskId = 0;      // 已入快照：消费即清（下轮不再误报）
        web.ExecuteJavaScript("window.__CQ__&&__CQ__.setData(" + QuoteJs(snap) + ")", Ack);
    }

    /// <summary>js 命令消费：[{"op":"enqueue","qty":n}] / [{"op":"cancel","id":n}]。</summary>
    private static void OnDrain(string result)
    {
        try
        {
            if (string.IsNullOrEmpty(result) || result == "[]" || result == "\"\"") return;
            var cmds = ParseCommands(result);
            foreach (var (op, seg) in cmds)
            {
                if (op == "enqueue")
                {
                    var qty = ExtractInt(seg, "qty");
                    if (QueueRunner.Enqueue(qty <= 0 ? 1 : qty, out var failMsg))
                        Ac_PopText_AddPopText.SendAction("已加入制作队列（材料已锁进抽屉）");
                    else
                        Ac_PopText_AddPopText.SendAction("入队失败：" + failMsg);
                }
                else if (op == "cancel")
                {
                    var id = ExtractInt(seg, "id");
                    if (id > 0) QueueRunner.Cancel(id, out _);
                }
            }
            if (cmds.Count > 0) PushSnapshotNow();
        }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Ui] 命令解析异常: " + e.Message + " raw=" + result); }
    }

    /// <summary>零依赖解析 drain JSON 数组（元素 {op,qty|id|key}）。粗解析容错，返回 (op, 原始段)。</summary>
    private static System.Collections.Generic.List<(string op, string seg)> ParseCommands(string json)
    {
        var list = new System.Collections.Generic.List<(string, string)>();
        int i = 0;
        while (i < json.Length)
        {
            int op = json.IndexOf("\"op\"", i, StringComparison.Ordinal);
            if (op < 0) break;
            int q1 = json.IndexOf('"', op + 4);
            int q2 = q1 >= 0 ? json.IndexOf('"', q1 + 1) : -1;
            if (q2 < 0) break;
            var name = json.Substring(q1 + 1, q2 - q1 - 1);
            int end = json.IndexOf('}', q2);
            if (end < 0) end = json.Length - 1;
            var seg = json.Substring(op, end - op);
            list.Add((name, seg));
            i = end;
        }
        return list;
    }

    private static string ExtractStr(string json, string field)
    {
        var tag = "\"" + field + "\"";
        int p = json.IndexOf(tag, StringComparison.Ordinal);
        if (p < 0) return "";
        int c = json.IndexOf(':', p + tag.Length);
        if (c < 0) return "";
        int q1 = json.IndexOf('"', c + 1);
        if (q1 < 0) return "";
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return "";
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    private static Vuplex.WebView.IWebView GetWebView()
    {
        try
        {
            if (!BaseSingleton<ReduxUISystem>.IsInstanceCreated) return null;
            var rus = BaseSingleton<ReduxUISystem>.Instance;
            var layer = rus?.GetWebUILayer();
            return layer?.canvasWebViewPrefab?.WebView;
        }
        catch { return null; }
    }

    /// <summary>ToolTable state（界面未开=null，DrawerTabs.GetStateByUI 同款非泛型路径）。</summary>
    private static State_Web_ToolTable ToolTableState()
    {
        try
        {
            if (!BaseSingleton<ReduxUISystem>.IsInstanceCreated) return null;
            var rus = BaseSingleton<ReduxUISystem>.Instance;
            return rus.GetStateByUI(ToolTableStateType)?.TryCast<State_Web_ToolTable>();
        }
        catch { return null; }
    }

    /// <summary>当前打开工作台的家具 id（台面现料源；界面未开=0）。</summary>
    internal static long CurrentToolTableFurnitureId()
    {
        try { return ToolTableState()?.CurrentFurnitureId ?? 0; } catch { return 0; }
    }

    /// <summary>取消/入队后立即回推快照（不等 0.5s 签名周期）。</summary>
    internal static void PushSnapshotNow()
    {
        try
        {
            var web = GetWebView();
            if (web == null) return;
            var snap = QueueState.SnapshotJson();
            _lastSig = "";                                                      // 强制下轮也推
            QueueState.JustFinishedTaskId = 0;
            web.ExecuteJavaScript("window.__CQ__&&__CQ__.setData(" + QuoteJs(snap) + ")", Ack);
        }
        catch { }
    }

    internal static int ExtractInt(string json, string field)
    {
        var tag = "\"" + field + "\"";
        int p = json.IndexOf(tag, StringComparison.Ordinal);
        if (p < 0) return 0;
        int c = json.IndexOf(':', p + tag.Length);
        if (c < 0) return 0;
        int s = c + 1;
        while (s < json.Length && (char.IsWhiteSpace(json[s]) || json[s] == '"')) s++;
        int v = 0; bool any = false;
        while (s < json.Length && char.IsDigit(json[s])) { v = v * 10 + (json[s] - '0'); s++; any = true; }
        return any ? v : 0;
    }

    /// <summary>JSON 文本作为 JS 字符串字面量（双引号包裹）。</summary>
    private static string QuoteJs(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 8);
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(ch); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string ReadBootstrap()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetName().Name + ".queueui.page.js";
            using var sr = new StreamReader(asm.GetManifestResourceStream(name));
            return sr.ReadToEnd();
        }
        catch (Exception e)
        {
            CraftQueuePlugin.Log.LogWarning("[CraftQueue.Ui] queueui.page.js 资源读取失败: " + e.Message);
            return null;
        }
    }
}
