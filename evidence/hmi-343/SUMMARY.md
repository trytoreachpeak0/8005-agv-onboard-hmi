# hmi#343 证据：在途取消报非 COMPLETED 后取消向量残留

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/343
基线：`w2g/fp-v2-impl@6a0b210`（含 hmi#164、hmi#337）。PR：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/pull/346

## 第 1 步：三条出路实测

见 `01-README.md` 与 `01-probe-*.txt`。补偿、故障交接、强制机械恢复全被 `RECOVERY_VECTOR_CONFLICT` 挡住；修复续行能走
但把已取消的装货重跑、且收不了尾。Coordinator11 据此裁定收归切换线、进生产前修。

## 修法（只动车载端）

1. **向量**：操作员发起的在途取消，结果一经确认为非 COMPLETED（发送时就拿到确认、重跑时补发拿到确认、重连后按发件箱收尾，
   三条路），与补偿、交接一样走 `ForgetSettledVector` 忘掉向量。`ActiveUnlockSlots`（交接过来时还开着的门）与
   `TakenOverSlotOperationAttemptId`（被接管的尝试）都留着，「可能已开过门」不丢；之后的恢复等待这扇门而不是再开它。
2. **入口**：同时把该尝试记为「取消已有结论」，在途取消入口按 onboard-hmi#254 已有的规则关闭。重启后由还原流程从发件箱读回
   （`WireToGateBusinessService.cs` 的还原，8c1fcb7 加入，见下面「提交历史说明」）。
3. **反馈**：入口关后仍到达业务层的按键走 `LOAD_CANCELLATION_ALREADY_CONCLUDED`，经 `PublishOperatorResponse`（不去重）应答：
   「这次装货已经取消过、取消已有结论，不能再取消；需要处理货物请联系维护人员，用「补偿清空」「故障交接」或「强制机械恢复」
   走恢复流程。」确认框跨过收尾才确认的按键原先回「对象已经变了，请重新按取消装货」（入口已关，误导），现在同样按取消已有结论作答。
4. **修复续行**：选「不重跑」。取消已有结论后「申请恢复」入口关闭，按下只回应答「…不能再恢复原装货。需要处理货物请改用
   「补偿清空」「故障交接」或「强制机械恢复」，或联系维护人员。」理由：续作重开仓门、要人把操作员取消掉的货再装一遍，与意图
   相反；被取消接管的装货，结论归那次取消（ADR-cross-0046）；B 没有自己的结果时真服务端照放修复续行，闸门只能在车上。

恢复状态只经 `IWireToGateJournal.UpdateRecoveryStateAsync`（`ForgetSettledRecoveryVectorAsync`）写；没有新写盘的持久消息。

## B 有没有自己的结果（审查 H1）

真服务端（control-server `OnboardRecoveryCoordinator`）收到对不上的取消结果只把需求置为 `RecoveryRequired`、旅程 `Blocked`，不碰
仓位操作；仓位操作只有收到非 COMPLETED 的 `OperationResult` 才是 `RecoveryRequired`，补偿要求这一条。所以：

- **B 没有结果**（装货被取消静默中止，现场常态）：服务端拒绝补偿，交接与强制机械恢复照放；车在会话中途进不了
  `RECOVERY_REQUIRED`，要靠重连握手报出未结尝试（`PENDING_FACT_RECONCILIATION_REQUIRED`）。服务端这一侧的缺口是
  control-server#697。本侧用例证明交接与强制机械恢复走通。
- **B 先报了 UNKNOWN**：仓位操作是需要恢复的，补偿合法。本侧用例证明补偿走通。

两种情形在用例里各自造确定（替身不改）：前者用日志簿包装层在取消向量写盘（装货已中止）那一刻才让 1 号仓读不到；后者在授权前
钩子里让 1 号仓读不到，并等车载端日志簿记下 B 的待发结果再放授权。第一版的替身开关 `HoldOperationOnUnreconciledLoadCancellationResult`
与真服务端不一致，已删除，`FakeControlServer.cs` 与基线无差异。

## 用例（`MultiDemandJourneyG2Tests.CancellationUnknownExits.cs`，另改写 hmi#164 的一条）

「修前」指 `src/` 临时换回 `5b86dca`（只有测试、产品代码与基线相同）跑，跑完 `git checkout HEAD -- src`，`git status` 干净。

