# 工作台制作队列 CraftQueue — 设计文档

> 版本：2026-11-18 v2（用户批准：双路径/预占抽屉/前台拦截 E 方案/家具包裹走官方/不入存档）
> 取证基线：gamekb `2026-11-12_26615062`（GameAssembly.dll buildid 26615062），RVA 均来自该基线 il2cpp dump + capstone 反汇编实证。
> 状态：设计已批准，待计划与实现。

## 1. 意图（为什么做）

工作台制作目前必须守在界面里逐次点击：批量仅限同配方 ×N，且批量循环活在 webui state 里，
关界面即丢。玩家想「一次排好多个配方的制作任务，然后关界面去干别的，回来直接收成品」。

本 mod 在不动官方前台制作流程的前提下，加一条**队列侧并行路径**：入队即锁定材料，
后台串行执行，成品自动入工作台抽屉，抽屉满则掉落地面。

## 2. 目标 / 非目标 / 反目标

**目标**
- 工作台界面内可把任意配方任务（配方 × 数量）加入队列；队列面板可查看、可取消（仅此两个操作，无单任务编辑）。
- 随时关闭工作台界面，队列任务继续异步执行。
- 成品自动进工作台抽屉（全局包 5010/5012/5013），抽屉空间不足逐件掉落地面。
- 入队即扣料到抽屉并预占，队列内部永不出现「开工时材料不足」。
- 精力不足整队自动挂起，恢复后自动续跑。

**非目标**
- 不改动官方前台制作（原生「制造」按钮、弹窗「收下」进背包的流程零改动）。
- 不支持队列任务编辑（改数量、改配方、调顺序）。
- 不做多工作台并行派发（v1 串行，一次派一个）。

**反目标**
- 不改游戏存档结构、不改游戏文件（套件红线，删 dll 即还原）。
- 不 hook 官方取料链做预占剔除（轻防御，见 §5.6）。
- 不做虚拟容器（见 §4 取舍）。

## 3. 游戏机制取证（结论长期有效）

| 事实 | 出处 |
|---|---|
| 制作计时与 UI 无关：`ToolTableManager.Update(deltaTime, unscaledDeltaTime)` 遍历 `ToolTableMap`，`State==Making` 累加 `MakingTime`，到点调 `TryCreateItem` | dump.cs:443506；反汇编实证累加的是 **unscaledDeltaTime**（暂停也跑） |
| `ToolTableManager` 挂在 `BattleLogicWorld`（BaseSingleton, IUpdateAction）字段 0x210，随主循环每帧 tick | dump.cs:390331 |
| 关界面不取消制作：`Reducer_Web_ToolTable.RA_Close` 无 `ForceEndProduction` 调用 | RVA 0x2D30690 反汇编 |
| 材料在**完成时**才消耗：`TryCreateItem`（RVA 0x28CE380）对 `CachedItemIds` 逐个 `ForceRemoveItem`，成品 `TryAddItemToBag` 进台面包 | 反汇编 |
| `StartProduction(long furnitureId, List<long> itemInstanceIds)` public，只记 `State=Making + MakingDuration + CachedItemIds`，不动物品位置 | dump.cs:443557 |
| 制作状态入存档：`AgentSave.HandMadeState/HandMadeMakingTime/HandMadeMakingDuration/HandMadeCachedItemIds`（0x60-0x70） | dump.cs:76857 |
| 抽屉=全局包 5010(12×10)/5012(12×11)/5013(12×12)，owner=编译期常量 `WorkbenchDrawerConfig.DrawerOwnerId`；抽屉物品原生入档（存档根 `WorkbenchDrawerItems` 0x328） | Config_Bag.json；CabinetEverywhere DrawerTabs.cs 取证 |
| 官方取料链含抽屉：`GetAllLinkedOwnerIds` 读 `PlayerBagOwnerId(0x30)+DrawerOwnerId(0x88)+CabinetTabs(0x90)` | CabinetEverywhere DESIGN.md §2.1（E8 扫描实证） |
| 溢出落地原语：`CollectBatchProducts(bench, dest, out stored, out dropped)` 装不下的逐件 `DropItemManager.DropItemFromBag` 掉地面 | RVA 0x28CA350 反汇编 |
| `ForceEndProduction(furnitureId)`：TryGetValue + notify + 提示，不毁料（料还在台面/原容器） | RVA 0x28CAE80 反汇编 |
| `HandMadeState` 枚举：Idle=0, Making=1, WaitingReceive=2, NoReceive=3 | dump.cs:58422 |
| 精力查询：`ToolTableManager.GetCraftStamina(out current, out required)` public | dump.cs:443580 |
| **官方取料顺序：台面(0x70) → 当前页签源(leftOwnerId) → 背包(0x30) → 抽屉(0x88) → 柜子(0x90)**。抽屉排在柜子之前——背包缺料时官方先吃抽屉 | `GetLinkedOwnersInFillOrder` RVA 0x2D2CEE0 + `GetAllLinkedOwnerIds` 0x2D2C1F0 反汇编（2026-11-18 补证） |
| **`StartProduction` 在入口校验并扣精力**：取 LeadingRole `AttributeComponent.GetTotalValue_Float` + 制造熟练度减免 `GetProductionStaminaReduction` + `GlobalSetting_ProductionCost`，不足则 notify(`Text_HandMadeComponent_0`) 拒绝启动 | RVA 0x28CD770 反汇编（2026-11-18 补证）→ mod 直调即正常耗精力，无白嫖 |

