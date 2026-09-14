# 车载端缺陷：状态未知的结果重启后既不报为待结清，也不补发

发现：2026-09-13，为控制服务端 G3 `FP-IS-03` 设计场景时对照协议向量 `CV-OPERATION-RESULT-UNKNOWN-RECONCILE` 的代码走查
本仓被观测版本：`w2g/b3-on-v2@c36d94c`（产品代码同 `04d0088`）
对端：控制服务端 `fp/b2-close`（同日服务端一半，缺陷单 `docs/defects/20260913-unknown-result-never-reconciled.md`）
协议：`protocol-v1.0.0@9f22db8`

## 结论先说

向量 `CV-OPERATION-RESULT-UNKNOWN-RECONCILE` 的顺序是 `OperationResult` → `DurableAck` → `RecoveryStateReport` → `OperationResult` → `DurableAck`，
车载端义务 `REPORT_UNKNOWN_AS_UNKNOWN`、`REPLAY_RESULT_ON_RECONNECT`。本仓只做到了第一条：

- `RecoveryStateReport.pendingResults` 永远是空的。日志里有这个字段、会序列化、会校验，但没有任何代码往里写。
- 已经收到 `DurableAck` 的结果，重连后不会再发。重连只重放**未确认**的发件箱消息，而且一旦有这类消息就不发报告。

所以向量第三、四条消息之间的「报告里列出、随后补发」在本仓从未发生。

2026-09-13 用户裁定两端按向量补齐。车载端这一半：**没能让操作结清的结果，记为待结清；之后每次建会话都在报告里列出它，
报告被确认后按新会话代号原样再发一次，直到这次操作结清。**

## 修复

- `WireToGateSlotOperationExecutor.RecordPendingResultAsync`：新增。把结果（`messageType`、`messageId`、`businessId` = 尝试号、`contentSha256` = `resultContentSha256`）
  记进日志的 `PendingResults`；要求该尝试正是日志里未结清的那一个，同时丢掉其他尝试遗留的条目。
  `MarkResultRecordedAsync`（操作结清）清空列表。
- `WireToGateBusinessService`：正式装卸与恢复续作两处，结果不是 `COMPLETED` 且日志检查点不是 `NONE` 时，**发送之前**先记待结清。
  与持久化发送同一时序，任何时刻重启都不会出现「发了但日志不知道它待结清」。
- `IWireToGateJournal.ReadOutgoingByMessageIdAsync` / `SqliteWireToGateJournal`：新增，按消息号读发件箱行。
- `WireToGateSessionClient`：
  - `SendRecoveryStateReportAsync` 只把「属于当前未结清尝试、且发件箱里有已确认原行」的条目写进 `pendingResults`，并把这些原行交回握手；
    报不出原行的条目不列出，否则服务端会一直等一条永远不来的补发。
  - 握手读完 `SessionReadiness`、接收循环启动之后，`ReplayAcknowledgedResultAsync` 逐条补发：原行只改 `sessionGeneration`，
    经接收循环等 `DurableAck`（服务端在就绪行之后会补发自己的待发命令，可能先到），核对消息号、类型与新行哈希。发件箱行不改。
  - 有未确认消息时的旧重放路径不变（不发报告，也就不补发）；那些消息确认之后，下一次建会话照常报告并补发。

结清以外的收尾路径（取消、补偿、故障货物移交）会清掉或换掉日志里的未结清尝试，报告只列属于当前未结清尝试的条目，因此遗留条目不会被报出。

## 证据

| 项 | 结果 |
| --- | --- |
| 新增 G2 测试 `AnUnknownResultIsReportedAsPendingAfterARestartAndReplayedOnceTheReportIsAcknowledged`（`FP-IS-03` / `CV-OPERATION-RESULT-UNKNOWN-RECONCILE`） | 修复前红（产品代码暂存、测试不变）：第一次连接发出 `UNKNOWN` 结果并被确认；重启后第二次连接的报告 `unsettledSlotOperationAttemptId` 为该尝试、`pendingResults` 为 `[]`，之后没有补发。修复后绿：报告列出该结果，报告之后同一 `messageId` 的结果按新会话代号补发、载荷逐字段相同，开锁次数不变 |
| `FakeIoModuleClient.LockerWaitTimesOut` | 新开关：等锁状态超时，让操作以 `UNKNOWN` 结束（与真装置上关空门等到超时同理） |
| `FakeControlServer.SimulateOnboardProcessRestart` | 新方法：车载端进程在同一份日志上重启，替身不再沿用上一进程被接受的能力与安全版本（真服务端每个新会话按本次快照应答） |
| 切片标注 | 向量同属 `FP-IS-03` 与 `FP-IS-07`，测试两者都标；少标 `FP-IS-07` 时 `IntegrationSliceTraitArchitectureTests` 三条红 |
| 全量 | `SQCD.Agv.UnitTests` 200 passed，`SQCD.Agv.WireToGateG2Tests` 51 passed，`dotnet format --verify-no-changes` 通过 |

真车载端上的完整顺序由控制服务端 G3 `FP-IS-03` 核对。
