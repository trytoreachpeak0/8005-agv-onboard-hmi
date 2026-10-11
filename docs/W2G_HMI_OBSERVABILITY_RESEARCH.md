# 调研：从控制端了解车载端 `SQCD.Agv.Wpf` 的状态，比截屏更好的办法

票：trytoreachpeak0/8005-agv-onboard-hmi#308（调研票，不改产品代码）
本仓被观测版本：`w2g/fp-v2-impl@270c20d`
服务端被观测版本：`8005-agv-control-server` `fp/v2-impl@a25ef54e`（只读，`git show`／`git grep`）
截屏工具：`8005-workspace` `remote-ops/onboard-hmi/scripts/17-capture-hmi-screen.ps1`（8005-workspace#16，PR 8005-workspace#17，
本文引用的行号按 `d52f0725`，该 PR 已以合入提交 `8d6c850e` 合入），证据 `remote-ops/onboard-hmi/evidence/ws16-20261011/`
日期：2026-10-11；按调度 Coordinator 10 的审查（M1～M9、L1～L7）修订一次

## 结论先说

1. **推荐方案：车载端把「操作员此刻看到的状态」从 ViewModel 读出来，写成本机的只读状态文件**（当前一份
   JSON，外加一条只记变化的时间线 NDJSON），控制端经现有 SSH 通道读文件。这是票面方案 A 的「文件」形态，
   并吸收方案 B 的时间线。不开新端口，不碰窗口，不改任何业务判据。**它有一条会改变 HMI 行为的风险路径**：
   构造快照的代码跑在 WPF 调度线程上，在那里抛出的未处理异常会被判为致命故障、整车锁存（第 4.3 节）。
   推荐方案成立的前提是这条路径被整体隔离并有测试钉住。
2. **不推荐在生产车上打开车载端已有的自动化 HTTP 接口**（`SQCD.Agv.AutomationHost`）。它确实只绑回环、带
   `/api/v1/snapshot`，但同一个接口还带一个**写**端点（录入 sublot），配置校验明令生产环境禁止开启；而且它的
   快照取的是业务层判据，不是屏上的最终值（锁存等界面层闸门不在里面），回答不了「屏上能按哪个按钮」。
3. **截屏＋UIA 保留**，角色从「主要手段」降为「兜底与对照」：状态文件可信与否，正是靠它定期对照来证明。
4. **服务端汇总（方案 C）回答不了按钮与横幅**：服务端没有「屏上显示什么／能按什么」的字段；锁存只以安全摘要里的一个
   原因码间接到达，`recoveryResumeEnabled` 与界面层闸门在线路上看不到。补上它要改协议、发版、作废证据。
5. **Snoop 一类开发期工具（方案 D）不适合现场车**：要在车上装工具、往运行中的进程注入或挂调试器，
   且只能在交互桌面上用。`dotnet-dump`／`procdump` 同理，现场不用。

需要用户拍板的一件事见第 7 节：状态文件在 `Production` 环境是否默认开启（审查给了一个分两步的候选）。

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
| O3 | 屏上「操作记录」（`MainViewModel.Logs`，四个来源） | 业务服务的操作员事件、控制器快照、修正窗口、业务拒绝 | 屏上「操作记录」列表里那几句话 | 进程外不可见；不落盘；操作员可清空 |
| O4 | 嵌入式自动化 HTTP 接口 | `SQCD.Agv.AutomationHost` | 业务层快照：会话、旅程、恢复状态、可否录入 | 界面层最终值；生产环境禁止开启 |
| O5 | UIA 树（截屏工具读的） | `MainWindow.xaml` 的 `AutomationId`／`ItemStatus` | 屏上文字；部分格子的原始码 | 为什么；不可见的状态 |
| O6 | 上报服务端的消息 | 协议消息，服务端落库 | 会话就绪、安全原因码、告警板、操作结果、恢复报告 | 屏上显示与按钮；锁存与配置开关（无专门字段） |
| O7 | ViewModel 状态 | `MainViewModel`（partial） | 绝大多数绑定的最终值，包括多数按钮的可见与可按 | 模态框是否弹着（在 code-behind）；目前没有任何出口把它带出进程 |
| — | 诊断 runner（hmi#297） | `scripts/run-staged-g3-recovery-ack-drop.ps1` | 不适用 | 它是开发机上的 G3 诊断台，不是车上的出口 |

hmi#297 的诊断 runner 在票面里被列为候选出口，读到的：它是本机起整套车载端、服务端、模拟器做 G3 恢复场景的脚本
（hmi#297 票面：「诊断用 runner，不出正式证据」），不在车上运行，也不提供任何查询接口，所以不进入下面的比较。

下面逐项写依据。

### O1 文件日志

- 读到的：`FileAppLogger.Write` 每条一行，格式 `时间戳(ISO 8601)\t等级\t来源类名\t消息`，追加到
  `<程序目录>/<logging.directory>/agv-yyyyMMdd.log`（`src/SQCD.Agv.Infrastructure/FileAppLogger.cs:16`、`:26`、`:33`）；
  默认目录 `logs`（`src/SQCD.Agv.Wpf/appsettings.json:126-128`）。写失败时只接住 `IOException` 与
  `UnauthorizedAccessException` 两类、`Debug.WriteLine` 后继续（`FileAppLogger.cs:37-44`）；其他异常类型会抛给调用方。
- 读到的：日志按天分文件，但 `FileAppLogger` 里没有任何按保留期清理的代码（`FileAppLogger.cs` 全文 53 行）。
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

### O3 屏上「操作记录」

