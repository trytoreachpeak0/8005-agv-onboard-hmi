# onboard-hmi#186（含 hmi#172 审查补充的同族）红绿证据

**要修的事**：车载端重启后，`SettleInterruptedExclusiveAsync`（按日志与实时 IO 结算上次没做完的仓位操作）判断一个仓「开过没有」只看活动集与已完成集。执行中途失败、但到达安全终点的那一格两样都不在（日志里活动集为空、它不在已完成集、结论 `UNKNOWN`），于是被报成 `NOT_STARTED`、原因码为空——ADR-cross-0058 决定 6 把 `NOT_STARTED` 留给「没开过」的仓。

## 做了什么

- `OpenedByThisAttempt`：「开过」＝活动集 ∪ 已完成集 ∪ 日志里该仓结论不是 `NOT_STARTED`。`ResumeExclusiveAsync` 与 `SettleInterruptedExclusiveAsync` 共用这一处。
- 结算里，日志结论为 `UNKNOWN` 的格一律报 `UNKNOWN`，沿用日志里的原因码，**读数终态也不记 `COMPLETED`**。依据 ADR-cross-0017：「若未结操作曾因锁 DI 等硬件故障进入 VehicleRecoveryRequired，即使重启后信号恢复，也必须先取得 HardwareRecoveryConfirmation，不能仅凭可证明检查点自动续行。」票面原写「读到终态记 COMPLETED」，调度按这句改了验收。管理员授权的恢复（`ResumeExclusiveAsync`）仍认这一格、不再开锁。
- 没开过的格沿用日志里自己的原因码（物理字段仍是实时读数）。冲突落日志后、记待答前进程退出，重启结算带回 `SLOT_OPERATION_CONFLICT`。
- **审查第 1 条**：首次执行里，被单门检查（REQ-0357）在本仓第一次脉冲之前拒绝的格，报 `NOT_STARTED`、原因码是那扇门的（决定 6），而不是 `UNKNOWN`。「没发过脉冲」只在进程内确知时才认：`DriveSlotToTargetStateAsync` 记本次调用有没有请求过脉冲，失败分支只在首次执行（`firstRun`）里据此报 `NOT_STARTED`；恢复轮一律仍报 `UNKNOWN`（日志分不出上一轮开没开过，失败分支会把未完成的格都写成 `NOT_STARTED`，审查第 4 条）；重开时被拒仍 `UNKNOWN`。
- `HandleSlotOperationAsync` 显示闸那段注释按现行派发改准（control-server#211；由 control-server#294 负责用服务端测试钉住，该票待做）。hmi#182 以此结论关闭，车载端不加拒绝。

## red-before-fix.txt

四条 #186 用例放到修复前的 `18663e4` 上：**四条全红**，三条中途失败用例红在「失败的仓应为 UNKNOWN、实际 NOT_STARTED」，冲突用例红在原因码为空。

## review/：审查（PR #190）后的修前红与底座对照

- `point1-red-at-6e5adb4.txt`：审查所看的头部 `6e5adb4` 上，审查复现（装货 [1,2]、5 号门开着、之后关上、重启结算）红在「1 号格应为 NOT_STARTED、实际 UNKNOWN」；反向用例、恢复轮用例与第 2、3 条两条用例在 `6e5adb4` 上绿（行为未变或已有）。与事先写下的预期一致。
- `points2-3-red-at-18663e4.txt`：第 2、3 条的两条用例钉住的是相对底座的行为变化，在 `18663e4` 上两条都红（实际 `COMPLETED`、期望 `UNKNOWN`），与审查实测一致。

## red/：变异探针（8 个，头部 `47451cb`）

每份开头是事先写下的预期与对 HEAD 的 diff，构建 `0 Error(s)` 才算数，末尾列失败断言位置；每轮后按字节备份还原，blob 与 HEAD 一致。`--filter WireToGateSlotOperationExecutor`（74 条）。

| 文件 | 改回的错 | 实际（与预期一致） |
| --- | --- | --- |
| `N1` | 「开过」去掉第三条 | 红 4 条：三条中途失败用例 + 恢复的 `AResumeStillCounts…`；活动集失败格与二次结算两条不红（那一格在活动集里） |
| `N2` | **票面原口径**：失败的格读到终态就记 COMPLETED（调度指定的注入） | 红 4 条：两条安全终点失败用例、活动集失败格、二次结算 |
| `N3` | 失败的格报通用原因码 | 红 4 条，红在原因码；二次结算不红（它期望的正是通用码） |
| `N4` | 没开过的格丢掉日志原因码 | 红 2 条：冲突用例、脉冲前被拒的结算用例 |
| `N5` | 第三条放宽到连 NOT_STARTED 也算开过 | 红 5 条：冲突、`LeavesTheSlotsAfter…`、恢复的两例、脉冲前被拒的结算用例 |
| `N6` | 恢复轮也认「没发过脉冲」 | 只红 `ASlotRefusedBeforeAResumesFirstPulseIsStillUnknown` |
| `N7` | 重开时的拒绝也当作脉冲前 | 只红 `ASlotRefusedOnAReopenIsStillUnknown` |
| `N8` | 首次执行脉冲前的拒绝改回 UNKNOWN（审查第 1 条原样） | 红 5 条：既有契约用例 4 例（改后期望 NOT_STARTED）+ 脉冲前被拒的结算用例 |

`N7` 第一次写成「无条件抛脉冲前异常」，变量 `pulseRequested` 因此未被使用，项目把这个警告当错误、构建失败，那一轮作废；改成「保留变量、条件恒为脉冲前」后重跑。

## green/（头部 `47451cb`）

- `unit-tests.txt`：**544 通过、0 失败**（基线 535，在 `18663e4` 上实数；新增 9 条）。
- `dotnet-format-verify.txt`：`exit=0`。
- `g2-multi-demand-and-station-deadline.txt`：`MultiDemandJourneyG2Tests`、`StationDeadlineExpiredG2Tests` 与架构测试，**92 通过**，含 #146/#152/#156 的 7 条。G2 全量走 CI。

## real-rig/

- `summary-6e5adb4.txt`：审查前的头部 `6e5adb4`，run 35591732768，`real-onboard-compensate-then-reconnect` PASS 66s。审查后代码变了，新头部的真装置在复审之后补跑。

## 守不到的

- **「读数终态也不自动记完成」的代价**：失败的那一格（安全终点或活动集里）重启前已装好，结算仍报 `UNKNOWN`，要走一次恢复（恢复时不再开锁）。ADR-cross-0017 要的人工确认。
- **上一次结算写下的 UNKNOWN 会保留**（有意的取舍，审查第 3 条）：两次都死在「写下结算检查点、结果进发件箱」之间，第二次结算不会因为读到终态转成 COMPLETED。日志状态已是 UNKNOWN，ADR-cross-0017 要求保持阻断；它与执行失败按形状和原因码都分不开（`SLOT_STATE_UNKNOWN` 两边都会写）。
- **恢复轮被单门检查拒绝的格报 UNKNOWN**，即使这一轮没发过脉冲：日志分不出上一轮开没开过，保守不错报。
- **向量残留到不了结算，前提是现状**：清掉 `RecoveryVector` 的 6 处里只有 `ForgetSettledVector` 保留向量写下的结果，它只被补偿、纠正、故障取货交接、强制机械恢复四种向量调用；结算在发件箱已有该 attempt 的 `OperationResult` 时直接返回，而这四种向量开始前结果必然已在发件箱——前提是「服务端收到结果才判恢复」。
- **恢复路径的第三条会把失败向量留下的结果算作开过**：#172 起就这样，本票没有扩大。
