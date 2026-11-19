// CraftQueue 队列面板（注入 Root 主文档，跨 iframe#ToolTable 挂 DOM）。
// 视觉 token 与渲染纪律对齐原型 design-proto/queue-panel.html（签名 diff 防闪烁）。
// 事件通道：官方 CRAFT 事件多带 cqEnqueue/cqCancel 标记 → C# QueueUi.HandleCraft 包装层。
(function () {
    'use strict';
    if (window.__CQ__) return;

    var CSS = [
        '.cq-head-btn{position:absolute;right:14px;top:16px;z-index:5;display:inline-flex;align-items:center;gap:6px;height:26px;padding:0 11px;background:linear-gradient(180deg,rgba(58,58,60,.9),rgba(38,38,40,.92));border:1px solid rgba(255,193,7,.42);border-radius:13px;color:#FFC107;font-size:12px;font-weight:600;cursor:pointer;font-family:inherit}',
        '.cq-head-btn:hover{background:rgba(255,193,7,.12);border-color:#FFC107;box-shadow:0 0 10px rgba(255,193,7,.28)}',
        '.cq-badge{min-width:16px;height:16px;border-radius:8px;padding:0 4px;background:#FFC107;color:#241a02;font-size:11px;font-weight:700;display:inline-flex;align-items:center;justify-content:center}',
        '.cq-badge.zero{background:rgba(255,193,7,.15);color:#FFC107}',
        '.cq-enqueue{color:#FFC107;border-color:rgba(255,193,7,.5);background:rgba(255,193,7,.06);font-weight:600;padding:8px 13px;font-size:12.5px;flex:none;white-space:nowrap}',
        '.cq-enqueue:hover{background:rgba(255,193,7,.14);border-color:#FFC107;color:#ffd75e}',
        '.cq-panel{position:fixed;right:14px;top:50%;transform:translateY(-50%);width:290px;max-height:76vh;z-index:60;background:linear-gradient(168deg,rgba(34,34,36,.96),rgba(20,20,22,.97));border:1px solid rgba(255,193,7,.35);border-radius:14px;box-shadow:0 16px 50px rgba(0,0,0,.65);display:none;flex-direction:column;overflow:hidden;font-family:"Microsoft YaHei",sans-serif}',
        '.cq-panel.open{display:flex}',
        '.cq-ph{display:flex;align-items:center;gap:8px;padding:12px 14px 10px;border-bottom:1px solid rgba(255,255,255,.1);color:#eee;font-size:15px;font-weight:700;letter-spacing:1px}',
        '.cq-x{margin-left:auto;background:none;border:none;color:#999;font-size:18px;cursor:pointer;line-height:1;padding:2px 6px;border-radius:5px}',
        '.cq-x:hover{color:#fff;background:rgba(255,255,255,.08)}',
        '.cq-st{padding:8px 14px;border-bottom:1px solid rgba(255,255,255,.08);font-size:11px;color:#999}',
        '.cq-st .r{display:flex;justify-content:space-between;margin-bottom:4px}.cq-st .v{color:#eee}',
        '.cq-st .bar{height:5px;border-radius:3px;background:rgba(0,0,0,.5);overflow:hidden}.cq-st .bar i{display:block;height:100%;background:linear-gradient(90deg,#4d8f4d,#6fbf73)}',
        '.cq-pause{display:none;align-items:center;gap:8px;margin:8px 14px 0;padding:7px 10px;border:1px solid rgba(224,93,93,.45);background:rgba(224,93,93,.1);border-radius:8px;font-size:12px;color:#f0a0a0}',
        '.cq-pause.show{display:flex}',
        '.cq-list{flex:1;overflow-y:auto;padding:8px 10px 12px;display:flex;flex-direction:column;gap:7px}',
        '.cq-empty{color:#999;font-size:12px;text-align:center;padding:28px 0;line-height:1.8}',
        '.cq-task{background:linear-gradient(180deg,#131316,#1d1d20);border:1px solid rgba(255,255,255,.13);border-radius:9px;padding:8px 10px 8px 12px}',
        '.cq-task.enter{animation:cqIn .25s ease-out}@keyframes cqIn{from{opacity:0;transform:translateY(-6px)}to{opacity:1;transform:none}}',
        '.cq-task.making{border-color:rgba(255,193,7,.4)}',
        '.cq-task.blocked{border-color:rgba(224,93,93,.55);background:rgba(224,93,93,.06)}',
        '.cq-top{display:flex;align-items:center;gap:8px}.cq-name{font-size:13px;font-weight:600;color:#eee}.cq-cnt{font-size:11px;color:#999}',
        '.cq-cancel{margin-left:auto;background:none;border:none;color:#999;cursor:pointer;font-size:15px;line-height:1;padding:2px 5px;border-radius:4px}',
        '.cq-cancel:hover{color:#e05d5d;background:rgba(224,93,93,.12)}',
        '.cq-stt{display:flex;align-items:center;gap:6px;margin-top:5px;font-size:11px;color:#999}',
        '.cq-dot{width:7px;height:7px;border-radius:50%;background:#999}.cq-task.making .cq-dot{background:#FFC107;box-shadow:0 0 6px #FFC107;animation:cqPulse 1s infinite}.cq-task.blocked .cq-dot{background:#e05d5d}@keyframes cqPulse{50%{opacity:.35}}',
        '.cq-task.making .cq-stt .s{color:#FFC107}.cq-task.blocked .cq-stt .s{color:#f0a0a0}',
        '.cq-bar{height:4px;border-radius:2px;background:rgba(0,0,0,.5);margin-top:6px;overflow:hidden;display:none}.cq-task.making .cq-bar{display:block}',
        '.cq-bar i{display:block;height:100%;background:linear-gradient(90deg,#b8830a,#FFC107);border-radius:2px;transition:width .4s}',
        '.cq-note{font-size:11px;margin-top:4px;color:#b7a6ff;display:none}.cq-note.on{display:block}.cq-task.blocked .cq-note.blk{display:block;color:#f0a0a0}',
        '.cq-drawer{padding:9px 14px 12px;border-top:1px solid rgba(255,255,255,.08);font-size:11px;color:#999}',
        '.cq-drawer .r{display:flex;justify-content:space-between;margin-bottom:4px}.cq-drawer .v{color:#eee}',
        '.cq-drawer .bar{height:5px;border-radius:3px;background:rgba(0,0,0,.5);overflow:hidden}.cq-drawer .bar i{display:block;height:100%;background:linear-gradient(90deg,#b8830a,#FFC107);transition:width .3s}'
    ].join('');

    var CQ = {
        data: null, sig: '', seen: {}, mounted: null, cssDone: false, cmds: [],

        // C# 每周期调用取走命令队列（返回值经 ExecuteJavaScript 回调回传——CE RailCommands 同款通道）
        drain: function () {
            if (!CQ.cmds.length) return '';
            var out = JSON.stringify(CQ.cmds);
            CQ.cmds.length = 0;
            return out;
        },

        lastKey: '',
        setData: function (jsonStr) {
            try { CQ.data = JSON.parse(jsonStr); } catch (e) { return; }
            var f = findToolTable();
            if (!f) return;                    // 界面未开：缓存，iframe 出现时 render
            mount(f.doc, f.win);
            hookSend(f.win);                   // iframe core 可能重建，每次都试挂（幂等）
            render(f.doc);
        }
    };
    window.__CQ__ = CQ;

    // 配方选中捕获：包装官方 UnitySendEvent，记录 RECIPE_CLICK 的 recipeKey（页面内可靠，
    // C# 侧 Harmony prefix 在 native 调用链未触发——实机诊断实证）
    function hookSend(win) {
        try {
            if (!win || !win.core || !win.core.UnitySendEvent || win.core.UnitySendEvent.__cq) return;
            var orig = win.core.UnitySendEvent;
            var wrapped = function (type, payload) {
                try {
                    if (type === 'RECIPE_CLICK' && payload && payload.recipeKey) CQ.lastKey = payload.recipeKey;
                } catch (e) { }
                return orig.call(win.core, type, payload);
            };
            wrapped.__cq = 1;
            win.core.UnitySendEvent = wrapped;
        } catch (e) { }
    }

    function findToolTable() {
        try {
            var fs = document.querySelectorAll('iframe');
            for (var i = 0; i < fs.length; i++) {
                if ((fs[i].id || '') !== 'ToolTable') continue;
                var doc = fs[i].contentDocument, win = fs[i].contentWindow;
                if (doc && doc.getElementById('btn-craft')) return { doc: doc, win: win };
            }
        } catch (e) { }
        return null;
    }

    function mount(doc, win) {
        var controls = doc.querySelector('.craft-controls');   // 终审 C1：官方是 class 非 id
        var h2 = doc.querySelector('.workbench-section > h2');
        if (!controls || !h2) return;
        if (!doc.getElementById('cq-style')) {                        // 终审 I10：按元素存在性（iframe 重建后重注）
            var st = doc.createElement('style'); st.id = 'cq-style'; st.textContent = CSS; doc.head.appendChild(st);
        }
        // 加入队列挂数量行尾（craft-controls 官方已有 清空/制造/能耗标签，第 4 键实测挤压变形）
        var qtyRow = doc.getElementById('qty-row');
        if (qtyRow && !doc.getElementById('cq-enqueue')) {
            var btn = doc.createElement('button');
            btn.className = 'btn cq-enqueue'; btn.id = 'cq-enqueue'; btn.textContent = '加入队列';
            btn.addEventListener('click', function () {
                var qty = 1;
                try { qty = parseInt(doc.getElementById('qty-input').value, 10) || 1; } catch (e) { }
                // key=最近点选的配方（hookSend 捕获）——C# 侧台面无料时按配方直取，入队不依赖摆料
                CQ.cmds.push({ op: 'enqueue', qty: qty, key: CQ.lastKey || '' });
            });
            qtyRow.appendChild(btn);
        }
        void controls;
        var section = h2.parentNode;                     // .workbench-section（position:relative）
        if (!doc.getElementById('cq-head-btn')) {
            var hb = doc.createElement('button');        // absolute 挂右上角：不动 h2（官方 > 选择器依赖结构）
            hb.className = 'cq-head-btn'; hb.id = 'cq-head-btn';
            hb.innerHTML = '队列 <span class="cq-badge zero" id="cq-badge">0</span>';
            hb.addEventListener('click', function () {
                var p = doc.getElementById('cq-panel'); if (p) p.classList.toggle('open');
            });
            section.appendChild(hb);
        }
        if (!doc.getElementById('cq-panel')) {
            var p = doc.createElement('div');
            p.className = 'cq-panel'; p.id = 'cq-panel';
            p.innerHTML = '<div class="cq-ph">制作队列 <span class="cq-badge zero" id="cq-pbadge">0</span>' +
                '<button class="cq-x" id="cq-x" title="收起（队列继续后台执行）">×</button></div>' +
                '<div class="cq-st" id="cq-st"><div class="r"><span>精力 · 制作消耗</span><span class="v" id="cq-stv">-</span></div><div class="bar"><i id="cq-stf"></i></div></div>' +
                '<div class="cq-pause" id="cq-pause">精力不足，队列已暂停——恢复后自动继续</div>' +
                '<div class="cq-list" id="cq-list"></div>' +
                '<div class="cq-drawer"><div class="r"><span>工作台抽屉</span><span class="v" id="cq-dv">-</span></div><div class="bar"><i id="cq-df"></i></div></div>';
            doc.body.appendChild(p);
            doc.getElementById('cq-x').addEventListener('click', function () { p.classList.remove('open'); });
        }
    }

    function render(doc) {
        var d = CQ.data; if (!d) return;
        var n = (d.tasks || []).length;
        var bd = doc.getElementById('cq-badge'), pb = doc.getElementById('cq-pbadge');
        if (!bd) return;                        // 面板还没挂上（mount 失败）
        bd.textContent = n; bd.className = 'cq-badge' + (n ? '' : ' zero');
        pb.textContent = n; pb.className = 'cq-badge' + (n ? '' : ' zero');
        doc.getElementById('cq-stv').textContent = d.stamina + ' / ' + d.staminaMax + '（还能做 ' + (d.canMake || 0) + ' 件）';
        doc.getElementById('cq-stf').style.width = (d.staminaMax > 0 ? d.stamina / d.staminaMax * 100 : 0) + '%';
        doc.getElementById('cq-pause').classList.toggle('show', !!d.paused);
        doc.getElementById('cq-dv').textContent = d.drawerUsed + ' / ' + d.drawerCap + ' 格';
        doc.getElementById('cq-df').style.width = (d.drawerCap > 0 ? d.drawerUsed / d.drawerCap * 100 : 0) + '%';

        var sig = (d.tasks || []).map(function (t) { return t.id + ':' + t.status + ':' + t.left; }).join('|');
        var list = doc.getElementById('cq-list');
        if (!n) {
            if (CQ.sig !== 'empty') { CQ.sig = 'empty'; CQ.seen = {}; list.innerHTML = '<div class="cq-empty">队列为空<br>选配方点「加入队列」，可随时关闭界面</div>'; }
            return;
        }
        if (sig !== CQ.sig) {
            CQ.sig = sig;
            var sorted = (d.tasks || []).slice().sort(function (a, b) {   // making/blocked 置顶，余按加入序
                var r = function (s) { return s === 'making' ? 0 : s === 'blocked' ? 1 : 2; };
                return r(a.status) - r(b.status);
            });
            list.innerHTML = sorted.map(function (t) {
                var cls = t.status === 'making' ? ' making' : t.status === 'blocked' ? ' blocked' : t.status === 'done' ? '' : '';
                var enter = CQ.seen[t.id] ? '' : ' enter'; CQ.seen[t.id] = 1;
                var idx = t.total - t.left + (t.status === 'done' ? 0 : 1);
                var sTxt = { queued: '排队中', making: '制作中…', blocked: '已挂起', done: '完成' }[t.status] || '';
                var note = t.furniture ? '<div class="cq-note on">家具成品将送玄关</div>' : '';
                var blk = t.reason ? '<div class="cq-note blk">' + esc(t.reason) + '——补料后自动继续</div>' : '';
                return '<div class="cq-task' + cls + enter + '" data-id="' + t.id + '">' +
                    '<div class="cq-top"><span class="cq-name">' + esc(t.name) + '</span><span class="cq-cnt">' + idx + ' / ' + t.total + '</span>' +
                    '<button class="cq-cancel" data-id="' + t.id + '" title="取消（材料留在抽屉）">×</button></div>' +
                    '<div class="cq-stt"><span class="cq-dot"></span><span class="s">' + sTxt + '</span></div>' +
                    '<div class="cq-bar"><i style="width:0"></i></div>' + note + blk + '</div>';
            }).join('');
            bindCancel(doc);
        } else {
            // 轻量：制作中任务的进度条本地推进（C# 只推结构签名）
            var mk = (d.tasks || []).filter(function (t) { return t.status === 'making'; })[0];
            if (mk) {
                var el = list.querySelector('.cq-task[data-id="' + mk.id + '"] .cq-bar i');
                if (el) { var w = Math.min(100, (parseFloat(el.style.width) || 0) + 3); el.style.width = w + '%'; }
            }
        }
    }

    function bindCancel(doc) {
        var bs = doc.querySelectorAll('.cq-cancel');
        for (var i = 0; i < bs.length; i++)
            bs[i].addEventListener('click', function (e) {
                CQ.cmds.push({ op: 'cancel', id: parseInt(e.currentTarget.getAttribute('data-id'), 10) || 0 });
            });
    }

    function send(win, payload) {
        try { if (win && win.core && win.core.UnitySendEvent) win.core.UnitySendEvent('CRAFT', payload); }
        catch (e) { }
    }
    function winOf(doc) { try { return doc.defaultView; } catch (e) { return null; } }
    function esc(s) { return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;'); }

    // 250ms 自愈轮询：①重开界面主动补挂（终审 I11）②making 进度本地推进（I12）
    // ③官方成品弹窗只在 justFinished 标志时自动收（I4/I5：不与 runner 收尾赛跑、不抢官方前台弹窗）
    setInterval(function () {
        var f = findToolTable();
        if (!f) return;
        hookSend(f.win);   // 界面重开补挂取 key 钩（iframe 重建后 setData 未到也要能捕获 RECIPE_CLICK——审查修3）
        // 界面重开补挂（iframe 重建后 setData 未到也要有按钮/面板——审查修2）+ making 进度本地推进
        if (!f.doc.getElementById('cq-head-btn') && CQ.data) { mount(f.doc, f.win); render(f.doc); return; }
        if (CQ.data && CQ.data.tasks) {
            var list = f.doc.getElementById('cq-list');
            if (list) for (var i = 0; i < CQ.data.tasks.length; i++) {
                var t = CQ.data.tasks[i];
                if (t.status !== 'making') continue;
                var el = list.querySelector('.cq-task[data-id="' + t.id + '"] .cq-bar i');
                if (el) el.style.width = Math.min(100, (parseFloat(el.style.width) || 0) + 2) + '%';
            }
        }
    }, 250);
})();