- 读到的：界面集合 `Logs` 有四个写入点：
  1. 业务服务的操作员事件，经 `ApplyWireToGateOperatorEvent`（`MainViewModel.cs:617-641`，写入在 `:628`）；
     `RaiseOperatorEvent` 本身只触发事件、不写文件日志（`src/SQCD.Agv.Wpf/WireToGateBusinessService.cs:3981-3992`），
     App 的接线在 `src/SQCD.Agv.Wpf/App.xaml.cs:243-247`；
  2. 修正窗口关闭的提示（`MainViewModel.cs:1097`）；
  3. 业务拒绝 `ReportOperatorRejection`（`MainViewModel.cs:2332`）；
  4. 控制器快照经 `OperatorRecordFormatter` 转成的记录，`AppendOperatorRecord`（`MainViewModel.cs:2382-2392`，写入在 `:2390`）。
- 读到的：集合上限 300 条（`MainViewModel.cs:13`、`:2526-2532`）；操作员可以按「清空记录」整个清掉（`ClearLogsCommand`，`MainViewModel.cs:216-222`）。
- 读到的：可见性的测试在 `tests/SQCD.Agv.WireToGateG2Tests/OperatorEventVisibilityTests.cs:21`，它钉的是
  「该出的提示一定出」，不是「提示被记录到进程外」。
- 推的：屏上「操作记录」那一栏的内容在进程外只能靠截屏读到；操作员一清空、进程一退出就没了。只在第 1 个来源上落盘会漏掉另外三个，
  所以落盘要做在 `Logs` 这一层（T4）。

### O4 嵌入式自动化 HTTP 接口

- 读到的：`OnboardAutomationHttpServer` 只许绑回环地址（`src/SQCD.Agv.AutomationHost/AutomationHttpServer.cs:17-25`），
  路由有 `GET /api/v1/health`、`GET /api/v1/snapshot`、**`POST /api/v1/sublots/submit`**、`GET /openapi/v1.json`
  （`src/SQCD.Agv.AutomationHost/AutomationApi.cs:48-167`，按本文件内行号）。
- 读到的：快照字段是 `AgvId`、控制器快照、会话快照、旅程快照、恢复状态、`CanSubmitSublot`、期望 sublot、当前尝试号与阶段
  （`src/SQCD.Agv.Core/OnboardAutomation.cs:7-17`），由 `WpfOnboardAutomationFacade.ReadSnapshot` 组装
  （`src/SQCD.Agv.Wpf/WpfOnboardAutomationFacade.cs:35-64`）。
- 读到的：快照里唯一的「能不能按」是 `CanSubmitSublot`，取业务服务的值（`WpfOnboardAutomationFacade.cs:59`）；它自己已含锁存
  （`src/SQCD.Agv.Wpf/WireToGateBusinessService.cs:308-309`）。也就是说，`MainWindow.xaml` 的 22 个按钮里，这份快照只覆盖了
  扫码提交这 1 个。十个恢复入口（「十个」出自 `MainViewModel.cs:752` 的注释与 `RecoveryEntryWriteSiteArchitectureTests`）
  根本不在快照里。它们的最终值要再过界面层的锁存闸门：九个过 `AllowRecoveryEntry`（`MainViewModel.cs:664-665`、`:691-692`），
  取消装货过 `AllowLoadCancellationEntry`（锁存期间只剩扫码之前那一半，`MainViewModel.cs:755-757`）。这一层只存在于 ViewModel。
- 读到的：配置默认关（`appsettings.json:39-43`），**`Production` 环境开启会被校验拒绝**：「Production环境禁止启用车载端
  自动化loopback接口，除非完成独立安全评审。」（`src/SQCD.Agv.Infrastructure/Configuration.cs:466-471`）
- 读到的：快照读日志簿是同步阻塞的（`.GetAwaiter().GetResult()`，`WpfOnboardAutomationFacade.cs:98-101`），与 O2 那把信号量同一把。
- 读到的：产品里它只在 `App.xaml.cs:358-375` 按配置启动；本仓的测试与脚本里，直接起这个主机的只有单元测试
  `tests/SQCD.Agv.UnitTests/OnboardAutomationHostTests.cs`，没有 G2 场景或脚本调用它。`scripts/run-first-integration.ps1` 里的
  `/api/v1/snapshot` 是模拟器的接口（端口 58006），不是它。本仓之外（服务端 L2、G3）有没有调用它，本票没有查。
- 推的：它是为「测试驱动扫码」设计的，不是为「现场观测」设计的。

### O5 UIA 树

- 读到的：`MainWindow.xaml` 有 42 处 `AutomationProperties.AutomationId`、22 个 `Button`；若干格子用 `ItemStatus` 带原始码，
  例如电量、充电周期、到站、发车倒计时档位（`src/SQCD.Agv.Wpf/MainWindow.xaml:50`、`:56`、`:106`、`:213`）。
- 读到的：按钮的可见与可按常常绑在两个不同的属性上，例如取消装货 `IsEnabled` 绑 `CanPressLoadCancellation`、`Visibility`
  绑 `CanRequestLoadCancellation`（`MainWindow.xaml:381-382`）。
- 读到的：截屏脚本用 `PrintWindow` 取窗口图、UIA 只读遍历，自带禁用调用扫描（`SetForegroundWindow`、`InvokePattern` 等）
  （`17-capture-hmi-screen.ps1:240-243`，按 `d52f0725`），每次在车上注册一次性计划任务进交互会话、跑完注销（`:161`、`:169`）。实车一次约 25～30 秒
  （`ws16-20261011/SUMMARY.md`：12:37:56 开始、12:38:26 写完）。
- 推的：ItemStatus 这条路已经在往「读原始码而不是读文案」走，但只覆盖了少数格子，按钮的可按状态 UIA 能读到 `IsEnabled`，
  读不到「为什么不可按」。