## 4. 方案取舍（对话定稿）

| 决策点 | 选项与结论 |
|---|---|
| 预占容器 | **实体抽屉** vs 虚拟容器。选抽屉：物品原生入档（虚拟容器必须 mod 自己持久化物品+队列，违背不改存档红线）；`TryCreateItem` 按物品 id 消耗，物品在抽屉原地即可被用。 |
| 成品去向 | **双路径**（用户定）：官方按钮=前台制作走原生（弹窗收下进背包）；mod 按钮入队=成品自动进抽屉。放弃了「总是自动入抽屉」的统一语义——双路径对官方流程零改动，行为可预期。 |
| 前台/手动取走预占料 | **前台拦截（E 方案，用户定）**：hook `Ac_ToolTable_Make.SendAction`（public static，WebBind 稳定面）prefix——按官方取料顺序口径计算「刨除预占料后本次配方是否仍够」：够→放行无感；不够→拦截本次前台制作+提示「材料已被队列任务占用」。~~轻防御（事后标红）~~已被 2026-11-18 取证推翻：抽屉排在柜子前，背包缺料即撞车，属常态而非边缘 case。~~硬 hook 私有取料链~~行为等价但版本敏感高，弃。玩家**手动**从抽屉拿走预占料仍属轻防御范围：开工校验发现→任务 Blocked(缺料) 标红挂起。 |
| 取消退料 | **料留抽屉**（用户定）：取消=预占 id 出集合，材料原地留在抽屉变普通可用料。不搬背包（省一次搬运，避免背包满的边界）。 |
| 队列持久化 | **不入存档**（套件红线）：读档后队列清空，但预占材料都在抽屉里不丢；重新入队时官方取料链含抽屉，无缝重扣。制作中的那一件由官方 `AgentSave` 原生恢复。 |
| 家具包裹类成品 | **走官方原生**（用户定）：工作台做的家具包裹官方路径是直送玄关（`TryDeliverPackageToEntrance`），不落台面——队列任务照常 Done，面板标注「家具已送玄关」，不做额外搬运。 |

## 5. 设计（How）

### 5.1 数据模型（mod 侧，会话内存）

```
CraftTask {
  int TaskId;
  string RecipeKey;        // 配方 key（官方 Config_ProductionList 推导口径）
  int Remaining;           // 剩余件数（任务=配方×数量，逐件派发）
  List<long> ReservedItemIds;  // 当前这一件已预占的材料物品 id（在抽屉里）
  TaskStatus Status;       // Queued / Dispatched(制作中) / Blocked(标红挂起：缺料或精力) / Done
  string BlockedReason;
}
CraftQueueState {
  List<CraftTask> Tasks;   // FIFO
  bool StaminaPaused;      // 整队精力挂起标志（面板显示，不必每任务记）
}
HashSet<long> ReservedItemIds;  // 全局预占集合 = 所有任务 ReservedItemIds 并集
```

