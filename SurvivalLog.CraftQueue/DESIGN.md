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

### QueueState 细节

- `CraftTask`：配方 × 数量逐件派发；`ReservedItemIds` 只记当前这件（下一件轮到时重取，避免一次占满抽屉）。
- `QueueState.ReservedItemIds` = 全任务并集，`SyncReservedIds()` 重建；取料/拦截/派发校验三处统一用它。
- 快照协议（`SnapshotJson()`，与原型 js 字段对齐）：
  `{"stamina","staminaMax","paused","drawerUsed","drawerCap","tasks":[{id,name,icon,left,total,status,reason,furniture}]}`，
  status ∈ queued/making/blocked/done（Dispatched→making）。手写拼接零依赖，中文原样、引号反斜杠转义。
- 单测：`tests/QueueStateTests`（零依赖 console，`dotnet run`，非零退出码=失败）。
| MaterialPicker.cs | 官方口径取料选择器（预占排除/变体拒绝/搬抽屉） |

### MaterialPicker 细节

- `PickAndReserve(recipeId, out PickFail)`：需求计数（`MaterialList` 重复出现=数量，表实证）→
  逐 owner 选料（背包 `ActionDispatcher.GetLeadingRoleAgentId()` → 柜子 → 抽屉剔除预占）→
  `TryQuickMoveItems(ids, src, drawerOwner, silent)` 搬抽屉（返回数≠选数=半路失败，放弃本轮不预占）。
- 变体拒绝：`DyeConfig.IsDyeMaterial(configId)` 命中任一材料 → `PickFail.Variant`（v1 口径）。
- 柜子清单：官方 `AgentManager.GetFurnituresWithBag(mapId,...)` 枚举 + 功能包排除
  （BagId 1008/1009/1010/2003 与家具 313/80033/80164/1024-1028/42033/66196/330/384/389/9126，
  CabinetEverywhere 同源实证），5s 缓存。
- `CountAvailExcludingReserved(configId)`：同口径计数，CraftGate（前台拦截）消费。
- 取料池**不含台面与页签源**（ledger Ruling：前台备料工作区保护）。
- interop 要点：`ItemData.InstanceId`（BaseEntity）为物品实例 id（跨包移动不变）；
  `GetItemDataList`/`TryQuickMoveItems` 收发 Il2CppSystem 列表。
| QueueRunner.cs | 串行 runner：派发/完成收尾/落地/精力挂起/取消 |

### QueueRunner 细节

- 0.5s 节流轮询；串行状态机顺序：在制收尾 → 精力恢复 → Blocked 补料恢复 → 派发队首。
- 派发：预占校验（`GetItemData(id).OwnerId==抽屉`，丢→Blocked）→ `ToolTableMap` 找
  `State==Idle 且台面无遗留物` 的家具 → `StartProduction(furn, ids)`（void，拒绝靠下一轮
  State 仍 Idle 判定，1s 窗口；`GetCraftStamina` 区分精力不足挂起 vs 校验失败 Blocked）。
- 收尾：`GetToolTableState≥WaitingReceive` → 台面成品 `TryQuickMoveItems` 入抽屉 → 残留
  `DropItemManager.DropItemFromBag` 逐件落地（家具包裹官方直送玄关、台面无物自然跳过）→
  **直写 `ToolTableData.State=0`+清 CachedItemIds 复位**（官方复位在 notify 订阅方/UI 层，
  队列侧等价复位；Il2CppInterop 私有成员可达）→ `Left--`，>0 重取料回 Queued。
- 取消 Dispatched：`ForceEndProduction` + 直写复位；料留抽屉（预占解除）。
- 只认自己派发的 `_busyFurnId`——官方/前台任务与读档恢复的 Making 不被队列收尾。
- 已知简化（ledger）：抽屉容量 v1 固定 120（最小档保守）；`ItemData.InstanceId` 跨包不变。
| CraftGate.cs | 前台取料拦截（RecipeClick 入口，手动 Harmony prefix） |

### CraftGate 细节

- **拦截点=RecipeClick**（实现期取证修正）：前台取料发生在点配方（RA_ToolTable_RecipeClick →
  ApplyFillRecipe），「制造」只消费台面已放料不再取料——单点覆盖主路径；CraftAgain 重取料但
  撞预占概率极低（刚做完=链上料足），派发校验兜底，v1 不拦。
- 判定：无预占零干预 → recipeKey 反查配方（官方 `ToProductionKey` 同源全表生成缓存；
  key=材料 id 排序串，反汇编实证）→ 逐材料 `CountAvailExcludingReserved ≥ 需求`，任一不足
  → 拦截（return false）+ `Ac_PopText_AddPopText` 提示；判定异常放行（官方兜底）。
- 手动 AccessTools.Method patch（CE CookingFxRefreshHook 同款），方法缺失=降级轻防御不炸。
| QueueUi.cs + queueui.page.js | webui 注入：入队按钮/队列胶囊/队列面板 |

### QueueUi 细节

- 通道：WebView=ReduxUISystem.GetWebUILayer().canvasWebViewPrefab.WebView（CE RailUi 同源）；
  ExecuteJavaScript 注入 Root 主文档，js 跨 `iframe#ToolTable` 挂 DOM（界面关=iframe 销毁，
  数据缓存重开复用）。
- **js→C# = 包装官方 `State_Web_ToolTable.Btn_Craft`**（Action&lt;string&gt; 属性）：
  页面按钮发官方 `CRAFT` 事件、payload 多带 `cqEnqueue`/`cqCancel` 标记——包装 delegate 命中即
  队列语义（入队用 CraftGate.LastRecipeId + qty；取消 taskId），未命中透传官方。零新事件通道；
  UI 重开重建 state → 按实例引用幂等重包。
- C#→js：快照签名（挂起/抽屉/任务数/队首态）变化才推 `__CQ__.setData(json)`；制作中进度条
  js 本地补间（不逐拍推送，防闪烁同原型纪律）。
- 官方成品弹窗：队列有任务时 js 检测 `#crafting-result-box` 出现自动点「收下」清窗
  （毫秒级先于玩家；成品此时多已被 runner 搬抽屉，收下=空操作）。已知边界：玩家毫秒级抢先
  手点收下→成品进背包而非抽屉（ledger）。
- 入队交互：官方数量行 qty + mod「加入队列」按钮（craft-controls 追加）+ 标题行「队列 N」胶囊
  （开/关右侧浮层面板：精力/挂起条/任务行/取消/抽屉占用）。

（各模块随任务落地时补细节节。）
