# onboard-hmi#119 红绿证据

续行命令（`SlotOperationResumeCommand`）在任何仓门 IO 之前被车挡住时，车回 `SlotOperationCommandRejected`。

每个文件开头记着跑它时的 `commit`（测试所在的提交）、`note`（源码若另从别的提交检出，写在这里）与命令，后面是 `dotnet test` 的原始输出。
测试都在 `tests/SQCD.Agv.WireToGateG2Tests/RecoveryVectorG2Tests.ResumeRejected.cs`。

## red/

| 文件 | 测试 | 源码 | 红在哪里 |
| --- | --- | --- | --- |
| `00-all-new-tests-on-3547a97.txt` | 全部 8 条 | `3547a97`（开工时的 `w2g/fp-v2-impl`） | 7 条红在服务端收不到拒绝；第 7 条是护栏，在基线上本来就绿 |
| `01-state-mismatch.txt` | `AResumeWhosePersistedStateDoesNotMatchIsRejectedBeforeAnyDoorIo` | 测试提交 `6987ffa` | 门禁已本地阻断，服务端收不到拒绝 |
| `02-context-missing.txt` | `AResumeWhoseOperationContextIsGoneIsRejectedBeforeAnyDoorIo` | `3547a97` | 同上 |
| `03-executor-pre-io.txt` | `AResumeTheExecutorRefusesBeforeItsFirstPulseIsRejected` | 测试提交 `951cbdd` | 执行器开锁前拒绝，只写日志不回拒绝 |
| `04-resend-same-rejection.txt` | `AResentResumeIsAnsweredWithTheSameRejection` | 测试提交 `b54ec98`，其源码即 `115f93c`（重放实现之前） | 重发时车不再发那条未确认的拒绝 |
| `05-refused-stays-refused.txt` | `AResumeRefusedOnceIsNotRunOnAResendTheGateWouldNowAllow` | `115f93c`（重放实现之前） | 拒绝过的续行重发时开了 1 次锁（`Expected: 0, Actual: 1`） |
| `06-keys-apart-baseline.txt` | `TheOriginalCommandsRejectionAndTheResumesRejectionDoNotShareAKey` | `3547a97` | 续行的拒绝没有发出 |
| `06-keys-apart-mutant-shared-key.txt` | 同上 | 当前实现，但续行拒绝的键改成原始拒绝的格式 `slot-operation-rejected:{attempt}:{reason}` | 撞键：续行被当成「已拒绝过」，自己的拒绝没发出 |
| `08-restart-no-extra-baseline.txt` | `ARestartSendsNoFurtherRejectionForAResumeAlreadyRejected` | `3547a97` | 服务端收不到拒绝 |
| `10-new-session-after-close.txt` | `AfterARejectedResumeClosesItsSessionTheVehicleCanOpenAnother`（第 9 条，真装置配对验证 P-07 发现后补） | 测试提交（清会话记录之前） | 按「补偿清空」本地报 `RECOVERY_SESSION_STATE_PENDING` |
| `11-restart-test-ack-race.txt` | 三个 G2 类一起跑时第 8 条偶发红 | 清会话记录的实现提交 | 测试自身的时序：重启赶在车记下 DurableAck 之前，握手补发了同一 messageId 的拒绝；最终改为重启前等车把 ack 记进日志、原断言不变 |
| `08-restart-no-extra-baseline-final.txt` | 第 8 条最终版 | `3547a97` | 服务端收不到拒绝 |

## green/

与上表同名的文件是同一条测试在对应实现提交上的通过记录；`00-all-new-tests.txt` 是最初 8 条一起跑；`09-after-merge-7ded1b7-g2-classes.txt` 是合并 hmi#120 之后；`11-g2-classes-after-release-fix.txt` 是补第 9 条修复后 `RecoveryVectorG2Tests`、`StationDeadlineExpiredG2Tests`、`WireToGateG2Tests` 三类 106 条（第 8 条中间版断言）；`12-g2-classes-final.txt` 是第 8 条恢复原断言后的同三类 106 条。

`rig-4e5cfcd/`：`4e5cfcd` 上的真装置时段证据，见该目录 `README.md`。

`07-after-pulse-settled-baseline.txt` 与 `07-after-pulse-settled.txt`：第 7 条
`AResumeThatFailsAfterItsFirstPulseIsSettledNotRejected` 是回归护栏，防的是今后把开锁之后的失败也当成拒绝。
基线从不发这条拒绝，所以它在基线上也是绿的，不可能先红；两份记录都放在这里说明这一点。
