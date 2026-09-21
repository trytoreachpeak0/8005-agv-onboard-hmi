# onboard-hmi#186（含 hmi#172 审查补充的同族）红绿证据

**要修的事**：车载端重启后，`SettleInterruptedExclusiveAsync`（按日志与实时 IO 结算上次没做完的仓位操作）判断一个仓「开过没有」只看活动集与已完成集。执行中途失败、但到达安全终点的那一格两样都不在（日志里活动集为空、它不在已完成集、结论 `UNKNOWN`），于是被报成 `NOT_STARTED`、原因码为空——ADR-cross-0058 决定 6 把 `NOT_STARTED` 留给「没开过」的仓。

## 做了什么

- `OpenedByThisAttempt`：「开过」＝活动集 ∪ 已完成集 ∪ 日志里该仓结论不是 `NOT_STARTED`。`ResumeExclusiveAsync` 与 `SettleInterruptedExclusiveAsync` 共用这一处（恢复那边在 #172 已有第三条，这次抽出来）。
- 结算里，日志结论为 `UNKNOWN` 的格（本 attempt 在它上面失败过）一律报 `UNKNOWN`，沿用日志里的失败原因码，**读数终态也不记 `COMPLETED`**。依据 ADR-cross-0017：「若未结操作曾因锁 DI 等硬件故障进入 VehicleRecoveryRequired，即使重启后信号恢复，也必须先取得 HardwareRecoveryConfirmation，不能仅凭可证明检查点自动续行。」票面原写「读到终态记 COMPLETED」，调度按这句改了验收。管理员授权的恢复（`ResumeExclusiveAsync`）仍认这一格、不再开锁。
- 没开过的格沿用日志里自己的原因码（物理字段仍是实时读数）。冲突落日志后、记待答前进程退出，重启结算带回 `SLOT_OPERATION_CONFLICT`（hmi#172 审查补充）。
- `HandleSlotOperationAsync` 显示闸那段注释按现行派发改准（服务端同站逐条串行、恢复中不发下一条，control-server#211；由 control-server#294 负责用服务端测试钉住，该票待做）。hmi#182 以此结论关闭，车载端不加拒绝。

## red-before-fix.txt

四条新用例放到修复前的 `18663e4` 上（临时 worktree，只加入测试文件）：**四条全红**，三条中途失败用例红在「失败的仓应为 UNKNOWN、实际 NOT_STARTED」，冲突用例红在原因码为空。与事先写下的预期一致。

## red/：变异探针（5 个）

每份开头是事先写下的预期与对 HEAD 的 diff，构建 `0 Error(s)` 才算数，末尾列失败断言位置；每轮后按字节备份还原，blob 与 HEAD 一致。`--filter WireToGateSlotOperationExecutor`（69 条）。

| 文件 | 改回的错 | 实际（与预期一致） |
| --- | --- | --- |
| `N1-opened-without-third-clause` | 「开过」去掉第三条（结算原来的样子） | 红 4 条：三条中途失败用例 + 恢复的 `AResumeStillCountsTheSlotThatFailedAtASafeFinishOnceItReadsFinal`——共用判定两边一起红 |
| `N2-failed-slot-counted-completed-on-final-reading` | **票面原口径**：失败的格读到终态就记 COMPLETED（调度指定的注入） | 红 2 条：`ASettlementDoesNotCount…EvenWhenItReadsFinal`、`ASettlementLeavesTheSlotsAfter…`（两条里失败的格都读到终态） |
| `N3-failed-slot-loses-its-reason` | 失败的格报通用原因码 | 红 3 条中途失败用例，红在原因码 |
| `N4-never-opened-slot-loses-journaled-reason` | 没开过的格丢掉日志原因码（#172 残留原样） | 只红 `ASettledOccupancyConflictKeepsItsReason` |
| `N5-opened-counts-not-started-results` | 第三条放宽到连 NOT_STARTED 也算开过 | 红 4 条：冲突用例、`ASettlementLeavesTheSlotsAfter…`、恢复的 `AResumeDoesNotCountASlotThisAttemptNeverOpenedAsCompleted`（Load、Unload） |

## green/

- `unit-tests.txt`：`dotnet test tests/SQCD.Agv.UnitTests -c Release`，**539 通过、0 失败**（基线 535，在 `18663e4` 上实数；新增 4 条）。
- `dotnet-format-verify.txt`：`exit=0`（format 自己的退出码）。
- `g2-multi-demand-and-station-deadline.txt`：`MultiDemandJourneyG2Tests`、`StationDeadlineExpiredG2Tests` 与架构测试，**92 通过**。其中包括 #146/#152/#156 的 7 条：本分支一度带着 #182 的车载端拒绝时它们变红（场景前提是「A 待恢复时 B 接手执行」），撤掉之后回到绿。G2 全量走 CI。

## real-rig/

`summary.txt`：CI `rig=real`，run 35591732768，`real-onboard-compensate-then-reconnect` × 1 **PASS 66s**。三端从场景那一步读：车载端 `6e5adb4f`、服务端 `0b19397b`、模拟器 `fb5f7c59`；`RIG_*` 只命中 1 次源码回显。两次启动的版本串都带 `6e5adb4f`。这个场景重启车载端再走补偿入口，不经过「中途失败后重启结算」那条路径：它证明本 PR 没把恢复入口和重启路径改坏，#186 的行为本身由单测、修前红与变异探针证明。

## 守不到的

- **「读数终态也不自动记完成」的代价**：操作员在重启前已经把失败的那一格装好，结算仍报 `UNKNOWN`，服务端照旧进恢复，要走一次恢复（恢复时 `ResumeExclusiveAsync` 认这一格、不再开锁）。这是 ADR-cross-0017 要的人工确认，不是缺陷。
- **向量残留到不了结算，前提是现状**：`WireToGateRecoveryVectorExecutor.WriteVectorStateAsync` 用同一 attempt 写 SlotResults。清掉 `RecoveryVector` 的 6 处里只有 `ForgetSettledVector` 保留这些结果，它只经 `HandleRecoveryVectorCommandAsync` 被补偿、纠正、故障取货交接、强制机械恢复四种向量调用；结算在发件箱已有该 attempt 的 `OperationResult` 时直接返回，而这四种向量开始前结果必然已在发件箱——前提是「服务端收到结果才判恢复」。这是现状，不是车载端的构造保证。
- **恢复路径的第三条会把失败向量留下的结果算作开过**：#172 起就这样，本票没有扩大，未改。
