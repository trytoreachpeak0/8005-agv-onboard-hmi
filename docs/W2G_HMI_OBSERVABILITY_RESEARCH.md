# 调研：从控制端了解车载端 `SQCD.Agv.Wpf` 的状态，比截屏更好的办法

票：trytoreachpeak0/8005-agv-onboard-hmi#308（调研票，不改产品代码）
本仓被观测版本：`w2g/fp-v2-impl@270c20d`
服务端被观测版本：`8005-agv-control-server` `fp/v2-impl@a25ef54e`（只读，`git show`／`git grep`）
截屏工具：`8005-workspace` `remote-ops/onboard-hmi/scripts/17-capture-hmi-screen.ps1`（8005-workspace#16，PR 8005-workspace#17），证据 `remote-ops/onboard-hmi/evidence/ws16-20261011/`
日期：2026-10-11

## 结论先说

1. **推荐方案：车载端把「操作员此刻看到的状态」从 ViewModel 读出来，写成本机的只读状态文件**（当前一份
   JSON，外加一条只记变化的时间线 NDJSON），控制端经现有 SSH 通道读文件。这是票面方案 A 的「文件」形态，
   并吸收方案 B 的时间线。不开新端口，不碰窗口，不改任何业务判据。
2. **不推荐在生产车上打开车载端已有的自动化 HTTP 接口**（`SQCD.Agv.AutomationHost`）。它确实只绑回环、带
   `/api/v1/snapshot`，但同一个接口还带一个**写**端点（录入 sublot），配置校验明令生产环境禁止开启；而且它的
   快照取的是业务层判据，不是屏上的最终值（锁存等界面层闸门不在里面），回答不了「屏上能按哪个按钮」。
3. **截屏＋UIA 保留**，角色从「主要手段」降为「兜底与对照」：状态文件可信与否，正是靠它定期对照来证明。
4. **服务端汇总（方案 C）回答不了这个问题**：服务端没有任何「屏上显示什么／能按什么」的字段，致命故障锁存与
   `recoveryResumeEnabled` 在线路上看不到；补上它要改协议、发版、作废证据，代价远大于收益。
5. **Snoop 一类开发期工具（方案 D）不适合现场车**：要在车上装工具、往运行中的进程注入或挂调试器，
   且只能在交互桌面上用。

需要用户拍板的一件事见第 7 节：状态文件在 `Production` 环境是否默认开启。

## 1. 问题是什么

现场真车票（如 control-server#566）需要从控制端知道车载端此刻：屏上显示什么、操作员能按哪些按钮、旅程／
仓位／恢复入口处在哪一步、**为什么**显示「需要恢复」或「故障」。

截屏工具在 2026-10-11 实车用过两次（`ws16-20261011/SUMMARY.md`）。第一次的记录正好说明了它的边界：
屏上横幅是「需要恢复　上层会话要求恢复：DEPARTURE_UNSAFE」，截屏如实给出了这句话，但「原因由现场会话查，
不在本证据里」。也就是说，截屏能回答「画了什么」，回答不了「为什么这么画」，而后者才是排障要的。

## 2. 现状盘点：车载端已有的可观测出口

下表每一行都是实读代码的结果，「读到的」指代码原文如此，「推的」指由代码推断、未实跑或未上车核对。

| # | 出口 | 在哪里 | 能回答 | 不能回答 |
| --- | --- | --- | --- | --- |
| O1 | 文件日志 `agv-yyyyMMdd.log` | `FileAppLogger` | 时间线上的自由文本事件；会话就绪变化及原因码 | 屏上此刻什么样；按钮能不能按；结构化查询 |
| O2 | 日志簿（SQLite） | `SqliteWireToGateJournal` | 恢复状态、未结清尝试、发件箱每一行是否被确认 | 界面层状态；会话就绪；锁存 |
| O3 | 操作员事件（`OperatorEventPublished`） | `WireToGateBusinessService` → `MainViewModel.Logs` | 屏上「操作记录」列表里那几句话 | 进程外不可见；不落盘 |
| O4 | 嵌入式自动化 HTTP 接口 | `SQCD.Agv.AutomationHost` | 业务层快照：会话、旅程、恢复状态、可否录入 | 界面层最终值；生产环境禁止开启 |
| O5 | UIA 树（截屏工具读的） | `MainWindow.xaml` 的 `AutomationId`／`ItemStatus` | 屏上文字；部分格子的原始码 | 为什么；不可见的状态 |
| O6 | 上报服务端的消息 | 协议消息，服务端落库 | 会话就绪、安全原因码、告警板、操作结果、恢复报告 | 屏上显示与按钮；锁存与配置开关（无专门字段） |
| O7 | ViewModel 状态 | `MainViewModel`（partial） | 屏上每个绑定的最终值，包括按钮的可见与可按 | 目前没有任何出口把它带出进程 |