### 5.2 入队（UI 开着时操作）

1. 玩家在工作台选配方、填数量，点 mod 注入的「加入队列」按钮（官方「制造」按钮不动）。
2. C# 侧按官方同款取料顺序（背包→柜子→抽屉）从**未预占**物品中为第 1 件选料（模仿
   `SelectItemsForRecipeLinked` 的选择口径，`ReservedItemIds` 全局过滤）。
3. 选到的料搬进抽屉（`ItemManager.TryMoveBagAll` / 单件移动，实现时择优），id 记入任务与全局集合。
4. 数量>1 时：v1 只预占第 1 件的料，后续件在轮到时再取料（避免一次占满抽屉）；取不到料则该任务
   Blocked（缺料）。〔builder 注意：若实证「逐件取料」体验差，可升级为全量预占，数据结构已兼容〕
5. 抽屉放不下 → 入队失败，飘字提示。

### 5.3 派发（runner，串行）

挂在 mod 现有 MonoBehaviour 帧循环（AccessTicker 同款模式，每帧或 0.5s 节流）：

1. 队首任务 Status==Queued 且 `GetToolTableState(furnitureId)==Idle` 且精力够 → 派发：
   - 校验 `ReservedItemIds` 全部仍在抽屉（`ItemManager.GetItemData` 逐个验 OwnerId）；
     有丢失 → Status=Blocked(缺料)，继续看队次任务（Blocked 不出队，标红置顶显示）。
   - 调 `ToolTableManager.StartProduction(furnitureId, ReservedItemIds)`，Status=Dispatched。
2. 精力：`StartProduction` 入口校验并扣精力（§3 已实证）。派发后检查返回值/家具 State：
   未进入 Making → 视为精力不足，整队挂起标志置位，面板显示「等待精力」；轮询恢复后自动重试。
   `GetCraftStamina` 仅用于面板预显示。
3. 工作台选择：家地图内任一 `ToolTable` 家具，取 `GetToolTableState==Idle` 者；全部忙则等待。
   多张台子的家具 id 候选清单复用 CabinetEverywhere 的 `AgentManager.agentList` 扫描口径（若该 mod 在场）或自扫。

### 5.4 完成收尾（Harmony hook + runner）

1. Harmony postfix 到官方完成通知点（`TryCreateItem` 完成路径的 NotifyGlobal，实现时定位精确
   hook 点；备选：runner 轮询 `GetToolTableState==WaitingReceive`）。
2. 判定该次完成归属队列任务（furnitureId 与 Dispatched 任务匹配）→ 把台面成品搬抽屉
   （`ItemManager` 搬运 API），装不下逐件 `DropItemFromBag` 落地（`CollectBatchProducts` 同款语义）。
3. 官方状态复位：实现时取证 `WaitingReceive→Idle` 的官方复位路径（`GetProduct` notify 链），
   由 mod 主动走一遍等价复位；若复位必须经过 UI action，则构造 dispatch。〔builder 风险点①〕
4. 任务的 `Remaining--`：>0 → Status=Queued 回队尾重取料；==0 → Done 出队，`ReservedItemIds` 清出全局集合。
5. **家具包裹类成品**：官方 `TryCreateItem` 链内 `TryDeliverPackageToEntrance` 直送玄关、不落台面
   （§4 用户定）——队列照常 Done，面板该行标注「家具已送玄关」，搬运逻辑跳过。
6. UI 开着时官方会弹成品窗（notify 链照发）：队列侧成品已被搬走，mod 需自动 dispatch
   `Ac_ToolTable_ResultConfirm` 关窗 + 飘字「成品已入抽屉」。〔builder 风险点②：弹窗时序〕

### 5.5 取消（UI 面板按钮）

- Queued/Blocked：直接出队，`ReservedItemIds` 出全局集合，料留抽屉。
- Dispatched（制作中）：调 `ToolTableManager.ForceEndProduction(furnitureId)`（不毁料，
  料仍在抽屉原地）→ 出队、出集合、料留抽屉。

### 5.6 防御边界（前台拦截 + 手动轻防御，用户定）

