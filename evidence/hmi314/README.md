# hmi#314 证据：屏幕状态快照 T1b

本目录是 hmi#314（屏幕状态快照 T1b：横幅与入口背后的输入）的证据。重点是验收标准最后一条「发出的消息内容与时序不变」的对照，以及研究文档第 4.1 节改动所依据的实测。每条结论标明「读到的」（从代码或运行输出直接读出）或「推的」（推断，未实测）。

## 1. 结论先说

- 读到的：产品代码相对集成分支 `af19f3e06` 的全部改动，在 G2 假对端收到的安全类消息（`SafetyStateSnapshot`、`SafetyStateChanged`、`PreDepartureSafetyCheckResult`）上，**196 个基线稳定用例中有 191 个逐字节相同**（规范化六个随运行而变的键之后，含顺序与条数）。
- 读到的：不一致的用例全部是「多一条或少一条安全消息」，没有一处是同位置、同条数而内容不同。
- 推的：这些差异属于两种时序形状（见第 6 节），与本票无关。**这一条还在补跑基线对照，结论待定**（第 7 节）。
- 读到的：研究文档原来的「只升不降」规则在三种情形下会登记错摘要，三种都已用 G2 用例跑出来（第 8 节）。其中「就绪消息先于确认登记」原先是推的，实测是常态：40 轮里 40 轮。

## 2. 底座与提交

| 角色 | 提交 | 说明 |
| --- | --- | --- |
| 基线 | `af19f3e06`（集成分支，hmi#315 合入） | detached worktree，只在测试侧加了探针钩子；每次运行后 `git diff -- src` 为空 |
| 候选 | `d7cd011`（本分支） | 底座同为 `af19f3e06`；`git diff 113776c d7cd011 -- src` 为空（`113776c` 之后只改了测试） |

对照在 merge hmi#323（`27ecf245e`）之前完成。#323 改的正是 `MultiDemandJourneyG2Tests` 的夹具和用例，merge 之后两边跑的就不是同一批场景了。merge 之后（`713f45a`）只跑定向测试和整个 `SQCD.Agv.UnitTests`，不再重做对照。

## 3. 方法

- **探针钩子**：`tests/SQCD.Agv.WireToGateG2Tests/FakeControlServer.cs` 的 `WriteSafetyMessageProbe`。设了环境变量 `HMI314_PROBE_DIR` 时，假对端在释放时把收到的安全类消息原文按顺序写进文件，每个测试每台假对端一个文件；没设时什么都不做。
- **规范化**：只抹六个键，其余逐字节比较。四个由真实时钟决定：`sentAt`、`payload.observedAt`、`payload.validUntil`，以及 `messageId`（它是含 `observedAt` 的去重键的 StableUuid）。两个是测试自己随机生成的：`correlationId`、`payload.preDepartureSafetyCheckId`。读到的：在 `b2f602e` 上两遍会话层运行之间，原文不同的键只有前四个。
- **对比脚本**：`scripts/compare-g2-safety-messages.ps1`。
  - 默认模式：只比较在所有基线遍里都一致的用例。
  - `-AllowBaselineVariants`：候选的每段序列必须逐字节等于基线某一遍出现过的序列。
- **工具自检**：
  - 读到的：同一份数据，pwsh 脚本和独立写的 Python 实现得出相同结论（`b2f602e` 两遍：128 个稳定、9 个不稳定，名单一致；本轮变体比对：9 处，逐条一致）。
  - 读到的：把一条消息的原因码改掉，脚本报 `RESULT: 1 differences`、退出码 1。

## 4. 运行与作废记录

每段运行的汇总行原文见 `runs.txt`。每段都核对退出码和用例数：会话层 85 条（基线 76 条），业务层 137 条（基线 136 条，候选多了本票新加的用例），17 用例组 19 条。

