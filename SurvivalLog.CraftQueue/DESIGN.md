# 工作台制作队列 CraftQueue — 设计文档

> 本文档随源码存放在 mod 自己的文件夹内；每次迭代改动必须同步更新本文档。
> 权威设计（含全部反汇编取证与方案取舍）：`../docs/superpowers/specs/2026-11-16-craft-queue-design.md`
> 实现计划：`../docs/superpowers/plans/2026-11-18-craft-queue.md`
> 交互原型（定稿参考）：`../design-proto/queue-panel.html`

## 1. 功能定位

工作台制作任务排队后台执行：入队即锁料进工作台抽屉，关界面继续制作，成品自动入抽屉
（满则落地），精力不足整队挂起。官方前台制作（「制造」按钮）零改动，与队列双路径并行。

## 2. 机制链（四行版）

- 计时=官方 `ToolTableManager.Update`（BattleLogicWorld 每帧，unscaledDeltaTime，与 UI 无关）；
  mod 只做调度：`StartProduction(furnId, 抽屉物品ids)` → 完成检测 → 成品搬抽屉/落地。
- 预占=入队时按官方取料口径（台面→页签→背包→抽屉→柜子）选料搬进抽屉，物品 id 进
  `ReservedItemIds` 集合；队列取料与前台拦截都以「刨除预占」为口径，队列内永不超发。
- 前台拦截（E 方案）=Harmony prefix `Ac_ToolTable_Make/CraftAgain.SendAction`：
  刨除预占后材料不够本次 → 拦截+提示「材料已被队列任务占用」。
- 红线：不改存档、不改游戏文件；队列与预占集合为会话内存，读档后队列清空（抽屉料仍在）。

## 3. 模块

| 文件 | 职责 |
|---|---|
| CraftQueuePlugin.cs | 入口：cfg、Ticker 注册（AccessTicker 同款） |
| QueueState.cs | 任务/预占集合数据模型 + 队列快照 JSON |
| MaterialPicker.cs | 官方口径取料选择器（预占排除/变体拒绝/搬抽屉） |
| QueueRunner.cs | 串行 runner：派发/完成收尾/落地/精力挂起/取消 |
| CraftGate.cs | 前台制作拦截（Make/CraftAgain 双入口） |
| QueueUi.cs + queueui.page.js | webui 注入：入队按钮/队列胶囊/队列面板 |

（各模块随任务落地时补细节节。）