### O6 上报服务端的消息（服务端能看到什么）

以下是对服务端 `fp/v2-impl@a25ef54e` 的只读查询结果，由子代理检索、本会话抽查了其中 5 处原文（标 ✓）。

- 读到的 ✓：入站消息整条原样写进收件箱表 `ProtocolInbox`（`src/ControlServer.Host/Transport/OnboardMessageProcessor.cs:154-171`）；
  有两处脱敏后再入库：握手 `SessionHello` 走单独的路径（`OnboardMessageProcessor.cs:59-120`，子代理读到、未抽查）；
  `ExceptionRecoverySessionRequested` 先去掉恢复认证凭证（✓ `OnboardMessageProcessor.cs:131-133`）。
- 读到的 ✓：`OperationProgress` 服务端只回 `DurableAck`，载荷不解析（`OnboardMessageProcessor.cs:548-549`），服务端没有「当前仓位操作阶段」
  的结构化字段；但整行原样存在 `ProtocolInbox` 里，按 `RequestJson` 能查到（推的：表上没有车号列，要从 JSON 里取）。
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
- 读到的：多数按钮的可见／可按是 `MainViewModel` 的属性，例如 `CanRequestWireToGateRecovery`、`CanPressLoadCancellation`
  （`MainViewModel.cs:1282-1330`），绑定在 `IsEnabled`／`Visibility` 上；恢复入口的最终值 = 业务服务给的判据 × 界面层锁存闸门
  （`MainViewModel.cs:744-778`、`:2395-2440`）。
- 读到的：「可按」并不完全同源，有三种例外：
  - 扫码提交、手动提交、清空记录走的是 `Command` 绑定（`MainWindow.xaml:143`、`:148`、`:994`），可按与否由
    `AsyncCommand.CanExecute` 决定，它还要求「上一次没在执行」（`src/SQCD.Agv.Wpf/ViewModels/AsyncCommand.cs:21`）；
  - 「现场确认充不上」的选项按钮在 `ItemsControl` 模板里，没有 `IsEnabled` 绑定，靠 `Click` 处理器（`MainWindow.xaml:736-750`）；
  - code-behind 里有 24 处模态 `MessageBox.Show`（`MainWindow.xaml.cs`，另有 `App.xaml.cs` 2 处）。确认框弹着时，ViewModel 里的
    按钮属性仍是「可按」，但操作员实际上只能点确认框。ViewModel 不知道有没有模态框。
- 读到的：横幅有两个写入者。
  - 控制器快照路径 `ApplySnapshot`：`StateText` 由控制器 `State`／`ErrorCode` 决定，「设备异常」「请处理」都出自这里
    （`MainViewModel.cs:2236-2245`）。致命故障锁存时走的是这一条。
  - W2G 呈现路径：`WireToGateHmiPresentation.Create(会话快照, 操作快照, canSubmit, …)`（`MainViewModel.cs:2440-2459`，
    纯函数在 `src/SQCD.Agv.Wpf/ViewModels/WireToGateHmiPresentation.cs:13-60`），输入里有会话的 `ReasonCodes`。
  - 两条路径都经 `PublishGuidance`，一条操作员提示会把说明行压住 8 秒（`MainViewModel.cs:16`、`:2360-2371`）。
- 读到的：会话的 `ReasonCodes` 只说到「`DEPARTURE_UNSAFE`」这一层；为什么不安全，在车载端算出的安全摘要的 `reasonCodes` 里
  （`src/SQCD.Agv.Infrastructure/WireToGateSafetyEvaluator.cs:35` 起的 `Evaluate`）。安全摘要经三条路发出：
  - 握手快照与按请求发的快照：会话客户端 `CreateSafetySummary`（`WireToGateSessionClient.cs:4610`，调用点 `:952`、`:2377`）；
  - 最常发的 `SafetyStateChanged`（`WireToGateSessionClient.cs:749-779`）和 `PreDepartureSafetyCheckResult`（`:735-745`）：
    内容由业务服务 `EvaluateSafety` 算好再交给会话客户端（`src/SQCD.Agv.Wpf/WireToGateBusinessService.cs:4651-4677`），
    不经过 `CreateSafetySummary`。
- 读到的：「最近一次」只以私有字段的形式存在，而且在业务服务里，不在会话客户端：`_pendingSafetyChange`（待确认的那份，含摘要）、
  `_lastSafetySignature`（已被服务端接受的那份的签名，签名是摘要加各仓原始 IO 的 JSON）（`WireToGateBusinessService.cs:165-166`）。
  签名在服务端接受的版本号追上待确认版本时写入（`:2148-2152`），换代时清空（`:2167-2171`），放弃的变化也会清（
  `WireToGateBusinessService.AbandonedOutbox.cs:134-136`）。会话客户端只保留「服务端已接受的版本号」`_acceptedSafetyStateVersion`
  （`WireToGateSessionClient.cs:83`），推进它的 `AdvanceSafetyStateVersion` 有六个调用点：`:778`（`SafetyStateChanged` 送达）、
  `:969`（握手快照）、`:2386`（按请求的快照）、`:2734`（换代后重绑的持久消息送达）、`:4957`（`SessionReadiness` 带回的版本）、
  `:5043`（迟到的送达确认）。其中只有前三处手里有摘要本身。
- 读到的：安全摘要的 `reasonCodes` 不写文件日志（`src/` 里 grep 不到带安全原因码的 `logger.Write`）；文件日志里只有会话就绪的原因码（O1）。
  所以「为什么显示需要恢复」要同时看：横幅是哪条路径写的、那条路径的输入、以及服务端已接受的那份安全摘要，而最后这一项今天在车载端进程外看不到。