| 用例 | B 的结果 | 钉住什么 | 红 |
| --- | --- | --- | --- |
| `AnAcknowledgedUnknownCancellationShutsTheEntryAndKeepsTheDoorInDoubt` | 无 | 确认后入口关、门的疑点与被接管尝试留着 | `02`（第一版） |
| `AnInFlightCancellationPressedAfterItsUnknownIsAcknowledgedIsAnswered` | 无 | 再按每次都有不去重的应答，点名三个出路 | `03`（第一版修前）、`14`（文案） |
| `ACompensationTakesOverAnAcknowledgedUnknownCancellationAndSettlesIt` | 先报 UNKNOWN | 「补偿清空」走通、ALL_EMPTY、日志簿清干净 | `04`（第一版修前） |
| `AFaultCargoHandoffTakesOverAnAcknowledgedUnknownCancellationWithNoLoadResult` | 无 | 「故障交接」走通、HANDED_OFF、不再开门（M1） | `16`（修前） |
| `AForcedMechanicalRecoveryTakesOverAnAcknowledgedUnknownCancellationAndKeepsItsDoor` | 无 | 强制机械恢复走通，1 号仓进隔离记录 | `05`（第一版修前） |
| `AResumeAfterRepairDoesNotRerunALoadWhoseCancellationIsConcluded` | 无 | 「申请恢复」关、按下只应答、不开会话不开门 | `06`、`08`（第一版）、`14`（文案） |
| `AcrossARestartAnAcknowledgedUnknownCancellationStaysShutAndRecoverable` | 无 | 重启后读回标记、入口仍关、交接可用、门的疑点在（H3 改为等读回日志） | `08`（第一版修前） |
| `AConfirmationLeftOpenAcrossTheReplayedAcknowledgementIsAnswered` | 无 | 确认丢失→重连补发→收尾；跨过收尾的确认框按下得到「取消已有结论」（H2 只数 `RECOVERY_BLOCKED`） | `07`、`08`（第一版）、`14`（文案） |
| `ARepressWhoseResendIsAcknowledgedForgetsTheVectorThere` | 无 | 重跑时补发的结果在发送里确认，向量当场忘掉（M3） | `16`（修前）；`15`（变异 :1064 3/3 红） |
| `AnAcknowledgedFailedCancellationShutsTheEntriesAndIsHandedOff` | 无 | FAILED（授权后锁存）同样关入口、断门的疑点（L1），复位锁存后交接走通 | `12`（第一版修前）、`16`（修前） |
| `ASingleDemandsAcknowledgedUnknownCancellationShutsTheEntryAndIsHandedOff` | 无 | 单需求同样关入口、门的疑点在、交接走通（M2） | `16`（修前） |
| `AnInFlightCancellationOfTheSecondDemandEndingUnknownKeepsTheScreenOnThatDemandAndShutsTheEntry`（hmi#164，改写） | — | 屏上仍是 B、入口关、再按动不到 A | 原判据「入口一直亮着」与本票相反，改写 |

`02`～`13` 是第一版用例的红绿（文件名里的编号与当时的用例对应）；返工后用例改了夹具，`14`～`19` 是返工后的证据。

## 本机自检（返工后，本机时段第 2 轮 10-13 21:25:54～21:38:42，C11 放行）

- `17-stability/`：上表 12 条，每轮 3 个进程同时跑、共 4 轮 = 12 次，全部 12/12。
- `18-green-family-485.txt`：`RecoveryVectorG2Tests`、`*LoadCancellation*`、`MultiDemandJourneyG2Tests`、
  `CV-LOAD-CANCELLATION-ALL-EMPTY`、`StationDeadlineExpiredG2Tests`、G2 `ArchitectureTests`，485/485，退出码 0。
- `19-green-unittests-1158.txt`：`SQCD.Agv.UnitTests` 整个项目 1158/1158，退出码 0（经 `Invoke-HeavyLocal.ps1`）。
- `20-format-verify.txt`：改动文件 `dotnet format --verify-no-changes` 退出码 0（无输出）。
- 车载端全量 G2 走 CI。

## 提交历史说明（审查 M4）

提交 `f058075` 的说明写着「revert: 撤回重启后读回取消结论的还原代码 / This reverts commit 8c1fcb7」，**这句话不实**：当时的
`git revert` 没有成功，紧接着的 `git commit --amend` 改写的是上一个提交（重启用例「先等入口关上再持续断言」的测试改动），于是
`f058075` 实际只改了测试，8c1fcb7 加的读回代码一直在 `WireToGateBusinessService.cs` 的还原流程里。这段代码保留：审查 H3 的修法
（重启用例等读回之后那条技术日志）正靠它。不改写历史。

## 剩余风险

- **B 没有结果时，车在会话中途进不了 RECOVERY_REQUIRED**，恢复入口要等下一次握手；服务端缺口在 control-server#697，修完后
  两边对一次文案与出路。
- **重启后的毫秒窗口**：会话就绪到还原流程读回标记之间，取消与修复续行入口可能短暂亮起；按下会被按键路径读发件箱拦下并应答，
  不发申请、不开门。onboard-hmi#254 的修正入口有同样的窗口。
- `ForgetSettledVector` 里「向量一扇门都没动过」（不留被接管标记）那一支没有本票用例：在途取消的前检拒绝必有交接过来的门或
  已清空的仓，本夹具造不出。
- 服务端发来的 `SlotOperationResumeCommand` 没加新的车载端拒绝：车载端不再提交修复续行，没有会话与动作 id 在档，现有安全门禁
  （`RecoverySessionAuthorized`）就会拒绝。
