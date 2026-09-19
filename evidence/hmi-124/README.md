# hmi#124 证据

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/124
PR：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/pull/126

三条新 G2 测试（`tests/SQCD.Agv.WireToGateG2Tests/StationDeadlineExpiredG2Tests.*`），各自先提交测试、在该提交上跑红，
再提交实现、跑绿。命令（VSTest 用 `--filter`）：

```
dotnet test tests/SQCD.Agv.WireToGateG2Tests -c Release --filter "FullyQualifiedName~<测试名>"
```

| 文件 | 提交 | 测试 | 结论 |
| --- | --- | --- | --- |
| `red/01-ack-late-not-unfinished.txt` | `a3970b4`（测试提交，产品代码 = `7ded1b7`） | `ACompletedLoadWhoseResultAckIsLateIsNotRestoredAsUnfinished` | 红：第一条 `SessionReadiness` 就发布「上次装货操作未完成：1号仓，需要管理员恢复。」 |
| `green/01-ack-late-not-unfinished.txt` | `5df631e` | 同上 | 绿 |
| `red/02-settled-once-acknowledged.txt` | `7a1fec8`（测试提交，产品代码 = `5df631e`） | `ACompletedLoadIsSettledOnceItsLateResultIsAcknowledged` | 红：重连补发被确认后 10 s 内 journal 仍未结算，并发布「上次装货操作未完成」 |
| `green/02-settled-once-acknowledged.txt` | `105aef3` | 同上 | 绿 |
| `red/03-restore-after-claim-release.txt` | `9062c57`（测试提交，产品代码 = `105aef3`） | `ALeftoverWhoseRestoreRanIntoTheReplayedCommandsClaimIsStillRestoredOnce` | 红：占位释放后 10 s 内服务端收不到 `OperationResult`，只有一条 `OPERATION_REPLAY` |
| `green/03-restore-after-claim-release.txt` | `15810bf` | 同上 | 绿 |
| `red/00-all-new-tests-on-7ded1b7.txt` | 测试取 `15810bf`，`src/` 取 `7ded1b7` | 三条新测试一起 | 3 红（票面要求的「在 `7ded1b7` 上跑」） |
| `red/05-resend-during-cancellation.txt` | `6a1afd2`（测试提交）（产品代码 = `55c0e9e`） | `AResendDuringAnInFlightCancellationLeavesTheOperationProjectionAlone` | 红：取消执行中重发原命令，补跑的恢复判断把快照改成「恢复向量 LOAD_CANCELLATION 尚未完成」（独立审查发现的新暴露面） |
| `green/05-resend-during-cancellation-and-class.txt` | `0f522ba` | 整个 `StationDeadlineExpiredG2Tests` 类 | 18/18 绿 |
| `green/04-new-tests-10x-15810bf.txt` | `15810bf` | 名字以 A 开头的 9 条（含三条新测试） | 连跑 10 遍全绿 |
| `green/06-not-ready-no-result-row.txt` | `7c0b01a` | `ALoadThatEndedWhileNotReadyHasNoResultToWaitForAndIsStillSettled`（守护测试，调度核对要求） | 绿：非 Ready 时结束的装货发件箱没有结果行，重连后照旧中断结算 |
| `red/06-not-ready-no-result-row-injected.txt` | `7c0b01a` 加注入故障（「没有结果行也判只差确认」，未提交，跑完已还原；文件里有注入的 diff） | 同上 | 红：`Timed out after 10s waiting for: the control server to receive OperationResult` |
| `green/07-class-after-injection-reverted.txt` | `7c0b01a` | 整个 `StationDeadlineExpiredG2Tests` 类 | 还原后全绿 |

第 3 条测试的接缝说明：握手后 `Ready` 触发的恢复判断被测试 journal 包装扣在读恢复状态那一步，服务端重发的同一命令
走到「已开始未结算」分支、持有占位时才放行，所以它确定地得到 `InFlight`，不靠时序碰运气。假服务端
`AnswerSafetyStateChanged=false` 挡掉车载端首条安全态变化的确认：那条确认会让客户端重新发布一次 `Ready`，也就是票里
说的「下一次会话状态变化」，它会替缺陷版本把遗留操作结算掉（第一次写这条测试时就在缺陷版本上假绿过一次，因此加了
这个开关和「读取确实被扣住」的断言）。
