# CraftQueue 工作台制作队列 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 工作台可入队制作任务（入队即锁料进抽屉），关界面后台串行执行，成品自动入抽屉（满则落地），前台制作被预占拦截。

**Architecture:** 三层：`QueueRunner`（MonoBehaviour 帧轮询，串行状态机）驱动官方 `ToolTableManager.StartProduction/TryCreateItem` 链；`CraftGate`（Harmony prefix）拦官方前台取料动预占料；`QueueUi`（webui 注入）提供入队按钮+队列面板。队列与预占集合均为会话内存（不入存档，套件红线）。

**Tech Stack:** BepInEx IL2CPP (net6.0) + Il2CppInterop + HarmonyX + HotUpdate interop；webui 注入复用 CabinetEverywhere 的 railui 路子。

**Spec:** `docs/superpowers/specs/2026-11-16-craft-queue-design.md`（v2 定稿，含全部 RVA 取证与 §5.7 布局定稿；交互参考 `design-proto/queue-panel.html`）

## Global Constraints

- 游戏/引用路径：`E:\SteamLibrary\steamapps\common\Survival Log\BepInEx\{core,interop}\`（csproj HintPath，照抄 QuickAction.csproj）
- csproj：`net6.0`、`AssemblyName=SurvivalLog.CraftQueue`、`Nullable=disable`、`DebugType=embedded`、modcenter.json 为 EmbeddedResource（mcSpec 2）
- 不改存档、不改游戏文件、不动官方前台制作行为；卸载 dll 即还原（spec §2 反目标）
- 行数上限：每个 C# 文件 ≤500 行（AGENTS.md）；单文件单职责
- 文档纪律：mod 目录 `DESIGN.md` 随实现同步（文档维护规则）；每个任务提交含对应 DESIGN.md 增量
- 无单测基建（il2cpp 运行时依赖）：每任务验证 = `dotnet build` + 游戏内定向点检（启动游戏 → 复现步骤 → grep `BepInEx/LogOutput.log` 中 `[CraftQueue]` 标记）
- 日志统一前缀 `[CraftQueue.<模块名>]`（对齐 CabinetEverywhere 的 `[CabinetEverywhere.Xxx]` 风格）
- commit 格式 `<type>：<描述>`；只在任务边界提交

## Review Focus

（spec 隐含、任务测试未直接覆盖的输入类；每条已归属到对应任务的点检步骤）

1. **读档后官方恢复的 Making 任务**：runner 重启不得把别家（官方/前台）的 Making/WaitingReceive 当队列任务收尾 → Task 4 Step 6 点检
2. **CRAFT_AGAIN 绕过 Make.SendAction**：再造一次走独立 action → Task 5 同 hook 两入口
3. **染料变体配方**（材料实例非唯一确定）：v1 拒绝入队并提示 → Task 3 Step 5
4. **工作台家具被拆/搬家**（furnitureId 失效）：runner 换空闲台，无台则挂起 → Task 4 Step 7
5. **抽屉彻底满**：入队失败提示（预占放不下）与成品落地（spec §5.2/§5.4）→ Task 3 Step 6 / Task 4 Step 5

---

### Task 1: 项目骨架（可加载的空 mod）

**Files:**
- Create: `SurvivalLog.CraftQueue/SurvivalLog.CraftQueue.csproj`
- Create: `SurvivalLog.CraftQueue/CraftQueuePlugin.cs`
- Create: `SurvivalLog.CraftQueue/modcenter.json`
- Create: `SurvivalLog.CraftQueue/DESIGN.md`
- Create: `SurvivalLog.CraftQueue/CHANGELOG.md`

**Interfaces:**
- Produces: `CraftQueuePlugin : BasePlugin`（`public const string Guid = "com.local.survivallog.craftqueue"`），`Load()` 内：cfg 绑定 + `ClassInjector` 注册 `QueueTicker : MonoBehaviour`（本任务内为空壳 Update，仅打点一条日志）；静态 `internal static ManualLogSource Log` 供各模块用
- Produces: cfg 键（后续任务消费）：`[General] Enabled`（默认 true，false=不注册 Ticker 且零日志）、`[Dev] VerboseLog`（默认 false）

- [ ] **Step 1: 建 csproj**——照抄 QuickAction.csproj（同引用集），改 AssemblyName/RootNamespace/Version=0.1.0；删 `ModShared` Compile Include（本 mod 不用 WelcomeTip）
- [ ] **Step 2: 建 modcenter.json**——mcSpec 2，id `com.local.survivallog.craftqueue`，display `制作队列`，summary 一句话（工作台任务排队后台制作，成品自动进抽屉），groups：`General.Enabled`、`Dev.VerboseLog` 两键（格式对齐 QuickAction 的 groups 段）
- [ ] **Step 3: 建 CraftQueuePlugin.cs**——`[BepInPlugin]`+`Load()`：绑 cfg → Log 赋值 → Enabled 时注入 Ticker；头部注释块写设计一句话+指向 spec 路径（对齐套件注释文化）
- [ ] **Step 4: 建 DESIGN.md**（三行：功能定位一句话、机制链四行、指向 spec）与空 CHANGELOG.md
- [ ] **Step 5: `dotnet build`**——Expected: 0 error 0 warning（CS 报错即改）
- [ ] **Step 6: 实机点检**——dll 复制到游戏 `BepInEx/plugins/`，启动游戏进存档，`LogOutput.log` 出现 `[CraftQueue] ticker ready` 一条；Enabled=false 重启则无任何 `[CraftQueue]` 日志
- [ ] **Step 7: Commit** `feat：CraftQueue 项目骨架（BepInEx 插件壳+ModCenter 清单）`

### Task 2: 队列数据模型与快照序列化

**Files:**
- Create: `SurvivalLog.CraftQueue/QueueState.cs`
- Modify: `DESIGN.md`（数据模型节）

**Interfaces:**
- Produces: `enum TaskStatus { Queued, Dispatched, Blocked, Done }`
- Produces: `sealed class CraftTask { int TaskId; string RecipeKey; string ProductName; int ProductIconId; int Left; int Total; List<long> ReservedItemIds; TaskStatus Status; string BlockedReason; bool IsFurniture; }`
- Produces: `static class QueueState { List<CraftTask> Tasks; HashSet<long> ReservedItemIds; bool StaminaPaused; int Add(CraftTask t); CraftTask Find(int id); bool Remove(int id); void SyncReservedIds(); string SnapshotJson(); }`（`SnapshotJson` 产出 QueueUi 直接消费的 JSON：任务数组+精力+抽屉占用+挂起标志）
- 序列化用 `Newtonsoft.Json`（HotUpdate 侧已带，游戏内稳定；不用 DataContract）

- [ ] **Step 1: 实现模型+集合操作**——`SyncReservedIds()` = 全任务 `ReservedItemIds` 并集重建（Remove 后必调）；`Add` 自增 TaskId
- [ ] **Step 2: 实现 `SnapshotJson()`**——字段名与原型 js 对齐：`[{id,name,icon,left,total,status,reason,furniture}]` + `{stamina,staminaMax,paused,drawerUsed,drawerCap}`
- [ ] **Step 3: `dotnet build`** 通过
- [ ] **Step 4: 实机点检**——Ticker.Update 里临时构造 2 条假任务调 `SnapshotJson()` 打日志（VerboseLog 门控），log 里 JSON 结构完整；点检后删临时代码
- [ ] **Step 5: Commit** `feat：队列数据模型与快照序列化`

### Task 3: 取料选择器与预占搬运

**Files:**
- Create: `SurvivalLog.CraftQueue/MaterialPicker.cs`
- Modify: `DESIGN.md`

**Interfaces:**
- Consumes: `QueueState.ReservedItemIds`（Task 2）
- Produces: `static class MaterialPicker { List<long> PickAndReserve(State_Web_ToolTable state, State_Data_Item itemState, string recipeKey); bool DrawerHasSpaceFor(List<Data_Item> items); }`
  - 返回按官方口径选好、已搬进抽屉、已加入 ReservedItemIds 的物品 id 列表；取不到（不足/变体/抽屉满）返回空 List 并 out 三个失败原因枚举（改签名 `PickAndReserve(..., out PickFail reason)`，`enum PickFail { None, Shortage, DrawerFull, Variant }`）
- 关键口径（spec §5.2/§5.6.1，全部 spec 已实证）：
  - owner 顺序=官方 `GetAllLinkedOwnerIds` 同构：台面(0x70)→当前页签源→背包(0x30)→抽屉(0x88)→柜子(0x90)；抽屉候选先剔除 ReservedItemIds（按物品 id，非 configId）
  - 材料 configId 需求从 `Config_ProductionList` 推导（`ConfigManager.GetProductionByMaterials` 同源表；按 recipeKey 查 `Config_ProductionList`）
  - 搬运用 `ItemManager` 移动 API（优先 `TryMoveBagAll` 族；interop 签名以生成 dll 为准，实现时核对）
- [ ] **Step 1: 实现 owner 收集+可用池过滤**——interop 直访 `ReduxUISystem.GetStateByUI(typeof(State_Web_ToolTable))`，null（界面未开）时取料仅允许背包+抽屉+柜子链（台面/页签源随 UI；入队时 UI 必开，此分支是防御）
- [ ] **Step 2: 实现 per-configId 数量选择+搬运进抽屉+登记 ReservedItemIds**
- [ ] **Step 3: 变体拒绝**——材料需求里存在多实例不同品质/染料属性可选项时（同一 configId 的实例集合不唯一确定）返回 `Variant`（v1 口径，spec §7.5；判定：配方材料含染料类 configId 即拒，名单实现时从 `DyeConfig` 查）
- [ ] **Step 4: `dotnet build` + 实机点检**——开工作台、背包备异量材料，日志打印选中清单（VerboseLog）：足量/不足/被预占排除三种输入各验一次
- [ ] **Step 5: 变体点检**——选含染料配方入队 → 拒绝+提示（Review Focus 3）
- [ ] **Step 6: 抽屉满点检**——塞满抽屉后入队 → `DrawerFull` 拒绝（Review Focus 5 前半）
- [ ] **Step 7: Commit** `feat：取料选择器（官方口径+预占排除+变体拒绝）`

### Task 4: 串行 Runner（派发/收尾/挂起/取消）

**Files:**
- Create: `SurvivalLog.CraftQueue/QueueRunner.cs`
- Modify: `CraftQueuePlugin.cs`（Ticker.Update 挂 `QueueRunner.FastPoll()`）、`DESIGN.md`

**Interfaces:**
- Consumes: `QueueState`、`MaterialPicker.PickAndReserve`（Task 2/3）
- Consumes: 官方 `ToolTableManager`（interop：`StartProduction(long, List<long>)`、`GetToolTableState(long)`、`GetCraftStamina(out float, out float)`、`ForceEndProduction(long)`）、`ItemManager`（成品搬运）、`DropItemManager.DropItemFromBag`（落地）
- Produces: `static class QueueRunner { void FastPoll(); bool Enqueue(string recipeKey, int qty, out string failMsg); bool Cancel(int taskId, out string failMsg); }`（Enqueue 供 QueueUi 调：内部调 MaterialPicker 取第 1 件料+建任务）
- 状态机（spec §5.3-5.5）：每 0.5s 节流一轮——①队首 Queued 且任一工作台 Idle 且精力够 → 校验预占物品 `OwnerId==抽屉`（丢失→Blocked(缺料)）→ `StartProduction(furnId, ids)` → Dispatched；②轮询所有 Dispatched：`GetToolTableState==WaitingReceive` → 收尾；③`StartProduction` 未进 Making → 整队精力挂起（`StaminaPaused=true`），精力恢复自动清除
- 收尾（②内）：成品搬抽屉（装不下逐件 `DropItemFromBag` 落地）→ 家具包裹跳过搬运（官方已送玄关）→ State 复位 → `Left--`：>0 重取料回 Queued，==0 出队+清预占

- [ ] **Step 1: 取证步骤（写死进代码注释）**——`GetProduct`/`ForceEndProduction` 的 State 复位路径：直调后轮询 `GetToolTableState` 是否回 Idle；不复位则兜底=直写 `ToolTableData.State`（spec 风险点①①b；取证结论写进本步注释与 DESIGN.md）
- [ ] **Step 2: 实现 Enqueue/Cancel**（Cancel 分 Queued/Blocked=出队清预占料留抽屉、Dispatched=`ForceEndProduction`+出队，spec §5.5）
- [ ] **Step 3: 实现派发与精力挂起**——工作台候选=家地图 `ToolTable` 家具扫描（`AgentManager.agentList` 直扫，configId 匹配 ToolTable 家具；缓存 5s）；全部忙/无台→等待（Review Focus 4：台子消失自然落此分支，日志一次）
- [ ] **Step 4: 实现收尾搬运+落地+下一件取料**
- [ ] **Step 5: `dotnet build` + 实机全链路点检**——入队木板×3（真实配方名以游戏为准）：①关界面等待，逐件完成，抽屉出现成品；②塞满抽屉再入队 → 完成时地面出现掉落物（Review Focus 5 后半）；③家具配方（柜子）→ 完成后玄关出现包裹、抽屉无物
- [ ] **Step 6: 读档点检**（Review Focus 1）——制作中存档→读档：官方恢复的 Making 不被 runner 收尾（日志无重复收尾记录），队列清空（预期），抽屉预占料仍在
- [ ] **Step 7: 精力点检**——精力耗尽入队 → 面板/日志挂起，吃食物恢复 → 自动续跑（本轮日志验证，UI 下任务呈现）
- [ ] **Step 8: Commit** `feat：串行队列 runner（派发/收尾/落地/挂起/取消）`

### Task 5: 前台拦截（E 方案）

**Files:**
- Create: `SurvivalLog.CraftQueue/CraftGate.cs`
- Modify: `CraftQueuePlugin.cs`（Load 里 `Harmony.CreateAndPatchAll(typeof(CraftGate))`）、`DESIGN.md`

**Interfaces:**
- Consumes: `QueueState.ReservedItemIds`（Task 2）、owner 链可用量计算（复用 MaterialPicker 的池扫描，抽 `internal static int CountAvailExcludingReserved(configId)` 到 MaterialPicker）
- Produces: `[HarmonyPrefix] static bool Prefix_Make(ref string jsonData)`（`Ac_ToolTable_Make.SendAction`）与同构 `Prefix_CraftAgain`（`Ac_ToolTable_CraftAgain.SendAction`）；返回 false=拦截
- 判定（spec §5.6.1）：解析 jsonData 配方（与官方 `ParseBatchQty` 同构：Newtonsoft 反序列化 {recipeKey,qty}——字段名实现时以 RA_Make 反汇编/页面 JS `CRAFT` 事件 payload 为准核对）→ 逐材料 `CountAvailExcludingReserved ≥ 需求×qty`，任一不足 → 拦截 + `Ac_PopText_AddPopText.SendAction("材料已被队列任务占用")`
- [ ] **Step 1: 实现两个 Prefix 与判定**——HarmonyX 引用补进 csproj（对齐 CabinetEverywhere 的写法）
- [ ] **Step 2: `dotnet build` + 实机点检**——①预占同配方料→背包清空该料→点官方制造 → 拦截提示、官方流程未启动（台面无料移动）；②背包余量足够 → 零干预（官方照常做）；③「再造一次」路径同验（Review Focus 2）
- [ ] **Step 3: Commit** `feat：前台制作预占拦截（Make/CraftAgain 双入口）`

### Task 6: WebUI 注入（按钮+胶囊+队列面板）

**Files:**
- Create: `SurvivalLog.CraftQueue/QueueUi.cs`
- Create: `SurvivalLog.CraftQueue/queueui.page.js`（EmbeddedResource 或注入时读文本——对齐 railui.page.js 的装载方式，实现时看 CabinetEverywhere 装载器择一）
- Modify: `CraftQueuePlugin.cs`（FastPoll 链挂 `QueueUi.FastPoll()`）、`DESIGN.md`

**Interfaces:**
- Consumes: `QueueRunner.Enqueue/Cancel`、`QueueState.SnapshotJson()`（Task 2/4）
- Produces: webui 侧（queueui.page.js）：`window.__craftQueue` 消息协议 `{type:'snapshot', json}`（C#→页面推送）与 `{type:'enqueue', recipeKey, qty}` / `{type:'cancel', taskId}`（页面→C#，C# 端 QueueUi 消费后调 QueueRunner 并回推快照）
- UI 结构=原型定稿（spec §5.7）：标题行「队列 [N]」胶囊（开/关面板）、按钮行 3 键（清空|制造|加入队列，btn-slim）、右侧浮层面板（精力条「制作消耗/还能做 N 件」+挂起条+任务行+取消+抽屉占用条）、签名 diff 渲染纪律（原型教训写进 js 注释）
- 注入路子：ToolTable iframe 加载完成后注入 js/css（对齐 CabinetEverywhere RailUi 的 iframe 钩子与 `WebUIVm.MarkAllMsgDirty()+OnMsgChange()` 推送制，spec 附录 B 口径）

- [ ] **Step 1: QueueUi.cs——iframe 发现+js 注入+消息桥**（enqueue/cancel 事件从页面回调进 C#，复用官方 `WebUI_ToolTable_Event` 绑定面或 `UnitySendEvent` 通道，实现时按 RailUi 实际通道抄）
- [ ] **Step 2: queueui.page.js——DOM 注入与面板**（视觉 token 照抄原型；i18n 不做，中文直写，对齐官方中文界面）
- [ ] **Step 3: 快照推送**——状态变化时（签名对比）推 `SnapshotJson`，空闲不推（对齐脏标记推送纪律）
- [ ] **Step 4: `dotnet build` + 实机全交互点检**——①入队→面板任务行出现、精力/抽屉条实时；②制作中进度与百分比更新无闪烁；③取消两路径；④关闭工作台界面→队列照跑（抽屉仍进成品）；⑤队列完成弹窗场景：UI 开着时队列任务完成 → 官方成品弹窗被自动确认+「已入抽屉」提示（spec 风险点②的实机验证；时序不对则改推 `Ac_ToolTable_ResultConfirm` 时机）
- [ ] **Step 5: Commit** `feat：WebUI 队列面板（入队/取消/进度/精力/抽屉占用）`

### Task 7: 收尾与发版整理

**Files:**
- Modify: `DESIGN.md`（终稿全链+实证结论回填）、`CHANGELOG.md`、`modcenter.json`（usage 补怎么用三 bullets）、`README.md`

- [ ] **Step 1: 文档回填**——DESIGN.md 按 mod 惯例写全（机制/取证/边界/卸载行为）；CHANGELOG 首版本条目
- [ ] **Step 2: 全量实机回归**——spec §2 目标逐条过一遍（入队/关界面/抽屉/落地/精力/取消/前台双路径/读档/删 dll 卸载还原：官方制作链零残留），一条一勾
- [ ] **Step 3: Commit** `docs：DESIGN/CHANGELOG/ModCenter 发版整理`（+按需 bump Version）