- 读到的：ViewModel 的写入都经 `RunOnUiThread` 调度到 WPF 调度线程（`MainViewModel.cs:2591-2606`）。从别的线程读它没有同步保证。
  **没有 `Dispatcher` 时（`Application.Current` 为空）它在调用线程上直接执行**（`MainViewModel.cs:2593-2597`）。
- 读到的：G2 夹具直接构造真正的 `MainViewModel`（`tests/SQCD.Agv.WireToGateG2Tests/MultiDemandJourneyG2Tests.cs:408`），
  并有一个容忍并发追加的读日志方法（`tests/SQCD.Agv.UnitTests/ViewModelLogsReadArchitectureTests.cs:5-21`）。
  推的：G2 里没有 WPF 应用，所以 `RunOnUiThread` 走的是「直接执行」那一支；`App.xaml.cs` 的接线与
  `OnDispatcherUnhandledException` 在 G2 里都碰不到。
- 推的：**ViewModel 是「屏上显示什么、能按什么」最接近完整的结构化来源**，但不是全部：模态框、`Command` 的执行中状态和模板里的按钮
  要另外补。它现在没有任何出口。

### 顺带的事实：焦点

- 读到的：扫码枪按键盘输入工作，`CanSubmit` 变真时界面把焦点放回 `ScanTextBox`（`src/SQCD.Agv.Wpf/MainWindow.xaml.cs:42-56`）。
- 推的：任何抢走前台焦点的观测手段都会让扫码枪打到别的窗口里，这是「不能抢焦点」最直接的业务后果。

### 顺带的事实：调度线程上的未处理异常

- 读到的：`App.OnDispatcherUnhandledException`（`src/SQCD.Agv.Wpf/App.xaml.cs:504`）按 `OnboardFailureClassification.Classify` 分类；
  未登记的异常一律判为 `FatalFault`（`src/SQCD.Agv.Core/OnboardFailureClassification.cs:85-87`），随后
  `EnterFatalFault("UNHANDLED_UI_ERROR", …)`（`App.xaml.cs:522`），整车锁存并弹出说明。
- 推的：所以任何挂在调度线程上的观测代码（`PropertyChanged` 处理器、`BeginInvoke` 进去的快照构造）只要抛一个异常，后果就是锁车。
  `PropertyChanged` 是同步回调，处理器在 `SetProperty` 里抛异常，还会把 `RefreshWireToGateInputStateCore` 这类一次写十几个属性的
  方法打断在半途，留下一半新一半旧的入口状态。

## 3. 方案比较

评价维度：能回答什么、改动面、对只读与安全的影响、性能、成本、怎么测。

### A. 车载端提供只读的本机状态出口

内容统一叫「屏幕状态快照」：从 `MainViewModel` 在调度线程上读出的一份结构化记录，字段清单见第 4.1 节。
出口通道除了文件与 HTTP，还有命名管道、ETW 事件、.NET 诊断 IPC，比较如下：

| 通道 | 新监听面 | 读者要在车上跑什么 | 判断 |
| --- | --- | --- | --- |
| 本机文件（A1） | 无 | 只读文件 | 推荐 |
| 回环 HTTP（A2） | 回环 TCP 端口 | 发请求 | 见下表 |
| 命名管道 | 一个管道服务端，ACL 要另配 | 要有客户端程序 | 与 A2 同类代价，又没有现成代码，不选 |
| ETW 事件 | 无 | 要起会话收事件（`logman`／`tracerpt` 或自写） | 适合事件流，不适合「此刻状态」；收集要在车上起会话，不选 |
| .NET 诊断 IPC（`dotnet-counters`／EventPipe） | 运行时自带的诊断端点（推的：.NET 默认开启，本仓未见关闭它的配置） | 要在车上装诊断工具并附着进程 | 适合计数器与性能，不带业务状态；附着本身是对运行中进程的操作，不选 |

A1 与 A2 的差别：

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
- 只读与安全：快照构造器只调属性 getter；不调任何窗口 API，不碰焦点。**风险在调度线程**：见第 2 节「调度线程上的未处理异常」，
  构造器或 `PropertyChanged` 处理器抛异常就是锁车，所以它们必须整体 try/catch（第 4.3 节）。写出器在后台线程，异常吞掉记日志，
  与 `JournaledOperationsFeed` 同一做法（`JournaledOperationsFeed.cs:70-78`）。A2 多出一个监听面，哪怕只读，也是
  「车上多开一个端口」，要写清代价。
- 性能：推的。属性变化时只置一个「脏」标记，防抖约 500 ms 后在调度线程上**以低优先级**取一次值，
  序列化与写盘放到后台线程；内容几 KB。另有约 5 秒一次的心跳写，证明调度线程还活着。对比现状：界面本身每次刷新都要
  重算这些属性，读一遍的开销在同一量级以下。发车倒计时每秒变一次文字，若按文字进指纹，时间线会每秒追加一行，
  所以倒计时只记档位与截止时刻（第 4.1 节）。
- 成本：A1 两张票（构造器、写出器）加控制端一个读取脚本；A2 再加拆分只读主机与安全评审。
- 怎么测：G2 夹具构造真 `MainViewModel`（`MultiDemandJourneyG2Tests.cs:408`），可以在既有场景里断言快照**内容**；
  但 G2 没有 `Dispatcher`，验证不了线程模型和「抛异常不锁车」，这两条要另起一个带真 `Dispatcher` 的单元测试
  （STA 线程上 `Dispatcher.Run`）。XAML 覆盖护栏（见第 5 节）用读 XAML 文本的单元测试实现，本仓已有同类写法
  （`tests/SQCD.Agv.UnitTests/RecoveryDemandChoiceXamlTests.cs`）。「不抢焦点」用源码禁用调用扫描守，与截屏脚本同一思路。

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
- 回答不了：屏上的按钮与横幅。读到的：服务端没有字段能读到车载端的入口开关（`FieldOperatorRoleRoster.cs:33`，
  `StationClearanceExit.cs:20` 写明协议里没有），不解析 `OperationProgress`；锁存只以安全摘要里的 `ONBOARD_FATAL_FAULT_LATCHED`
  一个原因码间接到达，没有专门处理；界面层的锁存闸门不上线。
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
  - 要在车上装第三方工具，与工作区「第三方二进制不进 git、按配方取用」的一贯做法相悖，车上也没有这类工具的配方。