下面逐项写依据。

### O1 文件日志

- 读到的：`FileAppLogger.Write` 每条一行，格式 `时间戳(ISO 8601)\t等级\t来源类名\t消息`，追加到
  `<程序目录>/<logging.directory>/agv-yyyyMMdd.log`（`src/SQCD.Agv.Infrastructure/FileAppLogger.cs:16`、`:26`、`:33`）；
  默认目录 `logs`（`src/SQCD.Agv.Wpf/appsettings.json:126-128`）。写失败只 `Debug.WriteLine`，不影响业务
  （`FileAppLogger.cs:37-44`）。
- 读到的：`FileAppLogger` 里没有任何删除或轮转代码（`FileAppLogger.cs` 全文 53 行）。
- 读到的：会话状态每次发布都会写一行，含 `readiness` 与 `reasonCodes`（`src/SQCD.Agv.Infrastructure/WireToGateSessionClient.cs:4863-4867`，
  经 `WireToGateSessionService.OnClientDiagnosticRecorded` 落盘，`src/SQCD.Agv.Infrastructure/WireToGateSessionService.cs:456-457`）。
  这一组诊断行是 control-server#380 为「恢复入口不出现时，查就绪到没到」加的（`WireToGateSessionClient.cs:168-173`）。
- 读到的：操作员提示（控制器快照里的 `ErrorCode`／`Guidance`）去重后写日志（`src/SQCD.Agv.Wpf/ViewModels/MainViewModel.cs:2554-2573`）。
- 读到的：全仓 `src/` 有 191 处 `logger.Write`，最多的是 `WireToGateBusinessService.cs`（63 处）。
- 读到的：`OnboardTechnicalLog`（REQ-0272 的四类技术日志、30 天保留）只有类定义，`src/` 里没有任何地方构造或使用它
  （`src/SQCD.Agv.Core/OnboardTechnicalLog.cs:63`；`git grep` 只命中定义本身）。
- 推的：日志是中文自由文本，按行能读，但「屏上此刻是什么样」要靠人把多条日志在脑子里叠起来；日志里没有按钮
  可见／可按的记录。

### O2 日志簿（SQLite）

- 读到的：四张表 `WireToGateJournalMetadata`、`WireToGateRecoveryState`（单行 JSON）、`WireToGateDurableOutbox`、
  `WireToGateAppliedJourneySnapshots`；`journal_mode = WAL`（`src/SQCD.Agv.Infrastructure/SqliteWireToGateJournal.cs:48-81`）。
  默认路径 `%LOCALAPPDATA%\SQCD\8005AGV\onboard-journal.db`（`appsettings.json` 的 `wireToGate.journalPath`）。
- 读到的：恢复状态含未结清尝试号、恢复检查点、开锁中的仓、待结清结果、异常处置会话号与动作号等
  （`src/SQCD.Agv.Core/WireToGateRecovery.cs:250-300`）。
- 读到的：进程内对日志簿只有一把信号量，界面读它要合并请求，避免与执行器写检查点抢锁
  （`src/SQCD.Agv.Wpf/JournaledOperationsFeed.cs:18-24`）。
- 推的：从车外用 `sqlite3` 只读打开是可行的（WAL 允许并发读），但有两条代价：长读会推迟检查点；从安装目录加载
  SQLite 原生库会锁住文件、妨碍下次部署（工作区记忆 `v2-helper-scripts-dont-load-from-install-dir` 记过这一坑）。
  而且它回答的是「持久化下来的恢复状态」，不是屏。

### O3 操作员事件

