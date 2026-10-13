# hmi#343 证据：在途取消报 UNKNOWN 后取消向量残留

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/343
基线：`w2g/fp-v2-impl@6a0b210`（含 hmi#164、hmi#337）。

## 第 1 步：三条出路实测

见 `01-README.md` 与 `01-probe-*.txt`。补偿、故障货物交接、强制机械恢复全被 `RECOVERY_VECTOR_CONFLICT` 挡住；
修复续行能走但把已取消的装货重跑、且收不了尾。Coordinator11 据此裁定收归切换线、进生产前修。

## 修法

1. **向量**：操作员发起的在途取消，结果一经确认为非 COMPLETED（含发送时就拿到确认、和重连后按发件箱收尾两条路），
   与补偿、交接一样走 `ForgetSettledVector` 忘掉向量。`ActiveUnlockSlots`（交接过来时还开着的门）与
   `TakenOverSlotOperationAttemptId`（被接管的尝试）都留着——「可能已开过门」不丢；之后补偿等待这扇门而不是再开它。
2. **入口**：同时把该尝试记为「取消已有结论」，在途取消入口按 onboard-hmi#254 已有的规则关闭。
3. **反馈**：入口关后仍到达业务层的按键走 `LOAD_CANCELLATION_ALREADY_CONCLUDED`，经 `PublishOperatorResponse`（不去重）应答
   「这次装货已经取消过、取消已有结论，不能再取消；需要处理货物请联系维护人员走恢复流程。」确认框跨过收尾才确认的按键
   原先回「对象已经变了，请重新按取消装货」（入口已关，误导），现在同样按取消已有结论作答。
4. **修复续行**：选「不重跑」。取消已有结论后「申请恢复」入口关闭，按下只回应答「…不能再恢复原装货。需要处理货物请改用
   「补偿清空」或「强制机械恢复」，或联系维护人员。」理由：续作重开仓门、要人把操作员取消掉的货再装一遍，与意图相反；
   被取消接管的装货，结论归那次取消（ADR-cross-0046）；补偿与强制机械恢复已经走得通。

恢复状态只经 `IWireToGateJournal.UpdateRecoveryStateAsync`（`ForgetSettledRecoveryVectorAsync`）写；没有新写盘的持久消息。

## 用例与红绿

全部在 `MultiDemandJourneyG2Tests.CancellationUnknownExits.cs`（另改写 hmi#164 的那一条）。「修前」指把 `src/` 临时换回
`5b86dca`（只有测试、产品代码与基线相同）跑，跑完 `git checkout HEAD -- src`，`git status` 干净。

| 用例 | 钉住什么 | 红 | 绿 |
| --- | --- | --- | --- |
| `AnAcknowledgedUnknownCancellationShutsTheEntryAndKeepsTheDoorInDoubt` | 确认后入口关、门的疑点与被接管尝试留着 | `02`（测试提交 `5b86dca` 上） | `09` |
| `AnInFlightCancellationPressedAfterItsUnknownIsAcknowledgedIsAnswered` | 再按每次都有应答，不发不开 | `03`（修前，红在 `Events` 为空） | `09` |
| `ACompensationTakesOverAnAcknowledgedUnknownCancellationAndSettlesIt` | 屏上按「补偿清空」走通、ALL_EMPTY、日志簿清干净（应答叫人走的维护出路） | `04`（修前，补偿入口不出现） | `09` |
| `AForcedMechanicalRecoveryTakesOverAnAcknowledgedUnknownCancellationAndKeepsItsDoor` | 强制机械恢复走通，1 号仓进隔离记录 | `05`（修前，入口不出现） | `09` |
| `AResumeAfterRepairDoesNotRerunALoadWhoseCancellationIsConcluded` | 「申请恢复」关、按下只应答、不开会话不开门 | `06`（测试提交 `e92bee2` 上）、`08`（修前） | `09` |
| `AcrossARestartAnAcknowledgedUnknownCancellationStaysShutAndCompensable` | 重启后入口仍关、补偿仍可用、门的疑点仍在 | `08`（修前，向量不离开日志簿） | `09` |
| `AConfirmationLeftOpenAcrossTheReplayedAcknowledgementIsAnswered` | 确认丢失→重连补发→收尾；跨过收尾的确认框按下得到「取消已有结论」 | `07`（测试提交 `bd9bf9d` 上，回的是 `LOAD_CANCELLATION_NOTHING_TO_CANCEL`）、`08`（修前） | `09` |
| `AnAcknowledgedFailedCancellationShutsTheEntriesAndIsCompensable` | 结果 FAILED（授权后锁存，1 号仓 NOT_STARTED）同样关入口、复位锁存后补偿走通 | `12`（修前，本机时段 10-13 20:41:03～20:42:09，C11 放行） | `13` |
| `AnInFlightCancellationOfTheSecondDemandEndingUnknownKeepsTheScreenOnThatDemandAndShutsTheEntry`（hmi#164，改写） | 屏上仍是 B、入口关、再按动不到 A | —（原判据「入口一直亮着」与本票相反，改写） | `09` |

假服务端新增 `HoldOperationOnUnreconciledLoadCancellationResult`（默认关）：照真服务端 `KeepDemandAndJourneyBlockedAsync`
把对不上的取消结果置为需要恢复。不设时「需要恢复」只偶尔被别的报文带出来，第 1 步探测里等它时红时绿。

## 本机自检

- `09-green-family-481.txt`：`RecoveryVectorG2Tests`、`*LoadCancellation*`、`MultiDemandJourneyG2Tests`、
  `CV-LOAD-CANCELLATION-ALL-EMPTY`、`StationDeadlineExpiredG2Tests`、G2 `ArchitectureTests`，481/481，退出码 0。
- 本票 8 条连跑 3 轮全绿；后补的 FAILED 用例连跑 3 轮全绿，`13-green-multidemand-149.txt` 为补完后 `MultiDemandJourneyG2Tests` 一族 149/149。
- `10-green-unittests-286.txt`：`SQCD.Agv.UnitTests` 的 `ArchitectureTests`（含切片标记）与向量相关单元测试 286/286，退出码 0。
- `11-format-verify.txt`：改动文件 `dotnet format --verify-no-changes` 退出码 0（无输出）。
- 车载端全量 G2 走 CI。

## 剩余风险

- **重启后的毫秒窗口**：入口读进程内标记，重启后要等会话就绪、中断装货收尾读完发件箱才写回。其间取消与修复续行入口可能
  短暂亮起；按下会被按键路径读发件箱拦下并应答，不发申请、不开门。onboard-hmi#254 的修正入口有同样的窗口。
- 服务端发来的 `SlotOperationResumeCommand` 没加新的车载端拒绝：车载端不再提交修复续行，没有会话与动作 id 在档，现有安全门禁
  （`RecoverySessionAuthorized`）就会拒绝。