- 同一类的还有转储工具 `dotnet-dump`、`procdump`：推的，取完整转储要挂起目标进程直到写完，大进程可能停顿秒级（本票未实测）。
  **现场车上一律不用**，包括「只取一次看看」。
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
| A1 文件 | 能（模态框靠 code-behind 登记） | 能 | 能（时间线） | 是，`Wpf` 层 | 否 | 否 | 是 |
| A2 HTTP | 能 | 能 | 否 | 是，`Wpf`＋`AutomationHost` | 是（回环） | 否 | 需安全评审 |
| B 日志 | 间接 | 部分 | 能 | 是，多处 | 否 | 否 | 是 |
| C 服务端 | 否 | 部分 | 部分 | 两端都改 | 否 | 是 | — |
| D Snoop | 能 | 能 | 否 | 否 | — | 否 | 否 |
| E 截屏 | 能 | 否 | 否 | 否 | 否 | 否 | 是（现状） |

## 4. 推荐：A1，ViewModel 屏幕状态快照写本机文件

### 4.1 做法

- 构造器：一个只读的 `HmiScreenStateBuilder`，入参是 `MainViewModel`（或它的只读接口），只读公开属性，产出一个 record。
  **字段名与 XAML 绑定的属性名一致**，让「屏上按钮 X 绑的是属性 Y」与「快照里 X 的值是 Y」可以机械对照。
- 字段分两组，对应 T1a、T1b：
  - **屏上看得见的（T1a）**：
    - 每个按钮的 `visible`、`enabled`。`visible` 明确定义为「绑定层可见」，即 `Visibility` 绑定属性的值，不管窗口是否被挡住或最小化；
      `enabled` 对 `IsEnabled` 绑定的取属性值，对 `Command` 绑定的取 `CanExecute`，对模板里的「充不上」选项按钮取选项列表本身；
    - **非按钮的可输入状态**：五个可编辑输入框各自的 `enabled` 与「是否非空」——扫码框 `ScanTextBox`（`IsEnabled` 绑 `CanSubmit`，
      `MainWindow.xaml:133-141`）、恢复原因（`:355`）、强制交接的 sublot 与接收人（`:507`、`:522`，`TextBox` 起于 `:499`、`:514`）、
      硬件恢复观察记录（`:553`，起于 `:546`）。注意 `:143` 是扫码框上的 `KeyBinding`，不是按钮；
    - **模态框**：是否有模态框弹着、叠了几层、最上层的标题与第一行正文。由 code-behind 在每个 `MessageBox.Show` 外面用
      `try/finally` 增减一个计数并记下标题（`MainWindow.xaml.cs` 24 处、`App.xaml.cs` 2 处），因为确认框可能叠加；快照读登记结果；
    - 横幅：`StateText`、`Guidance`、`HasWarning`、`HasError`，以及**最后写横幅的是哪条路径**（控制器快照，还是 W2G 呈现）；
    - 操作员提示：`operatorNotice` 的文字与到期时刻（8 秒保持，`MainViewModel.cs:16`）；
    - 旅程计划行、到站格、电量、充电状态的原始码（与 UIA 的 `ItemStatus` 同源）；
    - 仓位卡（锁、有货、目标）；
    - 发车倒计时：只记档位与截止时刻，不记每秒变化的文字与闪烁相位；
    - 最近 N 条「操作记录」（`Logs` 的尾部）。
  - **横幅与入口背后的输入（T1b）**：
    - 控制器快照的 `State`、`ErrorCode`、`Guidance`、`DeparturePermitted`；锁存与否；
    - 会话的 `Readiness`、`ReasonCodes`、`SessionGeneration`；
    - 当前仓位操作阶段；`WireToGateHmiPresentation.Create` 的其余输入；
    - 十个恢复入口各自的业务层判据（锁存闸门之前的值），与 T1a 的最终值并列，二者之差就是锁存闸门的作用；
    - **服务端已接受的那份安全摘要**：版本号、`departureSafe`、`reasonCodes`。今天它只以业务服务的私有签名存在（第 2 节 O7），
      进程外看不到。T1b 在会话客户端里加一个只读字段，记「服务端已接受的版本号＋对应的摘要」：
      - 在手里有摘要的三个发送点（`AdvanceSafetyStateVersion` 的 `:778`、`:969`、`:2386` 调用旁）记「这个版本号对应这份摘要」；
      - 送达确认后、版本号推进时，用 `Volatile.Write` 存一个不可变 record 的引用；不加锁、不做 I/O、不改发送路径；
      - 另外三个调用点（`:2734`、`:4957`、`:5043`）只带版本号，T1b 要说明这三种情形下字段怎么取值（例如保留已知最新的摘要并标出版本号不一致），
        不能静默显示一份旧摘要当作当前。
      这是推荐方案里唯一一处碰到 `SQCD.Agv.Infrastructure` 的改动。