- 读到的：业务服务 `RaiseOperatorEvent` 只触发事件，不写文件日志（`src/SQCD.Agv.Wpf/WireToGateBusinessService.cs:3981-3992`）。
- 读到的：App 把它接到 `MainViewModel.ApplyWireToGateOperatorEvent`，后者只往界面集合 `Logs` 里加一行
  （`src/SQCD.Agv.Wpf/App.xaml.cs:243-247`，`MainViewModel.cs:617-641`），集合上限 300 条（`MainViewModel.cs:13`、`:2526-2532`）。
- 读到的：可见性的测试在 `tests/SQCD.Agv.WireToGateG2Tests/OperatorEventVisibilityTests.cs:21`，它钉的是
  「该出的提示一定出」，不是「提示被记录到进程外」。
- 推的：屏上「操作记录」那一栏的内容在进程外只能靠截屏读到；进程一退出就没了。

### O4 嵌入式自动化 HTTP 接口

- 读到的：`OnboardAutomationHttpServer` 只许绑回环地址（`src/SQCD.Agv.AutomationHost/AutomationHttpServer.cs:17-25`），
  路由有 `GET /api/v1/health`、`GET /api/v1/snapshot`、**`POST /api/v1/sublots/submit`**、`GET /openapi/v1.json`
  （`src/SQCD.Agv.AutomationHost/AutomationApi.cs:48-167`，按本文件内行号）。
- 读到的：快照字段是 `AgvId`、控制器快照、会话快照、旅程快照、恢复状态、`CanSubmitSublot`、期望 sublot、当前尝试号与阶段
  （`src/SQCD.Agv.Core/OnboardAutomation.cs:7-17`），由 `WpfOnboardAutomationFacade.ReadSnapshot` 组装
  （`src/SQCD.Agv.Wpf/WpfOnboardAutomationFacade.cs:35-64`）。
- 读到的：快照里唯一的「能不能按」是 `CanSubmitSublot`，取业务服务的值（`WpfOnboardAutomationFacade.cs:59`）；它自己已含锁存
  （`src/SQCD.Agv.Wpf/WireToGateBusinessService.cs:308-309`）。屏上另外十个恢复入口根本不在快照里，它们的最终值要再过界面层的
  锁存闸门 `AllowRecoveryEntry`（`MainViewModel.cs:664-665`、`:691-692`），这一层只存在于 ViewModel。
- 读到的：配置默认关（`appsettings.json:39-43`），**`Production` 环境开启会被校验拒绝**：「Production环境禁止启用车载端
  自动化loopback接口，除非完成独立安全评审。」（`src/SQCD.Agv.Infrastructure/Configuration.cs:466-471`）
- 读到的：快照读日志簿是同步阻塞的（`.GetAwaiter().GetResult()`，`WpfOnboardAutomationFacade.cs:98-101`），与 O2 那把信号量同一把。
- 读到的：本仓里用到这个接口的只有单元测试 `tests/SQCD.Agv.UnitTests/OnboardAutomationHostTests.cs`；
  `scripts/run-first-integration.ps1` 里的 `/api/v1/snapshot` 是模拟器的接口（端口 58006），不是它。
- 推的：它是为「测试驱动扫码」设计的，不是为「现场观测」设计的。

### O5 UIA 树

- 读到的：`MainWindow.xaml` 有 42 处 `AutomationProperties.AutomationId`、22 个 `Button`；若干格子用 `ItemStatus` 带原始码，
  例如电量、充电周期、到站、发车倒计时档位（`src/SQCD.Agv.Wpf/MainWindow.xaml:50`、`:56`、`:106`、`:213`）。
- 读到的：按钮的可见与可按常常绑在两个不同的属性上，例如取消装货 `IsEnabled` 绑 `CanPressLoadCancellation`、`Visibility`
  绑 `CanRequestLoadCancellation`（`MainWindow.xaml:381-382`）。
- 读到的：截屏脚本用 `PrintWindow` 取窗口图、UIA 只读遍历，自带禁用调用扫描（`SetForegroundWindow`、`InvokePattern` 等）
  （`17-capture-hmi-screen.ps1:176-179`），每次在车上注册一次性计划任务进交互会话、跑完注销（`:149`、`:155`）。实车一次约 25～30 秒
  （`ws16-20261011/SUMMARY.md`：12:37:56 开始、12:38:26 写完）。
