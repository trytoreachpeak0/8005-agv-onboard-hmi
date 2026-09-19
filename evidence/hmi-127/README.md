# hmi#127 证据

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/127（control-server#189 第二步）

## G2 与单元测试（先红后绿）

每条新测试先单独提交、在该提交上跑红，再提交实现、跑绿。`red/0N-*.txt` 是各自测试提交上的失败原文；
`red/00-*` 是全部新测试一起放到开工基线 `172077a`（w2g/fp-v2-impl，含 hmi#124、hmi#123）上的一次运行。

| 红 | 测试 | 红在 |
| --- | --- | --- |
| `red/01-inflight-result-recovery-required.txt` | `ALoadThatEndsWhileTheSessionAwaitsItsResultSendsTheResultAndTheSessionComesBackReady` | 重连后会话 `RecoveryRequired`，结果没有发出（`RESULT_ACK_PENDING`） |
| `red/02-disconnected-result-on-file.txt` | `ALoadThatEndedWhileDisconnectedPutsItsResultOnFileAndTheHandshakeReplaysIt`（hmi#124 守护测试改写） | 断开时结束的装货，发件箱没有结果行 |
| `red/04-original-command-rejection.txt` | `ASlotOperationCommandRefusedWhileRecoveryRequiredStillGetsItsRejectionOut` | 对原始命令的拒绝发不出去 |
| `red/06-mid-session-ack-lost-resend.txt` | `AResultWhoseAckIsLostMidSessionIsSentOnceMoreAndSettles` | 会话中途确认丢失、链路不断时停在「结果等待确认」 |
| `red/07-record-race-next-operation.txt` | `RecordingALateResultNeverClearsTheNextOperationThatStartedInBetween`（单元测试） | 补记把读写之间写下的下一单 journal 清空 |
| `red/08-record-conflict-warning.txt` | `ALateAckForAnAttemptTheJournalHasMovedPastIsLoggedForWhatItIs` | 只记「恢复未完成仓位操作的界面投影失败」 |
| `red/09-rejection-message-id-collision.txt` | `AnAttemptRefusedWhileRecoveryRequiredStillGetsItsResultOutWhenReissued` | 拒绝占用了结果的 `messageId`，再下发后门开了、结果存不进发件箱（独立审查发现） |
| `red/10-unfinished-result-resend.txt` | `AnUnfinishedResultWhoseAckIsLostIsSentOnceMore` | 失败／未知结果确认丢失后不重发（独立审查发现） |

`red/00-all-new-g2-tests-on-172077a.txt` 在第 9、10 条加入之前取得：当时 9 条中 5 红 4 绿，4 条绿的是守护测试
（`AReconciledResultIsNeither…`、`ProgressIsStillNotSent…`）与为本票调整过的两条 hmi#124 测试。其中第 1 条在基线上红的方式
与真装置不同：G2 里安全态变化确认之后客户端重新发布一次会话状态，恢复判断按实时 IO 做了中断结算，于是红在「不应走中断
结算」这一断言上；真装置上两事件先后相反，所以一直卡着（cs#189 `-002`）。两者都是同一缺陷。

绿：`green/00-all-new-g2-tests-d21b3e8.txt`、`green/00-record-race-unit-test-d21b3e8.txt`；全量门禁
`green/onboard-hmi-g2-d21b3e8/`（`run-w2g-g2.ps1 -SkipProtocolG1`，PASS），布局检查 `green/ui-layout-d21b3e8.json`（PASS）。

## 真装置 L2

服务端 `905ffd1d`（fp/v2-impl，detached worktree），模拟器 `fb5f7c59`，对端都经 `-OnboardRepository`／`-SimulatorRepository`
显式传入干净 worktree。

| 目录 | 车载端 | 结论 |
| --- | --- | --- |
| `green/rig-inflight-load-reconnect-d21b3e8-001/` | `d21b3e80` | PASS：`L2-IR-02` 在第 2 代次内送达、Ready、Committed；`L2-IR-05` 只有一条结果 |
| `green/rig-compensate-then-reconnect-d21b3e8-001/` | `d21b3e80` | PASS |
| `green/rig-compensate-then-reconnect-172077a-001/` | `172077a7`（前一个合并提交，规格 21.2 第 7 条） | PASS |

`real-onboard-inflight-load-reconnect` 是 cs#189 的一次性场景，副本与修改存在
`green/rig-inflight-load-reconnect-d21b3e8-001/scenario/`，改了什么写在脚本头注释里：修 `-002` 报告的两处读数
（`Get-Results` 被 `@()` 多包一层；`L2-IR-04` 读阶段太早），`L2-IR-02` 要求结果在第一次重连后的代次内送达，`L2-IR-03`
由「进度仍送达」的观察改为「RecoveryRequired 期间核对／收尾进度不发」的守护。

红证据不重取，引用 control-server 仓 `fp/v2-impl`：
`evidence/l2/20260919-cs189-inflight-reconnect-4a6790e-002/`（`L2-IR-02` FAIL：第 2 代次约 52 秒没有 `OperationResult`）与
`evidence/l2/20260919-cs167-debug-001/`（原始发现）。
