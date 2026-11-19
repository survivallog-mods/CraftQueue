// CraftQueue 现场快照（CDP 只读）：队列面板 + 抽屉/台面 + 官方精力显示
const { chromium } = require('C:/temp/baidu_up/node_modules/playwright-core');
(async () => {
    const browser = await chromium.connectOverCDP('http://localhost:9222');
    const page = browser.contexts()[0].pages()[0];
    const R = await page.evaluate(() => {
        const out = { frames: [] };
        for (const f of document.querySelectorAll('iframe')) {
            const d = f.contentDocument;
            if (!d) continue;
            const info = { id: f.id };
            // CraftQueue 面板
            const st = d.getElementById('cq-stv');
            if (st) {
                info.cqStamina = st.textContent;
                info.cqDrawer = (d.getElementById('cq-dv') || {}).textContent;
                info.tasks = [...d.querySelectorAll('.cq-task')].map(e => ({
                    name: (e.querySelector('.cq-name') || {}).textContent,
                    stt: (e.querySelector('.cq-stt .s') || {}).textContent,
                    cnt: (e.querySelector('.cq-cnt') || {}).textContent,
                    reason: (e.querySelector('.cq-note.blk') || {}).textContent,
                }));
            }
            // 官方抽屉/台面物品（workbench-grid=台面；抽屉 tab 视图）
            const bench = d.querySelectorAll('#workbench-grid .grid-item, #workbench-grid [class*=item]');
            if (bench.length) info.benchItems = bench.length;
            const drawerGrid = d.querySelector('#drawer-grid, [id*=drawer] .grid, .cabinet-content');
            if (drawerGrid) info.drawerItems = drawerGrid.querySelectorAll('.grid-item, [class*=item]').length;
            // 官方精力/能耗标签（craft-controls 区域文本）
            const ctrl = d.querySelector('.craft-controls');
            if (ctrl) info.officialCtrl = ctrl.textContent.replace(/\s+/g, ' ').slice(0, 120);
            // 数量行
            const qty = d.getElementById('qty-input');
            if (qty) info.qty = qty.value;
            if (Object.keys(info).length > 1) out.frames.push(info);
        }
        return out;
    });
    console.log(JSON.stringify(R, null, 1));
    await browser.close();
})().catch(e => { console.error('FAIL', e.message); process.exit(1); });