- **前台制作拦截（E 方案）**：Harmony prefix 到 `Ac_ToolTable_Make.SendAction`（含 `CRAFT_AGAIN`
  路径 `Ac_ToolTable_CraftAgain.SendAction` 同口径）：解析 JsonData 配方 → 按官方取料顺序口径
  （台面→页签→背包→抽屉→柜子，§3 实证）计算「刨除全局 `ReservedItemIds` 后」本次需求是否满足：
  满足→放行（mod 无感）；不满足→拦截，`Ac_PopText_AddPopText.SendAction` 提示
  「材料已被队列任务占用」。
- **手动拿走（轻防御）**：玩家手动从抽屉拿走预占料 → 派发校验发现 → 任务 Blocked(缺料) 标红挂起。
- Blocked 任务不出队；玩家补料或取消后，下一次轮询自动重试取料，成功则恢复 Queued。

### 5.6.1 前台拦截的判定口径（builder 实现依据）

- 需求与可用量均按 configId×数量计（与 `CheckMaterialSufficiencyAcrossLinked` 同口径，但不直调
  私有方法，mod 自扫 linked owners 物品计数）。
- 可用量 = 台面 + 页签源 + 背包 + 柜子 + 抽屉中**未预占**物品。抽屉预占判定按 `ReservedItemIds`
  （物品 id 精确匹配，configId 相同也排除）。
- 数量 >1 的前台批量：按「全部件数」判定，不够整批即拦截（与官方整批语义一致）。

### 5.7 UI（webui 注入，railui 同款路子）

- 工作台页（ToolTable iframe）注入：
  - 数量行旁「加入队列」按钮（与官方「制造」并排，视觉区分）。
  - 队列面板（侧栏/浮层）：任务列表=配方名×剩余件数+状态（排队中/制作中/标红原因），
    每行一个「取消」按钮；整队挂起时顶部显示「等待精力恢复」。
- C#→webui 数据通道：队列快照 JSON 经 ReactiveProperty 注入或轮询拉取（实现时按
  CabinetEverywhere 的 State_Web_ToolTable 注入经验择优，倾向复用其脏标记推送模式）。
- 仅查看+取消，无编辑。

### 5.8 生命周期与还原

- 队列与预占集合均为会话内存（不入存档）。读档/退出：队列清空，抽屉里的料自然留存。
- 卸载 mod：dll 移除即还原；唯一残留是抽屉里的物品（本来也是普通物品）。
- 制作中的一件若在 mod 卸载后完成：官方原生收尾（成品留台面等收下），无破坏。

## 6. 约束

- 行数上限：后端模块 500 行/工具 300 行（AGENTS.md）；预计 C# 400-500 行 + 注入 js 一份。
- 套件红线：不改存档、不改游戏文件、不动官方前台制作行为。
- 版本敏感点集中在 §5.4 的 hook 与复位路径，全部有轮询兜底方案。

## 7. 留给 builder 的事

1. **风险点①**：`WaitingReceive→Idle` 官方复位路径取证（`GetProduct` RVA 0x28CB200 只见
   TryGetValue+NotifyGlobal，实际搬物品逻辑在 notify 订阅方——需定位）。
2. **风险点①b**：取消路径同构问题——`ForceEndProduction`（RVA 0x28CAE80）同样只见
   TryGetValue+notify+提示，**State 是否复位 Idle 未证**；若复位也在订阅方，取消 Dispatched 任务
   会卡 Making 死锁。兜底：直写 `ToolTableData.State` 或构造官方取消 action。
3. **风险点②**：UI 开着时队列完成 → 官方成品弹窗与自动搬抽屉的时序；自动 ResultConfirm 的构造。
4. `MakingDuration` 如何从配方推导（官方 `Ac_ToolTable_Make` 链取证）；`StartProduction` 返回值
   语义（精力不足拒绝时返回什么）。
5. 取料选择器实现：对齐 `SelectItemsForRecipeLinked` 口径（含部分材料/染料变体选择）；
   v1 可先只支持「整份材料唯一确定」的配方，变体配方（染料自选色）入队时按当前选中项锁定。
6. UI 注入通道选型与队列快照序列化。
7. `modcenter.json` / csproj 骨架对齐套件现有 mod（QuickAction 为模板）。