17 个用例展开成 19 条，读到的：其中两个是带参数的 theory，各展开两组参数，即 `ARefusedCancellationEndedAfterTheManualCheckKeepsTheLoadsAttemptAcrossAReconnect`（`doorLeftOpen`）和 `AnUnknownHandoffOverALoadWhoseAckCameLateKeepsItsDoorAcrossAReconnect`（`ackAfterHandoffEnded`）。基线探针目录每遍正好 19 个文件。

作废的运行，以及原因：

| 运行 | 原因 |
| --- | --- |
| 候选 `113776c` 第 2 遍（第一次） | 读到的：红 `TheRunningDemandsUnknownResultIsShownBeforeTheQueuedCommandTakesOver`，栈在 `QueuedSlotCommandDisplay.cs:160`，与 hmi#323 提交 `e4c2835` 写明的根因相同 |
| 候选 `113776c` 第 2 遍（重跑） | 读到的：红 `ALatchedVehicleOpensNoDoorForASlotCommandAndReportsItFailedNotStarted`（`FatalFaultLatch.cs:92`）。这是**本票新断言的时序缺陷**，不是环境问题；修法见第 9 节 |
| 候选 `113776c` 第 1、3 遍 | 不计入结论。候选一侧改为全部出自 `d7cd011` |
| 基线 17 用例组第 9 遍 | 读到的：红的同样是 `QueuedSlotCommandDisplay.cs:160`（#323 那类）。这一遍跑在没有本票改动的 `af19f3e06` 上 |
| 第一轮压力连跑 13 轮 | 编译报 `MSB3021`（DLL 被刚中止的那次测试占用），命令把编译失败吞掉了，13 轮跑的都是旧二进制：每轮只有 1 个用例，新用例不存在 |
| 一次中止的候选第 2 遍 | 按调度要求扩大比较集合时中止，不是测试失败；半截数据已删 |

## 5. 对照结果

### 5.1 严格比对（`compare-stable.txt`）

基线全量 3 遍：210 个用例文件，196 个稳定（511 条消息），14 个不稳定。拿候选全量 3 遍比这 196 个，有 5 处差异：

- `ARefusedCancellationEndedAfterTheManualCheckKeepsTheLoadsAttemptAcrossAReconnect`（`doorLeftOpen: True`）
- `ALatchRefusesTheReopenAfterADoorShutEmptyAndReportsTheSlotFailed`
- `TheRepairReleaseIsShutToAVehicleWithNoRecoveryAdministrator`（在 `b2f602e` 两遍时就不稳定）
- `AHandoffThatOpenedNothingLeavesTheLoadsAckToSettleIt`
- `ARestartAfterTheLateLoadAcknowledgementResumesTheCancellationOverItsOpenDoor`

### 5.2 变体比对（`compare-variants.txt`）

17 个用例（19 条）由两部分组成：基线 3 遍里的 14 个不稳定用例，加上候选第一轮里出现了基线未见变体的 3 个。基线和候选各固定跑 10 遍，遍数在看到结果之前定死。每条在基线 10 遍里的变体数：