- 在 G2 里构造快照（M4）：G2 没有 `Dispatcher`，ViewModel 的集合可能正被别的线程追加。T1a 的 G2 断言要在夹具的 UI 锁内构造快照，
  或者照 `Logs` 的做法用容忍并发追加的复制（`ViewModelLogsReadArchitectureTests.cs:5-21` 说的那个快照方法），二者选一并写明。
  - 元数据：`contentRevision`（内容指纹变化时加一）、`writtenAt`（每次写都更新，含心跳）、进程启动号、程序版本，
    以及 `status`：正常为 `ACTIVE`；快照因异常停用后，写出器最后写一份 `status=DISABLED` 并带停用原因与时刻，之后不再更新。
    这样 T3 能把「快照自己停了」和「界面卡死」（`writtenAt` 不前进但 `status` 仍是 `ACTIVE`）分开。
- 触发：订阅 `MainViewModel.PropertyChanged` 与几个集合的 `CollectionChanged`。**处理器里只置一个脏标记，不做任何别的事**；
  防抖后用 `Dispatcher.BeginInvoke(..., DispatcherPriority.Background)` 取一次值；外加定时心跳。构造完交给后台写出器。
- 写出：两份文件（位置见第 7 节）：
  - `hmi-state.json`：当前快照，写临时文件再原子替换，读者永远读到完整的一份；
  - `hmi-state-yyyyMMdd.ndjson`：只在内容指纹变化时追加一行（指纹去掉时间戳，做法同 `AutomationApi.cs:237` 的
    `RemoveObservationTimestamps`；倒计时只按档位进指纹）。按天切分，单文件设大小上限（建议 20 MB，超出当天停写并记一行日志），
    保留期设上限（建议 30 天），过期由写出器自己清理。清理只匹配 `hmi-state-*.ndjson`，绝不碰同目录的 `agv-*.log`。
    这份时间线是诊断材料，**不算 REQ-0272 的技术日志**，不替代、也不满足那条需求。
- 控制端：`8005-workspace` 的 `remote-ops/onboard-hmi/scripts/` 下新增一个读取脚本，`ssh` 读文件、判新鲜度、打印摘要；
  加一个 `-CrossCheck` 模式，与 `17-capture-hmi-screen.ps1` 配合比对两者（做法见第 6 节第 1 条）。
- 内容边界：
  - 快照与文件日志同级：放在日志目录或其旁，目录 ACL 与日志目录相同，不另开放读权限；
  - 只写已经显示在屏上、或已经写进文件日志、或已经发给服务端的东西，以及它们的直接输入。逐项说：横幅、按钮、到站与仓位本来就在屏上；
    会话就绪原因码、控制器的错误码与提示已在文件日志里（第 2 节 O1）；**安全摘要的 `reasonCodes` 不在文件日志里**，
    但它已经随安全摘要发给服务端、落在服务端 `SessionRecoveries` 里（O6），快照只是把车载端这一侧也记下来；
  - 五个可编辑输入框（扫码框、恢复原因、强制交接的 sublot 与接收人、硬件恢复观察记录）一律只写「是否非空」与「是否已提交」的布尔值，
    不写内容。接收人姓名是个人信息，观察记录是自由文本，都不进快照；
  - 不写凭据与认证凭证（恢复认证凭证本来就不持久化，`WireToGateRecovery.cs:267-271`），不写环境变量的值，只写变量名是否已设。

### 4.2 为什么不是 A2

- 生产环境已有明令禁止开回环自动化接口（`Configuration.cs:466-471`），A2 要么改这条校验、要么另做评审；A1 不涉及。
- A2 要么复用带写端点的现成主机（不可接受），要么拆出只读主机，工作量不比 A1 小。
- A1 顺带给出时间线与「界面卡死」信号，A2 没有。

### 4.3 推荐方案改不改 HMI 行为

设计目标是不改，下面每一条都是这个目标的前提，缺一条就不成立：

1. **调度线程上的代码整体隔离。**调度线程上的每一个入口——`PropertyChanged`／`CollectionChanged` 处理器、`BeginInvoke` 进去的
   快照构造、心跳定时器的 `Tick` 处理器——各自整体包在 `try/catch (Exception)` 里，捕获后记一行日志、停用快照（直到下次重启、
   并写出 `DISABLED` 标记），绝不重新抛出。**catch 里记日志这一步本身也要再包一层**：`FileAppLogger` 只接住 IO 与权限两类异常
   （`FileAppLogger.cs:37-44`），别的异常会从 catch 里再抛出来。理由见第 2 节：调度线程上的未处理异常会被判为
   `UNHANDLED_UI_ERROR`、整车锁存（`App.xaml.cs:504`、`:522`，`OnboardFailureClassification.cs:85-87`）；处理器在 `SetProperty`
   里抛异常还会把入口刷新打断在半途。处理器只置脏标记，把这一路能抛异常的代码量压到最小。
2. 构造器只调 getter；在调度线程上用 `Background` 优先级排队，排在所有输入与渲染之后。
3. 序列化和写盘不在调度线程；写出失败只记日志。
4. 不调任何窗口、焦点、输入 API。
5. code-behind 登记模态框只是在 `MessageBox.Show` 外面套一层 `try/finally` 增减计数，不改弹框本身；登记代码同样不得抛异常。

**仍有一条代价要明说**：它在调度线程上增加了少量排队工作。推的：量级是界面每次刷新本来就要做的属性读取，
T1a 要用量测证明（见验收标准）。

**线程模型**（T2 要写进代码注释并用测试钉住）：处理器与构造器在调度线程；写出器在一个专用后台任务里串行执行，一次只写一份；
心跳定时器在调度线程上以 `Background` 优先级触发构造，所以调度线程卡住时心跳也停，这正是「界面无响应」的信号。

## 5. 拆票建议