- 推的：ItemStatus 这条路已经在往「读原始码而不是读文案」走，但只覆盖了少数格子，按钮的可按状态 UIA 能读到 `IsEnabled`，
  读不到「为什么不可按」。

### O6 上报服务端的消息（服务端能看到什么）

以下是对服务端 `fp/v2-impl@a25ef54e` 的只读查询结果，由子代理检索、本会话抽查了其中 5 处原文（标 ✓）。

- 读到的 ✓：除握手外，每条入站消息整条原样写进收件箱表 `ProtocolInbox`（`src/ControlServer.Host/Transport/OnboardMessageProcessor.cs:154-171`）。
- 读到的 ✓：`OperationProgress` 服务端只回 `DurableAck`，载荷不解析（`OnboardMessageProcessor.cs:548-549`），服务端没有「当前仓位操作阶段」字段。
- 读到的：安全摘要（`departureSafe`、`reasonCodes`）落在 `SessionRecoveries` 行（`OnboardMessageProcessor.cs:462-503`）；告警板整份
  落 `OnboardAlarmSnapshots`（`OnboardMessageProcessor.cs:443-461`）。
- 读到的 ✓：车载端锁存时在安全摘要里报 `ONBOARD_FATAL_FAULT_LATCHED`（本仓 `src/SQCD.Agv.Infrastructure/WireToGateSafetyEvaluator.cs:33`），
  服务端只把它列为「认识的码」，注释写明尚无专门处理（`src/ControlServer.Domain/ProtocolErrorCodes.cs:24-30`）。
- 读到的 ✓：车载端有没有清桩入口，协议里没有字段能读到，只能靠服务端配置 `FieldOperatorRoles:OnboardClearanceEntryDeclared` 声明
  （`src/ControlServer.Host/Runtime/Charging/FieldOperatorRoleRoster.cs:33`，`StationClearanceExit.cs:20`）。
- 读到的：对外接口有 `GET /api/runtime/sessions`（`src/ControlServer.Host/Program.cs:275`）、看板查询 `fleet-sessions`、`onboard-alarms`、
  `expected-action-overdue`、`blocked-journeys`；在线判定是查询时现算「6 秒内当前会话代有没有入站」
  （✓ `src/ControlServer.Infrastructure/Persistence/SessionLiveness.cs:51`），没有 `lastSeen` 列。
- 读到的：服务端全仓搜不到 `currentScreen`、`availableActions` 一类字段。

### O7 ViewModel 状态

- 读到的：票面提到的 `MultiDemandViewModel`、`FatalFaultLatchViewModel` 在 `src/` 里不存在；那是两个**测试类**的名字
  （`tests/SQCD.Agv.WireToGateG2Tests/MultiDemandViewModelTests.cs`、`FatalFaultLatchViewModelTests`）。产品里只有一个
  `MainViewModel`（`MainViewModel.cs:11`，partial，主文件 2608 行），外加 `LockerCardViewModel`、`SlotGroupViewModel` 等子项。
- 读到的：屏上每个按钮的可见／可按都是 `MainViewModel` 的属性，例如 `CanRequestWireToGateRecovery`、`CanPressLoadCancellation`
  （`MainViewModel.cs:1282-1330`）；它们的最终值 = 业务服务给的判据 × 界面层锁存闸门（`MainViewModel.cs:744-778`、`:2395-2440`）。
- 读到的：横幅（「需要恢复」＋说明）由纯函数 `WireToGateHmiPresentation.Create(会话快照, 操作快照, canSubmit, …)` 算出
  （`src/SQCD.Agv.Wpf/ViewModels/WireToGateHmiPresentation.cs:13-60`），输入里就有会话的 `ReasonCodes`。**「为什么显示需要恢复」
  的答案就是这个函数的输入。**
- 读到的：ViewModel 的写入都经 `RunOnUiThread` 调度到 WPF 调度线程（`MainViewModel.cs:2591-2606`）。从别的线程读它没有同步保证。
- 读到的：G2 夹具直接构造真正的 `MainViewModel`（`tests/SQCD.Agv.WireToGateG2Tests/MultiDemandJourneyG2Tests.cs:408`），
  并有一个容忍并发追加的读日志方法（`tests/SQCD.Agv.UnitTests/ViewModelLogsReadArchitectureTests.cs:5-21`）。
