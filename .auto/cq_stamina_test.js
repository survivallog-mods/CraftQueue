// 精力消耗受控实验（CDP）：点配方→读精力→入队→跟踪派发/完成全程精力变化
const { chromium } = require('C:/temp/baidu_up/node_modules/playwright-core');
const sleep = ms => new Promise(r => setTimeout(r, ms));
(async () => {
    const browser = await chromium.connectOverCDP('http://localhost:9222');
    const page = browser.contexts()[0].pages()[0];
    const frame = () => [...page.frames()].find(f => {
        try { return f.name() === '' && f.url() === page.url(); } catch { return false; }
    }) ? page.mainFrame() : null;
    // 直接用 evaluate 在主文档找 iframe
    const ev = (fn, arg) => page.evaluate(fn, arg);
    const snap = () => ev(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        if (!f) return null;
        const d = f.contentDocument;
        return {
            t: new Date().toISOString().slice(11, 19),
            stamina: (d.getElementById('cq-stv') || {}).textContent || '',
            tasks: [...d.querySelectorAll('.cq-task')].map(e => (e.querySelector('.cq-name') || {}).textContent + ':' + (e.querySelector('.cq-stt .s') || {}).textContent + ':' + (e.querySelector('.cq-cnt') || {}).textContent),
            bench: d.querySelectorAll('#workbench-grid .grid-item, #workbench-grid [class*=item]').length,
            drawer: (d.getElementById('cq-dv') || {}).textContent || '',
        };
    });
    const clickRecipe = () => ev(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        const d = f.contentDocument;
        const items = [...d.querySelectorAll('.recipe-item.can-craft')].filter(x => !x.className.includes('craft-locked'));
        if (!items.length) return 'NO-RECIPE';
        items[0].click();
        return items[0].textContent.trim().slice(0, 20);
    });
    const enqueue = () => ev(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        const btn = f.contentDocument.getElementById('cq-enqueue');
        if (!btn) return 'NO-BTN';
        btn.click(); return 'CLICKED';
    });
    const log = [];
    const S0 = await snap(); log.push({ step: 'baseline', ...S0 });
    const rc = await clickRecipe(); await sleep(1500);
    const S1 = await snap(); log.push({ step: 'recipe:' + rc, ...S1 });
    const eq = await enqueue(); await sleep(2000);
    log.push({ step: 'enqueue:' + eq, ...(await snap()) });
    // 跟踪 60s：每 3s 快照（精力变化+任务状态+抽屉）
    for (let i = 0; i < 20; i++) {
        await sleep(3000);
        log.push({ step: 'watch' + i, ...(await snap()) });
        const s = log[log.length - 1];
        if (s.tasks.length === 0 && i > 3) break;   // 队列清空=完成
    }
    console.log(JSON.stringify(log, null, 1));
    await browser.close();
})().catch(e => { console.error('FAIL', e.message); process.exit(1); });
