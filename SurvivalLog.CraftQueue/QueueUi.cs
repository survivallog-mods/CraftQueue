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
    private static Il2CppSystem.Action<string> _probeCb;
    private static bool _live;                              // js 存活（CQ_OK 确认；探针失守即重注全量）
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
                if (r == "CQ_OK") _live = true;
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

        // ① 注入探活制（卡顿根因修复 2026-11-19）：此前每 0.5s 重发 ~40KB bootstrap 全文，
        // WebView2 脚本队列被持续塞爆 → 前台 HUD/界面卡顿变形 + CE 推送通道饥饿（日志实证）。
        // 改 CE RailUi 同款纪律：存活期每轮只发几十字节探针；探活失败才发一剂全量（尾分号
        // 必须去掉——括号内分号=SyntaxError，实机二轮定位）。
        if (_live)
        {
            if (_probeCb == null)
                _probeCb = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string>>(
                    new Action<string>(r => { if (r != "1") _live = false; }));
            try { web.ExecuteJavaScript("window.__CQ__?1:0", _probeCb); } catch { _live = false; }
        }
        else
        {
            var boot = _bootstrap.TrimEnd(';', ' ', '\r', '\n', '\t');
            try { web.ExecuteJavaScript("(window.__CQ__||(" + boot + "),window.__CQ__?'CQ_OK':'CQ_BAD')", Ack); } catch { }
        }

        // ①b 弹窗/遮罩推送层拦截（唯一防线，无兜底）：订阅官方 IsCrafting/ResultShow 两个
        // ReactiveProperty——官方写 true 的同帧回调里，若判定为队列台的制作/完成（当前台不在
        // 玩家自己制作且非 WaitingReceive=玩家自己刚做完），立即写回 false。webui 推送是脏标记
        // 批量机制（当帧稍后 flush），flush 时值已是 false → 前台从头到尾收不到，弹窗不渲染。
        // 自己写回 false 会再次触发订阅（e=false）→ 自然终止，无重入。
        WatchStateProps();

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
                    var key = ExtractStr(seg, "key");
                    if (QueueRunner.Enqueue(qty <= 0 ? 1 : qty, key, out var failMsg))
                        Ac_PopText_AddPopText.SendAction("已加入制作队列（材料已锁进抽屉）");
                    else
                    {
                        CraftQueuePlugin.Log.LogWarning("[CraftQueue.Ui] 入队失败: " + failMsg);
                        Ac_PopText_AddPopText.SendAction("入队失败：" + failMsg);
                    }
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

    internal static State_Web_ToolTable ToolTableStatePublic() => ToolTableState();

    private static State_Web_ToolTable _watchedState;      // 已订阅的 state 实例（UI 重开重建即重订）
    private static readonly System.Collections.Generic.List<Il2CppSystem.IDisposable> _subs = new();

    /// <summary>订阅官方弹窗开关（幂等；UI 重开自动重订）。拦截判定见调用点注释。</summary>
    private static void WatchStateProps()
    {
        try
        {
            var st = ToolTableState();
            if (st == null)
            {
                if (_watchedState != null) { foreach (var d in _subs) d?.Dispose(); _subs.Clear(); _watchedState = null; }
                return;
            }
            if (ReferenceEquals(st, _watchedState)) return;      // 已订阅
            foreach (var d in _subs) d?.Dispose();
            _subs.Clear();
            _watchedState = st;
            var cb = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(
                new Action<bool>(v => { if (v) OnOfficialPopupSwitch(); }));
            Il2CppSystem.IDisposable d1 = null, d2 = null;
            // 审查修1：AnonymousRObserver<bool> 是 AOT 未实例化的值类型泛型组合（运行时构造可能抛异常
            // 被吞=拦截静默失效）；改用 ObservableSubscribeExtensions.Subscribe<bool>(source, Action)
            // ——dump 实证该组合游戏自身实例化过（RVA 0xDBBB00），AOT 安全。
            try { if (st.IsCrafting != null) d1 = R3.ObservableSubscribeExtensions.Subscribe<bool>(st.IsCrafting, cb); }
            catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Ui] IsCrafting 订阅失败: " + e.Message); }
            try { if (st.ResultShow != null) d2 = R3.ObservableSubscribeExtensions.Subscribe<bool>(st.ResultShow, cb); }
            catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Ui] ResultShow 订阅失败: " + e.Message); }
            if (d1 != null) _subs.Add(d1);
            if (d2 != null) _subs.Add(d2);
        }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Ui] 弹窗订阅异常: " + e.Message); }
    }

    /// <summary>官方弹窗开关被置 true（同帧）：判定来源，队列相关则写回 false 掐断推送。</summary>
    private static void OnOfficialPopupSwitch()
    {
        try
        {
            // 队列在制 or 刚完成（3s 窗）
            bool qActive = QueueState.JustDoneAt > 0 && UnityEngine.Time.unscaledTime - QueueState.JustDoneAt < 3f;
            for (int i = 0; i < QueueState.Tasks.Count; i++)
                if (QueueState.Tasks[i].Status == TaskStatus.Dispatched) { qActive = true; break; }
            if (!qActive) return;
            // 玩家前台特征：当前台在制作（自己做）或 WaitingReceive（自己刚做完要收下）→ 不拦
            var st = _watchedState;
            if (st == null) return;
            var world = GameCore.HotUpdate.BaseSingleton<GameCore.HotUpdate.Battle.Logic.BattleLogicWorld>.Instance;
            var ttm = world?._ToolTableManager;
            if (ttm == null) return;
            var cur = ttm.GetToolTableState(st.CurrentFurnitureId);
            // 玩家前台事件保护：Making/WaitingReceive/NoReceive 全态（审查补：NoReceive=玩家
            // 自己的失败弹窗，缺保护会被误吞——既有缺口随本次一并收口）
            if (cur == GameCore.HotUpdate.HandMadeState.Making || cur == GameCore.HotUpdate.HandMadeState.WaitingReceive
                || cur == GameCore.HotUpdate.HandMadeState.NoReceive)
                return;
            // 队列台事件：同帧掐断
            st.IsCrafting.Value = false;
            st.ResultShow.Value = false;
            // HandMadeState 同帧写回 Idle（2026-11-19 取证）：官方 RA_CheckResult（开工）/
            // RA_Result（完成）均无视 FurnitureId 直写全局单例 state.HandMadeState（0x2D327E4
            // 写 1 / 0x2D38F77 写 action.State）——队列台一动就污染前台台状态；RA_RecipeClick
            // 与 RA_Make 入口都检查 HandMadeState!=Idle 直接拒（0x2D381DE / 0x2D35EFC，连提示
            // 都没有）=「制作中点配方取料/开始制作无响应」。两处污染写都在 IsCrafting/ResultShow
            // RP 写 true（同步触发本回调）之前 → 写回必为终值；同帧稍后 webui flush 序列化时
            // 值已是 Idle。
            st.HandMadeState = GameCore.HotUpdate.HandMadeState.Idle;
            if (CraftQueuePlugin.CfgVerbose.Value)
                CraftQueuePlugin.Log.LogInfo("[CraftQueue.Ui] 队列弹窗已在推送层掐断");
        }
        catch (Exception e) { CraftQueuePlugin.Log.LogWarning("[CraftQueue.Ui] 弹窗拦截异常: " + e.Message); }
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
