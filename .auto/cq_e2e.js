// E2E：进档 → SelfTest 自动入队×2 → 制作中点配方（同/异）→ 入队 → 抽屉/精力/不卡 全链取证
const { chromium } = require('C:/temp/baidu_up/node_modules/playwright-core');
const sleep = ms => new Promise(r => setTimeout(r, ms));
(async () => {
    const browser = await chromium.connectOverCDP('http://localhost:9222');
    const page = browser.contexts()[0].pages()[0];
    const ev = fn => page.evaluate(fn);
    // ① 等主菜单 → 点继续生存
    let loaded = false;
    for (let i = 0; i < 40 && !loaded; i++) {
        await sleep(6000);
        loaded = await ev(() => {
            const cont = [...document.querySelectorAll('.menu-item')].find(e => (e.textContent || '').includes('继续生存'));
            if (cont) { cont.click(); return true; }
            return false;
        }).catch(() => false);
    }
    console.log(JSON.stringify({ step: 'enter-save', ok: loaded }));
    if (!loaded) process.exit(1);
    // ② 等 SelfTest 入队×2（进档后 ~20s）+ 工作台 UI 自动打开
    let tt = null;
    for (let i = 0; i < 30 && !tt; i++) {
        await sleep(5000);
        tt = await ev(() => {
            const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
            return (f && f.contentDocument && f.contentDocument.getElementById('btn-craft')) ? 1 : null;
        }).catch(() => null);
    }
    console.log(JSON.stringify({ step: 'ui-open', ok: !!tt }));
    const snap = () => ev(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        if (!f) return { err: 'NO-TT' };
        const d = f.contentDocument;
        return {
            t: new Date().toISOString().slice(11, 19),
            stamina: (d.getElementById('cq-stv') || {}).textContent || '',
            drawer: (d.getElementById('cq-dv') || {}).textContent || '',
            tasks: [...d.querySelectorAll('.cq-task')].map(e => (e.querySelector('.cq-name') || {}).textContent + ':' + (e.querySelector('.cq-stt .s') || {}).textContent),
            bench: d.querySelectorAll('#workbench-grid .grid-item, #workbench-grid [class*=item]').length,
        };
    });
    const clickRecipe = idx => ev(i => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        const d = f.contentDocument;
        const items = [...d.querySelectorAll('.recipe-item.can-craft')].filter(x => !x.className.includes('craft-locked'));
        if (!items[i]) return 'NO-' + i;
        items[i].click();
        return 'CLICKED:' + (items[i].textContent || '').trim().replace(/\s+/g, ' ').slice(0, 18);
    }, idx);
    const enqueue = () => ev(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        const btn = f.contentDocument.getElementById('cq-enqueue');
        if (!btn) return 'NO-BTN';
        btn.click(); return 'CLICKED';
    });
    const log = [];
    log.push({ step: 'base', ...(await snap()) });
    // ③ 制作中点异配方（idx 0）→ 期望放行摆料（修复①验证）
    log.push({ step: 'r0:' + await clickRecipe(0) });
    await sleep(1800);
    log.push({ step: 'after-r0', ...(await snap()) });
    // ④ 台面有料直接入队（路径A）→ 材料进抽屉
    log.push({ step: 'enqueue:' + await enqueue() });
    await sleep(2500);
    log.push({ step: 'after-enqueue', ...(await snap()) });
    // ⑤ 清台面（官方清空按钮）后点配方再入队（路径B key 直取）
    await ev(() => {
        const f = [...document.querySelectorAll('iframe')].find(x => x.id === 'ToolTable');
        const d = f.contentDocument;
        const clear = [...d.querySelectorAll('button')].find(b => (b.textContent || '').includes('清空'));
        if (clear) clear.click();
        return 'cleared';
    });
    await sleep(1000);
    log.push({ step: 'r1:' + await clickRecipe(1) });
    await sleep(1800);
    log.push({ step: 'after-r1', ...(await snap()) });
    // 若 r1 被拦台面空，直接入队 → 路径B 直取（修复②验证）
    const s1 = log[log.length - 1];
    if (!s1.err) {
        log.push({ step: 'enqueue2:' + await enqueue() });
        await sleep(2500);
        log.push({ step: 'after-enqueue2', ...(await snap()) });
    }
    // ⑥ 跟踪 40s：完成行为+精力+不卡
    for (let i = 0; i < 13; i++) {
        await sleep(3000);
        log.push({ step: 'w' + i, ...(await snap()) });
    }
    console.log(JSON.stringify(log, null, 1));
    await browser.close();
})().catch(e => { console.error('FAIL', e.message); process.exit(1); });