| 票 | 仓 | 内容 | 依赖 |
| --- | --- | --- | --- |
| T1a | onboard-hmi | 快照模型与构造器：按钮、模态框、横幅、操作员提示、屏上原始码。不写盘 | 无 |
| T1b | onboard-hmi | 快照里横幅与入口背后的输入：控制器状态、会话、业务层判据、服务端已接受的安全摘要 | T1a |
| T2 | onboard-hmi | 写出器（当前文件＋时间线）、调度线程隔离、配置开关、接线 | T1a（T1b 可后补字段） |
| T3 | 8005-workspace | 控制端读取脚本与 `-CrossCheck` 对照模式 | T2 合入并部署到一台车；`17-capture-hmi-screen.ps1` 已随 8005-workspace#17 合入（`8d6c850e`），无待办依赖 |
| T4（可选） | onboard-hmi | 「操作记录」落盘：在 `Logs` 这一层写结构化日志行，覆盖四个来源与清空动作 | 无，可与 T1a 并行 |

### T1a 验收标准

- [ ] 护栏：一个读 XAML 文本的测试列出 `MainWindow.xaml` 里每一个 `Button` 的 `IsEnabled`、`Visibility`、`Command` 绑定，
      带 `Click` 而没有 `IsEnabled` 绑定的按钮，以及每一个可编辑 `TextBox` 的 `IsEnabled` 与 `Text` 绑定（`KeyBinding` 按它所在的输入框算），
      断言每一个都在快照字段里有对应；另一个读 code-behind 文本的测试断言每一处
      `MessageBox.Show` 都经过模态框登记。新加按钮或弹框而不进快照时它们红。写明它们覆盖不到什么
      （例如绑定写在样式里、按钮在代码里动态创建）。
- [ ] 横幅：快照给出横幅输出与「最后写横幅的路径」；操作员提示给出文字与到期时刻。
- [ ] G2：在至少三个既有场景（正常装货中、会话需恢复、致命故障锁存）里断言快照内容；快照在夹具的 UI 锁内构造，或用容忍并发追加的复制，
      二选一并写明（第 4.1 节「在 G2 里构造快照」）。锁存场景要断言横幅来自控制器快照路径、
      恢复入口在快照里为不可按。
- [ ] 只读护栏：构造器源码扫描，不出现属性 setter 调用、`ICommand.Execute`、`Focus`、`Activate` 等；同时说明扫描的边界。
- [ ] 量测：在 G2 夹具里量一次构造耗时，给出数字。

### T1b 验收标准

- [ ] 快照给出 `WireToGateHmiPresentation.Create` 的全部输入，以及控制器快照的 `State`／`ErrorCode`／`Guidance`。
- [ ] 十个恢复入口的业务层判据与最终值并列；G2 锁存场景断言「业务层为真、最终值为假」——这正是 O4 回答不了的那一条。
- [ ] 服务端已接受的安全摘要：会话客户端按第 4.1 节记「已接受版本号＋对应摘要」，快照带出版本号、`departureSafe` 与 `reasonCodes`；
      G2 覆盖三条发送路（握手快照、按请求快照、`SafetyStateChanged`），断言快照里的那份与服务端假对端确认的那一份一致；
      并说明并测试只带版本号的三个调用点下字段的取值。
- [ ] **发出的消息内容与时序不变**：加字段前后，同一组 G2 场景里假对端收到的安全类消息逐字节相同、顺序相同。

### T2 验收标准

- [ ] **调度线程隔离（对应 4.3 第 1 条）**：一个起真 `Dispatcher` 的单元测试（STA 线程上 `Dispatcher.Run`），分别注入必抛异常的
      构造器、处理器、心跳 `Tick`，以及一个必抛非 IO 异常的日志器，断言：测试 `Dispatcher` 的 `Dispatcher.UnhandledException`
      触发零次（单元测试起不了 `App`，所以断言落在 `Dispatcher` 上，不落在 `OnDispatcherUnhandledException` 上）、
      `RefreshWireToGateInputStateCore` 写的入口值与不挂快照时相同、快照已停用并写出 `DISABLED` 标记。
- [ ] **入口护栏**：一个源码扫描断言快照代码里每一个调度线程入口（事件处理器、`BeginInvoke`／`InvokeAsync` 的委托、定时器 `Tick`）
      方法体整体包在 `try/catch (Exception)` 里，catch 里的记日志再包一层；写明扫描的边界。
- [ ] **线程模型（M4）**：同一类真 `Dispatcher` 测试断言构造在调度线程、写出在非调度线程；调度线程被人为阻塞时 `writtenAt` 停止前进
      （这是 T3 判「界面无响应」的依据）。G2 里 `RunOnUiThread` 直接在调用线程执行，验证不了这两条，所以必须是这类测试。
- [ ] **时间线体积（M5）**：发车倒计时走完一个完整周期，时间线只按档位变化追加，不按秒追加；单文件大小上限与保留期清理各有测试，
      清理测试同时断言同目录里过期的 `agv-*.log` 不被删除。
- [ ] 原子写：并发读者在任何时刻读到的都是完整 JSON（测试里边写边读若干千次）。
- [ ] 写出器抛异常（目录只读、磁盘满的替身）时，业务与界面不受影响，日志里有一行。
- [ ] 时间线只在内容变化时追加；同一内容重复触发不追加。
- [ ] 配置开关与默认值按第 7 节用户的决定；`Production` 环境的行为有测试钉住。
- [ ] 源码禁用调用扫描覆盖处理器、构造器与写出器（无窗口、焦点、输入 API）。
- [ ] 本仓 CI 绿。

### T3 验收标准

