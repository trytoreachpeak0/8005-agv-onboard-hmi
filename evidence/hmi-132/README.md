# hmi#132 证据

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/132（批次 6 审查后续；范围已由用户 2026-09-20 缩小到原第 4 条）

## 测什么

结果行在「握手读完发件箱之后、就绪之前」落盘。这次握手的补发已经读过发件箱、看不到这一行，会话又还没就绪，结果发送口
先落盘再以 `WIRE_TO_GATE_NOT_READY` 拒绝。能把它送出去的只剩就绪之后的恢复投影。要抓的两种坏法：结果根本不发，或者发了、
结算了两次。

测试：`StationDeadlineExpiredG2Tests.AResultPutOnFileInsideTheHandshakeWindowIsSentAfterTheReadinessAndSettledOnce`
（`tests/SQCD.Agv.WireToGateG2Tests/StationDeadlineExpiredG2Tests.ResultInHandshakeWindow.cs`），`FP-IS-05`／
`CV-CONNECTION-LOSS-SAFE-FINISH`，两例：

| 例 | 握手卡在 |
| --- | --- |
| `AfterOutboxRead` | 握手刚读完未确认发件箱（`ReadUnacknowledgedOutgoingAsync` 返回之后） |
| `AfterRecoveryReportAck` | 恢复状态报告已被确认、就绪还没读（报告的 `MarkOutgoingAcknowledgedAsync` 之后） |

窗口是确定性构造的：包装日志 `HandshakeWindowJournal` 把握手卡在上表的点上，卡住期间操作员关门、装货结束、结果行落盘。
判据分两层：

1. **前提**（放行前断言）：结果行是在握手被卡住期间写入的；写入时会话已连接、是本次握手的代次、就绪状态是 `Recovering`；
   放行前握手未完成、会话仍是 `Recovering`、发件箱里这一行未确认、服务端没收到任何 `OperationResult`。
2. **结果**（放行后断言）：
   - 服务端恰好收到一条 `OperationResult`，在本次连接上，`messageId` 是 attempt，`overallOutcome` 是 `COMPLETED`；
   - 它的到达排在车载端公布本代次 `Ready`／`RecoveryRequired` 之后（服务端 `EnvelopeReceived` 与车载端 `StateChanged` 记在同一条时间线上）；
   - `MarkResultRecordedAsync` 恰好被调用一次（按直接调用帧计，不按整条栈），恰好一次把 attempt 从未结算清成 `ResultRecorded`；
   - 会话回到 `Ready`，锁只开过一次。

## 修前就绿，判别力由注入故障证明

`green/00-new-g2-test-6f8e213.txt`：测试提交 `6f8e213`（未改产品代码）上两例都绿。现有设计（未就绪时先落盘、就绪后恢复投影
重发，hmi#127）在这个窗口里成立，测试没有暴露缺陷，所以本票没有实现提交。

为了证明这条用例不是恒绿，用 `tools/inject-hmi132.ps1` 在 `6f8e213` 上逐个注入三种坏法。脚本要求每个替换恰好命中 1 处，
`--no-incremental` 编译并要求 `0 Error(s)`，跑完从字节备份还原并核对 SHA-256、刷新时间戳。注入前先写下的预期与实际一致：

| 注入 | 模拟的坏法 | 预期红点 | 实际 |
| --- | --- | --- | --- |
| `red/M1-no-resend-after-readiness-6f8e213.txt` | 恢复投影不重发未确认结果（`TryResendUnacknowledgedResultAsync` 直接返回 false），即 hmi#127 之前的行为 | 两例都红在等待「会话回到 Ready 且已结算」超时（第 92 行） | 两例红，第 92 行 `Timed out after 10s waiting for: the session back to Ready and the load settled` |
| `red/M2-replayed-before-readiness-6f8e213.txt` | 握手在发恢复状态报告之前再读一次发件箱、把迟到的结果补发掉，即就绪之前发出 | 只有 `AfterOutboxRead` 红在「到达晚于就绪」（第 112 行）；`AfterRecoveryReportAck` 的结果行在注入点之后才落盘，应仍绿 | `AfterOutboxRead` 红在第 112 行 `The result has to arrive after this generation's readiness.`；`AfterRecoveryReportAck` 绿 |
| `red/M3-settled-twice-6f8e213.txt` | 结算路径把「补记已完成结果」调用两次 | 两例都只红在 `RecordingCalls == 1`（第 117 行） | 两例红在第 117 行 `Expected one recording of the attempt, got 2`，失败信息附两次调用的栈 |

M3 说明为什么要数调用次数而不只数「清掉未结算」：第二次调用是条件写，未结算已经清掉时它什么也不写、以
`SLOT_OPERATION_CONFLICT` 结束，只数写入会漏掉它。

开发中碰到的两处检查器问题，都已修正、都不在上表的证据里：

- 计数器最初按「整条栈里有没有 `MarkResultRecordedAsync`」计，数出 2。栈显示第二次是 `RecordAcknowledgedCompletedResultAsync`
  随后的缓存读取（`ReadRecoveryStateCachedAsync` 经 `UpdateRecoveryStateAsync`），它作为 `MarkResultRecordedAsync` 完成后的同步
  续体运行，栈底还挂着那一帧。改为只看包装类之外的第一个调用帧。
- 注入脚本第一版用 `Copy-Item` 还原，备份保留旧时间戳，下一轮增量构建没重编 `SQCD.Agv.Infrastructure`，M3 那一轮带着 M2 的
  二进制跑，`AfterOutboxRead` 红在了 M2 的位置。该轮作废重跑，脚本改为 `--no-incremental` 并刷新时间戳；上表是重跑结果，
  之后无注入重编跑出的基线即 `green/00-*`。

`green/01-architecture-tests-6f8e213.txt`：`ArchitectureTests` 90 条全过（含 `IntegrationSlice` 等于 `ProtocolVector` 投影的约束）。

## 门禁与真装置

`ONBOARD_HMI_G2` 与布局检查以车载端 CI 为准；真装置 `real-onboard-compensate-then-reconnect` 走 control-server 的 `l2.yml`
（`rig=real`）。两者的 run 号与摘录记在 PR 正文，不进本目录。