- 推的：**ViewModel 是「屏上显示什么、能按什么」唯一一份结构化的真相来源**，界面只是它的绑定。它现在没有任何出口。

### 顺带的事实：焦点

- 读到的：扫码枪按键盘输入工作，`CanSubmit` 变真时界面把焦点放回 `ScanTextBox`（`src/SQCD.Agv.Wpf/MainWindow.xaml.cs:42-56`）。
- 推的：任何抢走前台焦点的观测手段都会让扫码枪打到别的窗口里，这是「不能抢焦点」最直接的业务后果。

## 3. 方案比较

评价维度：能回答什么、改动面、对只读与安全的影响、性能、成本、怎么测。

### A. 车载端提供只读的本机状态出口

内容统一叫「屏幕状态快照」：从 `MainViewModel` 在调度线程上读出的一份结构化记录，至少含
页面区块与横幅（状态文字、说明、横幅输入里的原因码）、每个按钮的 `visible`／`enabled`（与 XAML 绑定的属性一一对应）、
旅程计划行与到站格的原始码、仓位卡（锁、有货、目标）、恢复入口状态、锁存、会话就绪与原因码、最近 N 条操作记录，
外加 `sequence`、`writtenAt`、进程启动号与版本。

A 有两种出口形态，差别很大：

| | A1 写本机文件 | A2 回环 HTTP 端点 |
| --- | --- | --- |
| 读法 | `ssh agv0N` 后读文件（`Get-Content`），不在车上运行任何新程序 | `ssh agv0N` 后对 `127.0.0.1:<端口>` 发请求 |
| 新开端口 | 无 | 有，一个只绑回环的监听端口 |
| 可复用的现成代码 | 无（写文件是新代码，约百行） | 有 `SQCD.Agv.AutomationHost`，但要拆出不带写端点的只读主机 |
| 进程挂掉／界面卡死时 | 文件停在最后一刻，`writtenAt` 变旧，本身就是信号 | 端点不应答 |
| 生产环境配置 | 新开关，无既有禁令 | 撞上 `Configuration.cs:466-471` 的禁令，要做「独立安全评审」 |
| 历史 | 可以顺带写变化时间线 | 只有此刻 |

- 能回答：屏上此刻显示什么、哪些按钮可见可按、为什么（横幅输入与原因码）、从哪一刻起变成这样（时间线）。
- 改动面：`src/SQCD.Agv.Wpf`：新增快照构造器（只读 `MainViewModel` 公开属性）与写出器；`App.xaml.cs` 接线；
  `Configuration.cs` 加开关。不改任何判据、不改协议、不改服务端。
- 只读与安全：快照构造器只调属性 getter；写出器异常一律吞掉记日志，与 `JournaledOperationsFeed` 同一做法
  （`JournaledOperationsFeed.cs:70-78`）；不调任何窗口 API，不碰焦点。A2 多出一个监听面，哪怕只读，也是
  「车上多开一个端口」，要写清代价。
- 性能：推的。只在 ViewModel 属性变化时（防抖约 500 ms）在调度线程上**以低优先级**取一次值，
  序列化与写盘放到后台线程；内容几 KB。另有约 5 秒一次的心跳写，证明调度线程还活着。对比现状：界面本身每次刷新都要
  重算这些属性，读一遍的开销在同一量级以下。
- 成本：A1 两张票（构造器、写出器）加控制端一个读取脚本；A2 再加拆分只读主机与安全评审。
- 怎么测：G2 夹具构造真 `MainViewModel`（`MultiDemandJourneyG2Tests.cs:408`），可以在既有场景里断言快照内容；
  XAML 覆盖护栏（见第 5 节）用读 XAML 文本的单元测试实现，本仓已有同类写法（`RecoveryDemandChoiceXamlTests`）。
  「不抢焦点」用源码禁用调用扫描守，与截屏脚本同一思路。

### B. 把操作员可见状态作为结构化事件写进日志

