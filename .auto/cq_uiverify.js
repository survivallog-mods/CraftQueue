// CraftQueue UI 层自动验证（CDP）：SelfTest 开工作台 UI 后接管，模拟完整用户流。
// 断言：胶囊/按钮挂载、点配方摆料、加入队列、队列面板出任务、官方制作遮罩被压制。
const { chromium } = require('C:/temp/baidu_up/node_modules/playwright-core');

const sleep = ms => new Promise(r => setTimeout(r, ms));

(async () => {
    const browser = await chromium.connectOverCDP('http://localhost:9222');
    const page = browser.contexts()[0].pages()[0];

    const findTT = () => page.evaluate(() => {
        for (const f of document.querySelectorAll('iframe')) {
            if (f.id === 'ToolTable' && f.contentDocument && f.contentDocument.getElementById('btn-craft'))
                return { ok: true };
        }
        return { ok: false };
    });

    // 等 ToolTable iframe（最多 60s）
    let tt = { ok: false };
    for (let i = 0; i < 30 && !tt.ok; i++) { await sleep(2000); tt = await findTT(); }
    if (!tt.ok) { console.log(JSON.stringify({ FAIL: 'ToolTable iframe 未出现' })); process.exit(1); }

    const R = {};
    // ① mod 元素挂载
    R.dom = await page.evaluate(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        const d = f.contentDocument;
        return {
            headBtn: !!d.getElementById('cq-head-btn'),
            badge: (d.getElementById('cq-badge') || {}).textContent || null,
            enqueueBtn: !!d.getElementById('cq-enqueue'),
            panel: !!d.getElementById('cq-panel'),
            forceStyle: !!d.getElementById('cq-force-style'),
        };
    });
    // ② 点第一个可点配方（can-craft 优先，锁定跳过）→ 台面摆料
    R.recipeClick = await page.evaluate(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        const d = f.contentDocument;
        const items = [...d.querySelectorAll('.recipe-item.can-craft, #recipe-list .recipe-item')];
        const el = items.find(x => !x.className.includes('craft-locked')) || items[0];
        if (!el) return 'NO-RECIPE';
        el.click();
        return 'CLICKED:' + (el.textContent || '').trim().slice(0, 16);
    });
    await sleep(1200);
    // ③ 台面物品数 + qty 值 + 点「加入队列」
    R.bench = await page.evaluate(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        const d = f.contentDocument;
        const benchCount = d.querySelectorAll('#workbench-grid .grid-item, #workbench-grid [class*=item]').length;
        const qty = (d.getElementById('qty-input') || {}).value;
        const btn = d.getElementById('cq-enqueue');
        if (btn) btn.click();
        return { benchCount, qty, clicked: !!btn };
    });
    await sleep(2500);
    // ④ 队列面板状态 + 官方遮罩压制
    R.queue = await page.evaluate(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        const d = f.contentDocument;
        return {
            badge: (d.getElementById('cq-badge') || {}).textContent,
            panelOpen: d.getElementById('cq-panel') && d.getElementById('cq-panel').classList.contains('open'),
            tasks: [...d.querySelectorAll('.cq-task .cq-name')].map(e => e.textContent),
            taskStatus: [...d.querySelectorAll('.cq-task .cq-stt .s')].map(e => e.textContent),
            officialOverlay: (ov => ov ? getComputedStyle(ov).display : 'none')(d.getElementById('crafting-overlay')),
            drawerLabel: (d.getElementById('cq-dv') || {}).textContent,
        };
    });
    // ⑤ 胶囊点开面板
    R.headToggle = await page.evaluate(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        const d = f.contentDocument;
        const b = d.getElementById('cq-head-btn');
        if (b) b.click();
        return d.getElementById('cq-panel').classList.contains('open');
    });
    await sleep(1000);
    R.queueAfter = await page.evaluate(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        const d = f.contentDocument;
        return {
            tasks: [...d.querySelectorAll('.cq-task .cq-name')].map(e => e.textContent),
            status: [...d.querySelectorAll('.cq-task .cq-stt .s')].map(e => e.textContent),
        };
    });
    console.log(JSON.stringify(R, null, 1));
    await browser.close();
})().catch(e => { console.error('FAIL', e.message); process.exit(1); });