| 用例 | 基线变体数 |
| --- | --- |
| **`ARefusedSafetyChangeIsGivenUpAndTheServersSnapshotRequestIsAnswered`（按请求快照）** | 2 |
| **`ARefusedSafetyChangeWithNothingAfterItStillAnswersTheServersSnapshotRequest`（按请求快照）** | 3 |
| **`ASafetyChangeRefusedInTheHandshakeReplayIsGivenUpAndTheSessionRecovers`（按请求快照）** | 2 |
| `AGuidanceLineNamingTheTypedSublotStaysOutOfTheScreenState` | 2 |
| `ALatchMidwayThroughATwoSlotLoadKeepsTheFinishedSlotAndNamesOnlyTheOtherOne` | 2 |
| `ALatchWhileTheDoorIsAlreadyOpenLetsTheOperatorFinishTheLoadWithoutAnotherPulse` | 2 |
| `AnUnknownHandoffOverALoadWhoseAckCameLateKeepsItsDoorAcrossAReconnect`（False / True） | 3 / 3 |
| `APressAfterTheCommandIsReplayedIntoANewSessionIsANoticeNotAFault` | 2 |
| `APressConfirmedAfterTheLoadEndedUnknownSendsNothing` | 2 |
| `ARefusedCancellationEndedAfterTheManualCheckKeepsTheLoadsAttemptAcrossAReconnect`（False / True） | 1 / 2 |
| `ARefusedHandoffEndedAfterTheManualCheckKeepsTheLoadsAttemptAndDoorAcrossAReconnect` | 2 |
| `CompensationNamesItsOwnTargetAmongSeveralLoads` | 2 |
| `EveryLoadedDemandCanStillBeHandedOffAfterAReconnectAndARestart` | 1 |
| `EveryLoadedDemandOfAJourneyCanBeHandedOff` | 2 |
| `TheLoadCancellationEntryClosesOnceTheLoadsNonCompletedResultIsOnItsWay` | 1 |
| `TheRunningDemandsUnknownResultIsShownBeforeTheQueuedCommandTakesOver` | 2 |
| `WireToGateG2Tests.AnOperationInterruptedWhileWaitingForTheOperatorIsSettledFromLiveIoOnTheNextSession` | 2 |

按请求快照这条发送路，读到的：集成分支上只有加粗的这三个用例真正发出了按请求快照（判据：同一连接里出现第二份 `SafetyStateSnapshot`），三个都不稳定，原因是快照之前多一条或少一条 `SafetyStateChanged`，版本号随之变化。快照本身的 `departureSafe`、原因码、`slotStates` 在各遍之间相同。

候选的 13 段（17 用例组 10 遍，加全量 3 遍里的这 19 条）中有 9 处不在基线变体集合里，逐行形状见第 6 节。

## 6. 差异的形状

逐行看过的结果（读到的）：

- **A 类，收尾时那一条重复上报有或没有**：末尾一条 `SafetyStateChanged` 与前一条内容相同，只是版本号加一，候选里多了或少了这一条。例子：`TheRepairReleaseIsShut…`、`ARefusedCancellationEnded…(False)`、`ARefusedSafetyChangeWithNothingAfterIt…`、`AnUnknownHandoff…(True)`。
- **B 类，断线前最后一条变化的确认有没有回来**：回来了，重连握手就没有要补发的，快照沿用已接受的版本号 vN；没回来，就先补发 vN，再发快照 vN+1。例子：`ARefusedCancellationEnded…(True)`、`ARefusedHandoffEnded…`、`EveryLoadedDemandCanStillBeHandedOffAfterAReconnectAndARestart`（4 处；其中「无补发、快照沿用 v3」这种序列在基线 13 段里没出现过）。
- **其他**：
  - `ALatchRefusesTheReopen…`：基线是两步变化（先「门没关＋锁存」，再「只剩锁存」），候选合成了一步，中间状态被合并。
  - `AHandoffThatOpenedNothing…`、`ARestartAfterTheLateLoadAck…`：末尾多一条内容不同的变化。

推的：A 类对应「一次 IO 变化引出两次安全上报请求」的机理（工作区记忆 `hmi-safety-pass-double-request`）：第二次上报有没有赶在假对端关闭前发出，取决于时序。B 类和「其他」都是一次事件落在某个时刻之前还是之后。本票的登记代码只在确认之后读写一个引用、在构造摘要时记一份，不改发送内容与时机；握手选版本号的 `WireToGateSessionClient.cs:946-955` 没有动过。

## 7. 待补：基线补跑对照

调度要求在 `af19f3e06` 上把涉事用例各跑 10 遍，判法是：候选的每一种序列，只要在「基线 17 用例组 10 遍＋基线全量 3 遍＋补跑 10 遍」里出现过，就判与本票无关。结果待填。

## 8. 「只升不降」规则的三种失效情形：G2 用例