- 能回答：事件时间线，可回放「从什么时候开始显示需要恢复」。
- 改动面：分散。要在业务服务、会话客户端、ViewModel 多处加结构化日志行；O1 已有一部分（会话状态行），但格式是中文自由文本。
- 只读与安全：只写日志，安全。
- 性能：可忽略。
- 局限：**事件不等于状态**。按钮可不可按是若干条件的合取，事件流里要么每个条件变化都记、再在控制端重算一遍
  `AllowRecoveryEntry` 与横幅逻辑（等于把界面逻辑复制到控制端，一改就漂），要么记的就是快照——那就是 A 的时间线。
- 结论：单独做不推荐；作为 A1 的「变化时间线」收进来。

### C. 由服务端汇总回答「屏上应该显示什么」

- 能回答：会话就绪与原因码、告警板、操作结果、恢复会话（O6）。这些现在就能查，排障时应当先查。
- 回答不了：屏上的按钮与横幅。读到的：服务端不知道 `recoveryResumeEnabled`（`FieldOperatorRoleRoster.cs:33` 的注释说协议里没有），
  不解析 `OperationProgress`，不专门处理锁存码；界面层的锁存闸门根本不上线。
- 改动面：要服务端知道这些，就得加协议字段或新消息。推的：那是一次协议发版，会作废受影响切片的 G1/G2/G3 证据
  （工作区 `CLAUDE.md`「A protocol push still voids gate evidence」）。
- 另一个问题：即使加了，服务端回答的是「按服务端的理解屏上**应该**显示什么」，与屏上**实际**显示什么可能不同，
  而现场排障恰恰最常问的是两者为什么不同（截屏证据第一次就是这种情形）。
- 结论：不作为本问题的方案；作为排障时的第一站保留现状。

### D. 开发期工具（Snoop、Visual Studio 实时可视化树等）

- 能回答：完整可视化树、绑定、数据上下文，信息量最大。
- 不适合现场车，理由：
  - 推的：Snoop 要往目标进程注入一段代码才能读树；VS 的实时可视化树要挂调试器。两者都改变了运行中的进程，
    挂调试器还可能在断点或异常时暂停全部线程——在一台可能正在执行仓门操作的车上不可接受。
  - 推的：它们是交互式 GUI 工具，要在车上的交互桌面里操作，会抢前台、抢焦点（见第 2 节「焦点」）。
  - 要在车上装第三方工具，违背工作区「第三方二进制不进车、不进 git」的一贯做法。
- 适合：开发机、L2 本机装置上调界面绑定问题。
- 结论：只在开发机用，现场不用。

### E. 继续用截屏＋UIA

- 能回答：画出来的结果；锁屏时窗口图与 UIA 仍可取（`ws16-20261011/SUMMARY.md` 第二次）。
- 局限：读不到为什么；文案或布局一改，下游判读就要跟着改；一次要在车上注册计划任务、跑 25～30 秒；
  交互会话不在时取不到。
- 只读与安全：脚本已有禁用调用扫描与焦点核对（`focusDisturbed=false`），两次实车都通过。
- 结论：保留为兜底，**并承担对照职责**：A 的快照是否忠实，用同一时刻的 UIA 读数对照来证明（第 6 节）。

### 汇总

| 方案 | 能回答「屏上有什么」 | 能回答「为什么」 | 能回答「从什么时候」 | 改产品代码 | 新端口 | 改协议 | 现场可用 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| A1 文件 | 能 | 能 | 能（时间线） | 是，`Wpf` 层 | 否 | 否 | 是 |
| A2 HTTP | 能 | 能 | 否 | 是，`Wpf`＋`AutomationHost` | 是（回环） | 否 | 需安全评审 |
| B 日志 | 间接 | 部分 | 能 | 是，多处 | 否 | 否 | 是 |
| C 服务端 | 否 | 部分 | 部分 | 两端都改 | 否 | 是 | — |
| D Snoop | 能 | 能 | 否 | 否 | — | 否 | 否 |
| E 截屏 | 能 | 否 | 否 | 否 | 否 | 否 | 是（现状） |

## 4. 推荐：A1，ViewModel 屏幕状态快照写本机文件

### 4.1 做法

