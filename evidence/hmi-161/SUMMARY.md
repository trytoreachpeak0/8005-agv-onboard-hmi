# hmi#161 证据：正向判据补「屏幕收到了什么」一层

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/161
基线：`w2g/fp-v2-impl@91766e3`；改动提交：`61f3789`（只改 `tests/SQCD.Agv.WireToGateG2Tests/StationDeadlineExpiredG2Tests.AckPendingNotUnfinished.cs`）。

## 机理（在 91766e3 上核过）

`WireToGateBusinessService.PublishOperatorEvent`（`src/SQCD.Agv.Wpf/WireToGateBusinessService.cs:3959-3981`）先写
`_currentOperationSnapshot`，再问 `_operatorEventDeduplicator.ShouldPublish`，被去重就 `return`。屏幕只经
`App.xaml.cs:258` 订阅的 `OperatorEventPublished` → `MainViewModel.ApplyWireToGateOperatorEvent`（`MainViewModel.cs:631`）
学到当前操作。所以只断 `Business.CurrentOperationSnapshot` 的正向判据，在「那条事件被吞」时会假绿。

## 判定方法

对每条候选用例，读它实际发布的事件序列（`01-published-event-sequences.txt`，由一行未提交的临时诊断取得，已还原），
问：**有没有一条带 operation 的事件，被吞掉后会改变屏幕最终收到的操作，而现有等待与断言仍全绿？**
有才补断言；补的断言只读「最后一条带 operation 的已发布事件」，原样取，不在测试里复刻任何视图规则。

## 反向验证（唯一改动的那条）

注入：`WireToGateBusinessService.cs:3974` 临时改为
`if (!_operatorEventDeduplicator.ShouldPublish(deduplicationKey) || kind == "OPERATION_COMPLETED")`，即让结算事件走与
去重被吞完全相同的路径（快照照写、事件不抛）。注入只动这一个文件，跑完 `git checkout --` 还原，`git status` 干净。

注入后（`02-reverse-mutated.txt`）：

```
Timed out after 10s waiting for: the settlement to be published to the operator's screen as Completed
...
at ...StationDeadlineExpiredG2Tests.AckPendingNotUnfinished.cs:line 103
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1
```

第 103 行是新加的等待；它排在全部原有断言之后，所以原有断言（含 `Business.CurrentOperationSnapshot?.Stage == Completed`）
在注入下全部通过，红的只有新断言。还原后（`03-reverse-restored.txt`）：`Passed: 1`，退出码 0。

## 逐条结论

| 用例 | 结论 | 理由 |
| --- | --- | --- |
| `MultiDemandJourneyG2Tests.ALoneAttemptStillCarriesItsSnapshotAfterGivingTheDisplayUp` | 不改 | 判据本来就是被抛出的那条事件本身（等 `harness.Events` 出现它、再断它的 `Operation`）；被吞时等待超时变红。票面数的 biz=2 是前置等待条件 |
| `...AQueuedSlotCommandLeavesTheCurrentOperationAndTheTargetHighlightOnTheRunningOne` | 不改 | 已断视图层：`ViewModel.Lockers` 高亮（由 `_wireToGateOperation` 算出）与 `LogsSnapshot` |
| `...TheRunningDemandsUnknownResultIsShownBeforeTheQueuedCommandTakesOver` | 不改 | 同上，`LogsSnapshot` 顺序断言 |
| `StationDeadlineExpiredG2Tests.AnUnfinishedResultOnAReadySessionIsAnnouncedOnceAndNotAsLastTimesOperation` | 不改 | 最后一条带 operation 的是 `OPERATION_RECOVERY_REQUIRED`，已被等待并计数 |
| `...AnUnfinishedResultWhoseAckWasLostIsStillAnnouncedOnceByTheRestore` | 不改 | 同上 |
| `...ALeftoverWhoseSettlementResultCouldNotBeSentIsStillRestoredAsLastTimes` | 不改 | 同上，且断了措辞 |
| `...AResendRefusedWithAProtocolProblemStillRestoresTheRecoveryEntry` | 不改 | 等待的就是那条 `OPERATION_RECOVERY_REQUIRED` 行本身 |
| `...ACompletedLoadWhoseResultAckIsLateIsNotRestoredAsUnfinished` | 不改 | 负向（不出恢复投影）；最后一条带 operation 的 `RESULT_ACK_PENDING` 已被等待 |
| `...AResultWhoseAckIsLostMidSessionIsSentOnceMoreAndSettles` | **改** | 最后一条是 `OPERATION_COMPLETED`，原先只等 journal 结算、只断业务层；吞掉后屏幕停在 `RESULT_ACK_PENDING` 的 Reporting 而全绿。反向验证见上 |
| `...AnOperationLeftByThePreviousProcessIsStillRestoredAsRecoveryRequiredAndSettled` | 不改 | 两个进程各自最后一条都是 `OPERATION_RECOVERY_REQUIRED`，都被等待 |
| `...SessionReadinessDuringALiveLoadDoesNotRestoreItAsAnUnfinishedOperation` | 不改 | 负向；最后一条 `WaitingOperator` 已被 `WaitForStageAsync` 等待 |
| `...ALeftoverWhoseRestoreRanIntoTheReplayedCommandsClaimIsStillRestoredOnce` | 不改 | 最后一条 `OPERATION_RECOVERY_REQUIRED` 已被等待并计数 |
| `...AResendDuringAnInFlightCancellationLeavesTheOperationProjectionAlone` | 不改 | 负向：`Assert.Same(before, CurrentOperationSnapshot)`，业务层比屏幕更敏感；之后的取消完成是收尾，不是本用例判据 |
| `LoadCancellationBeforeSublotG2Tests`（3 处 `Assert.Null(CurrentOperationSnapshot)`） | 不改 | 负向：断「没写快照」，业务层更敏感 |
| `RecoveryVectorG2Tests.AnInterruptedOperationOnOtherSlotsIsStillSettledWhileAnIsolationStands` | 不改 | 两条带 operation 的事件（进度 `RecoveryRequired`、`OPERATION_RECOVERY_REQUIRED`）对屏幕效果相同，单吞任一条屏幕最终不变，新断言做不出先红后绿 |

## 剩余风险

- `RecoveryVectorG2Tests.AnInterruptedOperationOnOtherSlotsIsStillSettledWhileAnIsolationStands`：两条事件**同时**被吞时屏幕无恢复入口而用例仍绿。单条被吞不影响屏幕，本票按「单条」标准不加。
- 站点期限一族的夹具不建 `MainViewModel`，新断言读的是屏幕的输入（已发布事件），不是屏幕本身；`ApplyWireToGateOperatorEvent` 如何消费事件由别的测试负责。