用例都在 `tests/SQCD.Agv.WireToGateG2Tests/WireToGateG2Tests.AcceptedSafety.cs`。

| 情形 | 用例 | 跑出来的事实 |
| --- | --- | --- |
| 新一代握手同号换内容（`:946-955`） | `ANewGenerationsHandshakeAtTheSameVersionReplacesTheOldGenerationsSummary` | 同号、代号变大、摘要不同（用例先断言了这三个前提） |
| 初值没有摘要（`:129`、`:969`） | `AcceptedSafetyAfterTheHandshakeIsTheSnapshotTheServerAcknowledged` | 握手快照的版本号等于配置值 1，来源从 `Configured` 变成 `HandshakeSnapshot` |
| 就绪消息先于确认登记（`:778` 对 `:4957`） | `TheNaturalOrderOfAcknowledgementAndReadinessAlwaysEndsWithTheChangesSummary`、`AcceptedSafetyAfterARequestedSnapshotIsTheSnapshotTheServerAcknowledged` | 按真服务端的顺序（先确认、紧跟就绪）：前者 5 次运行、每次 40 轮里 40 轮是就绪消息先登记；后者 5 次全是 |

## 9. 新断言的时序缺陷与修法

- 读到的：原断言写在 `ALatchedVehicleOpensNoDoorForASlotCommandAndReportsItFailedNotStarted` 里，写死「取消装货业务层为真」。业务层放不放出这个入口，取决于取快照时失败结果有没有已经报出，机器负载重时就会红。
- 修法：
  - 原用例改为对十个入口断言一个关系：最终值等于业务值经过锁存闸门，取消装货再加上「扫码之前」那一半。不写死哪个入口为真。
  - 新增确定时刻的用例 `ALatchOverAnOpenDoorClosesTheCancellationTheBusinessStillOffers`：门开着、操作员不动，等取消入口为真后锁存，等最终值变假再取快照。取完快照后核对操作仍在「等操作员」阶段，防止夹具的 2 秒操作员时限先到。
- 读到的：在 `d7cd011` 上把这两个用例压力连跑 20 轮，每轮 2 个用例，0 红。

## 10. 变异取红

| 变异 | 结果（读到的） |
| --- | --- |
| M1 就绪消息不按版本号查摘要 | 红 `AReadinessThatOvertakesTheChangesOwnRegistrationFindsTheChangesSummary` |
| M3 去掉「新一代握手可同号替换」 | 红 `ANewGenerationsHandshakeAtTheSameVersionReplacesTheOldGenerationsSummary` |
| M5 锁存前那一处不记取消装货的业务值 | 红 `ALatchedVehicleOpensNoDoorForASlotCommandAndReportsItFailedNotStarted`（变异在旧断言上做，修法之后要在新用例上重做） |
| M7 构造器忽略已接受安全摘要 | 红锁存用例与 `SwitchedOnWithASessionItWritesTheAcceptedSafetyAndASessionChangeAloneIsWritten` |
| M2、M4、M6 | 第一次的变异写法被「警告当错误」挡在编译阶段，没测到东西；已改写法，待补跑 |

## 11. 更正记录

- 我曾向调度报告「按请求快照在 128 个一致的用例里」，这是错的：当时只核了用例里调用了 `RequestSafetyStateSnapshotAsync`，没核消息层面真的发出了第二份快照。实际情况见第 5.2 节。
- 排查变体差异时，我把输出截到前 80 或 190 个字符，theory 的参数后缀被截掉，一度拿 `doorLeftOpen: True` 的文件去查 `doorLeftOpen: False` 的不匹配，以为脚本误报。实际是我看错了文件，脚本没错。

## 12. 原始数据位置

探针原始输出与每段 `dotnet test` 日志留在本机 `F:/8005-scratch/hmi314-baseline/`，不入库：`af19f3e/`、`af19f3e-u17/`、`d7cd011/`、`d7cd011-u17/`，以及 `*-void` 作废数据。本目录只放汇总与比对输出。
