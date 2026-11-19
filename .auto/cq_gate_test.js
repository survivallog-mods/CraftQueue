// CraftGate 误拦验证（CDP）：入队制造预占+Dispatched → 制作中点不冲突配方 → 观察摆料/拦截/精力
const { chromium } = require('C:/temp/baidu_up/node_modules/playwright-core');
const sleep = ms => new Promise(r => setTimeout(r, ms));
(async () => {
    const browser = await chromium.connectOverCDP('http://localhost:9222');
    const page = browser.contexts()[0].pages()[0];
    const getDoc = () => page.evaluate(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        return f ? { doc: f.contentDocument, ok: true } : { ok: false };
    });
    const snap = () => page.evaluate(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        if (!f) return { err: 'NO-TT' };
        const d = f.contentDocument;
        return {
            t: new Date().toISOString().slice(11, 19),
            stamina: (d.getElementById('cq-stv') || {}).textContent || '',
            drawer: (d.getElementById('cq-dv') || {}).textContent || '',
            tasks: [...d.querySelectorAll('.cq-task')].map(e => (e.querySelector('.cq-name') || {}).textContent + ':' + (e.querySelector('.cq-stt .s') || {}).textContent),
            bench: d.querySelectorAll('#workbench-grid .grid-item, #workbench-grid [class*=item]').length,
            popText: (document.querySelector('.pop-text, [class*=poptext]') || {}).textContent || '',
        };
    });
    const listRecipes = () => page.evaluate(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        if (!f) return [];
        const d = f.contentDocument;
        return [...d.querySelectorAll('.recipe-item.can-craft')].filter(x => !x.className.includes('craft-locked'))
            .slice(0, 8).map(x => (x.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 24));
    });
    const clickRecipe = (idx) => page.evaluate((i) => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        const d = f.contentDocument;
        const items = [...d.querySelectorAll('.recipe-item.can-craft')].filter(x => !x.className.includes('craft-locked'));
        if (!items[i]) return 'NO-' + i;
        items[i].click(); return 'CLICKED:' + (items[i].textContent || '').trim().slice(0, 16);
    }, idx);
    const enqueue = () => page.evaluate(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        const btn = f.contentDocument.getElementById('cq-enqueue');
        if (!btn) return 'NO-BTN';
        btn.click(); return 'CLICKED';
    });
    const log = [];
    if (!(await getDoc()).ok) { console.log(JSON.stringify({ FAIL: 'ToolTable UI 未开' })); process.exit(1); }
    log.push({ step: 'base', ...(await snap()) });
    log.push({ step: 'recipes', list: await listRecipes() });
    // ① 点配方0 摆料 → 入队（制造预占+Dispatched）
    log.push({ step: 'r0:' + await clickRecipe(0) });
    await sleep(1500);
    log.push({ step: 'after-r0', ...(await snap()) });
    log.push({ step: 'enqueue:' + await enqueue() });
    await sleep(2500);
    log.push({ step: 'after-enqueue', ...(await snap()) });
    // ② 制作中：点不同配方（1、2）观察是否放行
    log.push({ step: 'r1:' + await clickRecipe(1) });
    await sleep(1500);
    log.push({ step: 'after-r1', ...(await snap()) });
    log.push({ step: 'r2:' + await clickRecipe(2) });
    await sleep(1500);
    log.push({ step: 'after-r2', ...(await snap()) });
    console.log(JSON.stringify(log, null, 1));
    await browser.close();
})().catch(e => { console.error('FAIL', e.message); process.exit(1); });