- [ ] 只读：读取脚本只执行读文件命令，不在车上注册任务、不起进程；有自检扫描。
- [ ] 新鲜度判定：`status=DISABLED` 判 `DISABLED`；`writtenAt` 超过阈值（建议心跳周期的 3 倍）判 `STALE`；文件不存在判 `ABSENT`；各有退出码。
- [ ] `-CrossCheck`：先读一次快照，再跑一次 `17-capture-hmi-screen.ps1`，再读一次快照；两次快照的进程启动号与 `contentRevision`
      都相同才比对，否则判 `MOVING`、不下结论。比对横幅文字、按钮与输入框的可见与可按、模态框、到站格原始码，输出差异表。
- [ ] 本机对照：在本机 L2 真装置上（`8005-agv-control-server` 的 `Invoke-L2Scenario.ps1` 真车载端模式，车载端 WPF 起在本机桌面），
      在场景的几个停顿点各跑一次对照。`17-capture-hmi-screen.ps1` 的 `-SshAlias` 只接受 `agv02`、`agv03`（`:54` 的 `ValidateSet`），
      没有本机模式，所以 T3 要二选一并写明：给 17 号脚本加一个本机模式（直接在本机会话里跑同一份 UIA 读取，不经 ssh、不注册计划任务），
      或者另写一个本机 UIA 读取，与 17 号脚本共用字段口径。读取快照对本机直接读文件。零差异。
- [ ] 确定结论的次数：本机对照至少 10 次比对得出确定结论（不是 `MOVING`）；`MOVING` 的次数也报出来，占比过高说明夹取方法要改。
- [ ] 实车对照：一次，不动车、只读文件与截屏，时间由调度安排。

### T4 验收标准

- [ ] `Logs` 的四个写入点与「清空记录」各落一行结构化日志（时间、来源、`Kind`、消息、尝试号），与屏上「操作记录」一一对应；
      落盘做在 `Logs` 这一层，不在各个来源上分别加。
- [ ] G2：`OperatorEventVisibilityTests` 的场景同时断言日志行。

## 6. 推荐方案的失败定义

出现下面任何一条，就判这个观测手段**不可信**，回退到截屏，并把它当缺陷处理：

1. **对照不一致**：一次 `-CrossCheck` 里，前后两次快照的进程启动号与 `contentRevision` 都相同（UIA 读取被夹在中间、期间内容没变、
   进程没换），
   而快照与 UIA 读数在横幅文字、任一按钮的可见或可按、模态框、到站格原始码上不一致。
2. **漏项**：屏上有一个按钮、弹框或状态区块，快照里没有（T1a 护栏本应拦住，拦不住即护栏失效）。
3. **静默陈旧**：车载端在运行、界面在刷新（截屏可见变化），而 `writtenAt` 不前进，且读取脚本没有报 `STALE`。
4. **改变了行为**：
   - 引入快照后，任何 G2 场景的行为、时序断言变化；
   - 现场或日志里出现可归因于快照的 `UNHANDLED_UI_ERROR`（锁存原因里的异常栈落在快照代码中）；
   - 现场出现扫码焦点丢失、界面卡顿可归因于它；
   - 写出器异常导致车辆故障或业务中断。
   注意：G2 没有 `Dispatcher`，判不出调度线程上的问题，这一条的前两种情形主要靠 T2 的真 `Dispatcher` 测试和现场日志。
5. **泄露**：快照或时间线里出现凭据、认证凭证，或五个可编辑输入框（扫码框、恢复原因、强制交接的 sublot 与接收人、硬件恢复观察记录）里的
   任何文字内容。

其中 1 是最重要的一条：快照只有在被证明与屏一致的范围内才可信，所以 E（截屏）不能退役，要定期用来对照。

## 7. 需要用户拍板

**状态文件在 `Production` 环境是否默认开启。**

- 选项一：一开始就默认开启，配置可关。理由：它只写本机文件、不开端口、不碰窗口，价值恰恰在现场；默认关的话，
  每次排障前都要先改车上配置、重启 HMI，而重启本身会改变要观察的状态。代价：在还没有现场运行记录时，就让生产车多一个跑在调度线程
  上的组件，而它出错的后果是锁车（第 4.3 节）。
- 选项二：默认关，只在 v2 备用车（agv02、agv03）的现场配置里打开。代价：生产车（agv01 切到 v2 之后）出问题时没有这份数据。
- **选项三（审查的建议，本文采纳为推荐）：分两步。**T2 合入后先只在 agv02、agv03 的现场配置里开；跑满一个现场周期，确认
  T2 的调度线程隔离与时间线体积两组护栏都在、现场日志里没有任何 `UNHANDLED_UI_ERROR` 归因到它，再把 `Production` 默认翻成开，
  开关保留。翻默认值是一次单独的改动，届时再报用户。
- 与既有禁令的关系：`Configuration.cs:466-471` 禁的是带写端点的回环 HTTP 接口，不是写文件；但两者都是「生产环境的观测出口」，
  用户可能希望同一个原则对待，所以单独提出来。

最终由用户拍板。

文件放在程序目录的 `logs/` 旁还是 `%LOCALAPPDATA%\SQCD\8005AGV\`（日志簿所在）也要在 T2 定。推的：程序目录在重新部署时会被换成新包
（工作区 `CLAUDE.md` 记部署保留 `.previous` 一代），放在那里的文件随旧包走；`%LOCALAPPDATA%` 不受部署影响，
但它属于运行 HMI 的那个交互用户，SSH 登录的用户要有读权限。无论放哪，ACL 与日志目录相同（第 4.1 节）。这一点要上车核实，本票没有上车。

## 8. 没做的事

- 没有上车：agv02、agv03 当天在 control-server#566 现场使用中。车上的日志文件实际位置、大小、日志簿路径都是按代码推的。
- 没有实跑任何方案；第 3、4 节的性能判断都是推的，由 T1a、T2 的量测与测试验收。
- 服务端那一侧只读了代码，没有查库。
