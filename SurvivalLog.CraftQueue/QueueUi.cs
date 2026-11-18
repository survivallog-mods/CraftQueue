using System;
using System.IO;
using System.Reflection;
using GameCore.HotUpdate;
using GameCore.HotUpdate.ReduxUI;

namespace SurvivalLog.CraftQueue;

/// <summary>
/// WebUI 注入（spec §5.7）：queueui.page.js bootstrap + 队列快照推送 + Btn_Craft 包装通道。
///
/// 通道取证（CabinetEverywhere RailUi 同源）：
/// - WebView = ReduxUISystem.Instance.GetWebUILayer().canvasWebViewPrefab.WebView（IWebView）；
///   ExecuteJavaScript(script, callback) 在 Root 主文档执行；bootstrap js 自跨 iframe#ToolTable。
/// - 推送纪律：状态签名变化才推（空闲不推）；js 侧签名 diff 渲染防闪烁（原型教训）。
/// - js→C#：**包装官方 Btn_Craft**（State_Web_ToolTable 的 Action&lt;string&gt; 属性，WebBind 面）：
///   页面按钮发官方 CRAFT 事件，payload 多带 cqEnqueue/cqCancel 标记字段——包装 delegate 先判标记，
///   命中=队列语义（入队/取消，吞掉），未命中=透传官方原逻辑。零新事件通道。
///   UI 重开重建 state 会重绑 Btn_Craft → 按实例引用幂等重包。
/// - 官方成品弹窗（UI 开着时队列任务完成）：js 检测 #crafting-result-box 出现即自动点官方
///   「收下」清窗（毫秒级先于玩家；成品此时多已被 runner 搬抽屉，收下=空操作）。
/// </summary>
internal static class QueueUi
{
    private static string _bootstrap;            // queueui.page.js 全文（EmbeddedResource 一次读）
    private static float _nextPoll;
    private static string _lastSig = "";         // 快照签名（变化才推）
    private static State_Web_ToolTable _wrappedState;   // 已包装 Btn_Craft 的 state 实例
    private static Il2CppSystem.Action<string> _origCraft;

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
        if (_bootstrap == null) _bootstrap = ReadBootstrap();
        if (_bootstrap == null) return;

        // ① bootstrap 幂等注入（WebView 重建自动重挂；页面侧 IIFE 双重幂等）
        web.ExecuteJavaScript("window.__CQ__||(" + _bootstrap + ")");

        // ② Btn_Craft 包装（ToolTable state 存在=界面开着）
        WrapBtnCraft();

        // ③ 快照推送：签名变化才推
        var snap = QueueState.SnapshotJson();
        var sig = QueueState.StaminaPaused + "|" + QueueState.DrawerUsed + "|" +
                  QueueState.Tasks.Count + "|" + (QueueState.Tasks.Count > 0 ? QueueState.Tasks[0].TaskId + ":" + (int)QueueState.Tasks[0].Status + ":" + QueueState.Tasks[0].Left : "");
        // 注意：单任务快照签名只覆盖队首进度（left/status），制作中进度百分比不推（js 端本地动画补间）
        if (sig == _lastSig) return;
        _lastSig = sig;
        web.ExecuteJavaScript("window.__CQ__&&__CQ__.setData(" + QuoteJs(snap) + ")");
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

    private static readonly Il2CppSystem.Type ToolTableStateType =
        Il2CppInterop.Runtime.Il2CppType.From(typeof(State_Web_ToolTable));

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

    private static void WrapBtnCraft()
    {
        var st = ToolTableState();
        if (st == null) { _wrappedState = null; _origCraft = null; return; }   // 界面关：重置（重开重建 state 必重包）
        if (ReferenceEquals(st, _wrappedState)) return;                        // 已包
        var orig = st.Btn_Craft;
        if (orig == null) return;                                              // 官方未绑定完，下轮再试
        _wrappedState = st;
        _origCraft = orig;
        var wrapped = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string>>(
            new Action<string>(json => HandleCraft(json, _origCraft)));
        st.Btn_Craft = wrapped;
        if (CraftQueuePlugin.CfgVerbose.Value)
            CraftQueuePlugin.Log.LogInfo("[CraftQueue.Ui] Btn_Craft 已包装（队列通道）");
    }

    /// <summary>CRAFT 事件包装：cqEnqueue/cqCancel 标记=队列语义；否则透传官方。</summary>
    private static void HandleCraft(string json, Il2CppSystem.Action<string> orig)
    {
        try
        {
            if (!string.IsNullOrEmpty(json) && json.Contains("cqCancel"))
            {
                var id = ExtractInt(json, "cqCancel");
                if (id > 0)
                {
                    QueueRunner.Cancel(id, out _);
                    QueueUi.PushSnapshotNow();   // 取消立即回推
                }
                return;
            }
            if (!string.IsNullOrEmpty(json) && json.Contains("cqEnqueue"))
            {
                var qty = ExtractInt(json, "qty");
                var recipeId = CraftGate.LastRecipeId;
                if (recipeId <= 0)
                {
                    Ac_PopText_AddPopText.SendAction("请先在制造笔记选择配方");
                    return;
                }
                if (QueueRunner.Enqueue(recipeId, qty <= 0 ? 1 : qty, out var failMsg))
                    Ac_PopText_AddPopText.SendAction("已加入制作队列（材料已锁进抽屉）");
                else
                    Ac_PopText_AddPopText.SendAction("入队失败：" + failMsg);
                QueueUi.PushSnapshotNow();
                return;
            }
        }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Ui] 队列事件处理异常: " + e.Message); }
        try { orig?.Invoke(json); } catch { }                                   // 透传官方
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
            web.ExecuteJavaScript("window.__CQ__&&__CQ__.setData(" + QuoteJs(snap) + ")");
        }
        catch { }
    }

    private static int ExtractInt(string json, string field)
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