- 构造器：一个只读的 `HmiScreenStateBuilder`，入参是 `MainViewModel`（或它的只读接口），只读公开属性，产出一个 record。
  **字段名与 XAML 绑定的属性名一致**，让「屏上按钮 X 绑的是属性 Y」与「快照里 X 的值是 Y」可以机械对照。
- 触发：订阅 `MainViewModel.PropertyChanged` 与几个集合的 `CollectionChanged`，防抖后用
  `Dispatcher.BeginInvoke(..., DispatcherPriority.Background)` 取一次值；外加定时心跳。构造完交给后台写出器。
- 写出：两份文件，放在日志目录旁（具体位置在 T2 里定，见第 7 节）：
  - `hmi-state.json`：当前快照，写临时文件再原子替换，读者永远读到完整的一份；
  - `hmi-state-yyyyMMdd.ndjson`：只在内容指纹变化时追加一行（指纹去掉时间戳，做法同 `AutomationApi.cs:237` 的
    `RemoveObservationTimestamps`），按天切分，保留期与日志一致。
- 控制端：`8005-workspace` 的 `remote-ops/onboard-hmi/scripts/` 下新增一个读取脚本，`ssh` 读文件、判新鲜度、打印摘要；
  加一个 `-CrossCheck` 模式，同时调 `17-capture-hmi-screen.ps1`，比对两者。
- 内容边界：不写凭据（恢复认证凭据本来就不持久化，`WireToGateRecovery.cs:267-271`）；不写操作员正在输入、尚未提交的文本框内容；
  只写已经显示在屏上的东西和它的直接输入。

### 4.2 为什么不是 A2

- 生产环境已有明令禁止开回环自动化接口（`Configuration.cs:466-471`），A2 要么改这条校验、要么另做评审；A1 不涉及。
- A2 要么复用带写端点的现成主机（不可接受），要么拆出只读主机，工作量不比 A1 小。
- A1 顺带给出时间线与「界面卡死」信号，A2 没有。

### 4.3 推荐方案改不改 HMI 行为

不改。理由逐条：只调 getter；在调度线程上用 `Background` 优先级排队，排在所有输入与渲染之后；
序列化和写盘不在调度线程；写出失败只记日志；不调任何窗口、焦点、输入 API。
**仍有一条代价要明说**：它在调度线程上增加了少量排队工作。推的：量级是界面每次刷新本来就要做的属性读取，
T1 要用量测证明（见验收标准）。

## 5. 拆票建议

| 票 | 仓 | 内容 | 依赖 |
| --- | --- | --- | --- |
| T1 | onboard-hmi | 屏幕状态快照模型与构造器，不写盘 | 无 |
| T2 | onboard-hmi | 写出器（当前文件＋时间线）、配置开关、接线 | T1 |
| T3 | 8005-workspace | 控制端读取脚本与 `-CrossCheck` 对照模式 | T2 合入并部署到一台车 |
| T4（可选） | onboard-hmi | 「操作记录」落盘：`OperatorEventPublished` 写结构化日志行 | 无，可与 T1 并行 |

### T1 验收标准

- [ ] 快照覆盖 `MainWindow.xaml` 里每一个 `Button` 的可见与可按：一个读 XAML 文本的护栏测试列出所有按钮的
      `IsEnabled`／`Visibility` 绑定属性，断言每个都在快照字段里。新加按钮而不进快照时它红。写明它覆盖不到什么
      （例如绑定写在样式或代码后置里的情形）。
- [ ] 横幅：快照同时给出横幅输出与 `WireToGateHmiPresentation.Create` 的全部输入。
- [ ] G2：在至少三个既有场景（正常装货中、会话需恢复、致命故障锁存）里断言快照内容，其中锁存场景要断言恢复入口在快照里为不可按、
      而业务层判据为真——这正是 O4 回答不了的那一条。
- [ ] 只读护栏：构造器源码扫描，不出现属性 setter 调用、`ICommand.Execute`、`Focus`、`Activate` 等；同时说明扫描的边界。
- [ ] 量测：在 G2 夹具里量一次构造耗时，给出数字。

### T2 验收标准

- [ ] 原子写：并发读者在任何时刻读到的都是完整 JSON（测试里边写边读若干千次）。
- [ ] 写出器抛异常（目录只读、磁盘满的替身）时，业务与界面不受影响，日志里有一行。
- [ ] 时间线只在内容变化时追加；同一内容重复触发不追加。
- [ ] 心跳：调度线程被人为阻塞时，`writtenAt` 停止前进（这是 T3 判「界面无响应」的依据）。
- [ ] 配置开关与默认值按第 7 节用户的决定；`Production` 环境的行为有测试钉住。
- [ ] 源码禁用调用扫描覆盖写出器（无窗口、焦点、输入 API）。
- [ ] 本仓 CI 绿。

### T3 验收标准

- [ ] 只读：脚本只执行读文件命令，不在车上注册任务、不起进程；有自检扫描。
- [ ] 新鲜度判定：`writtenAt` 超过阈值（建议心跳周期的 3 倍）判 `STALE`，文件不存在判 `ABSENT`，各有退出码。
- [ ] `-CrossCheck`：同一时刻取快照与 UIA，逐项比对横幅文字、按钮可见与可按、到站格原始码，输出差异表。
- [ ] 本机 L2 真装置上跑一次对照，零差异；实车跑一次对照（不动车、只读文件与截屏），时间由调度安排。

### T4 验收标准

- [ ] 每条操作员事件落一行结构化日志（时间、`Kind`、消息、尝试号），与屏上「操作记录」一一对应。
- [ ] G2：`OperatorEventVisibilityTests` 的场景同时断言日志行。

## 6. 推荐方案的失败定义

出现下面任何一条，就判这个观测手段**不可信**，回退到截屏，并把它当缺陷处理：

1. **对照不一致**：同一时刻，快照与 UIA 读数在横幅文字、任一按钮的可见或可按、到站格原始码上不一致，且不能用两次读取之间的
   状态变化解释（`sequence` 前后不变）。
2. **漏项**：屏上有一个按钮或状态区块，快照里没有（T1 护栏本应拦住，拦不住即护栏失效）。
3. **静默陈旧**：车载端在运行、界面在刷新（截屏可见变化），而 `writtenAt` 不前进，且读取脚本没有报 `STALE`。
4. **改变了行为**：引入快照后，任何 G2 场景的行为、时序断言变化；或现场出现扫码焦点丢失、界面卡顿可归因于它；
   或写出器异常导致车辆故障或业务中断。
5. **泄露**：快照或时间线里出现凭据、认证凭证或尚未提交的输入内容。

其中 1 是最重要的一条：快照只有在被证明与屏一致的范围内才可信，所以 E（截屏）不能退役，要定期用来对照。

## 7. 需要用户拍板

**状态文件在 `Production` 环境是否默认开启。**

- 选项一（建议）：默认开启，配置可关。理由：它只写本机文件、不开端口、不碰窗口，价值恰恰在现场；默认关的话，
  每次排障前都要先改车上配置、重启 HMI，而重启本身会改变要观察的状态。代价：生产车上多一个后台写文件的组件，
  出问题时要能确认它与故障无关（T2 的异常隔离与第 6 节第 4 条就是为此）。
- 选项二：默认关，只在 v2 备用车（agv02、agv03）的现场配置里打开。代价：生产车（agv01 切到 v2 之后）出问题时没有这份数据。
- 与既有禁令的关系：`Configuration.cs:466-471` 禁的是带写端点的回环 HTTP 接口，不是写文件；但两者都是「生产环境的观测出口」，
  用户可能希望同一个原则对待，所以单独提出来。

文件放在程序目录的 `logs/` 旁还是 `%LOCALAPPDATA%\SQCD\8005AGV\`（日志簿所在）也要在 T2 定。推的：程序目录在重新部署时会被换成新包
（工作区 `CLAUDE.md` 记部署保留 `.previous` 一代），放在那里的文件随旧包走；`%LOCALAPPDATA%` 不受部署影响，
但它属于运行 HMI 的那个交互用户，SSH 登录的用户要有读权限。这一点要上车核实，本票没有上车。

## 8. 没做的事

- 没有上车：agv02、agv03 当天在 control-server#566 现场使用中。车上的日志文件实际位置、大小、日志簿路径都是按代码推的。
- 没有实跑任何方案；第 3、4 节的性能判断都是推的，由 T1、T2 的量测验收。
- 服务端那一侧只读了代码，没有查库。
