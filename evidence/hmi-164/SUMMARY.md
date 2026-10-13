# hmi#164 证据：多需求下取消 B 的在途装货、清空报 UNKNOWN 时，恢复入口指向 B

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/164
基线：`w2g/fp-v2-impl@f487a16`。只改 `tests/`：新增
`tests/SQCD.Agv.WireToGateG2Tests/MultiDemandJourneyG2Tests.CancellationUnknownRecoveryEntry.cs`，产品代码一行未动。

## 票面核对（开工前，在 f487a16 上实读）

- 票面列的 8 条用例都还在；`ALostCancellationAuthorization…`、`ACancellationPressedWhileTheSessionIsNotReady…` 已搬到
  `StationDeadlineExpiredG2Tests.CancellationFirstPress.cs`，`AResendDuringAnInFlightCancellation…` 在
  `StationDeadlineExpiredG2Tests.TakenOverReplayRace.cs:107`。
- 该向量现有 G2 用例 24 条（方法数；其中一条是两组参数的 Theory，所以按用例计是 25 个），不是 8 条。本票加 1 条。
  验收按调度裁定不写死条数。
- 最接近的三条都不覆盖本缺口：
  - `MultiDemandJourneyG2Tests.HandedOverDoorRefusal` `ACancellationRefusedWhileTheHandedOverDoorStandsOpenReportsThatDoorInTheHandshake`：
    取消以 UNKNOWN 结束，但被取消的是 A（单需求），只断结果报文与重连握手，不断恢复入口。
  - `MultiDemandJourneyG2Tests.CancellationOverRecordedLoad` `ARefusedCancellationEndedAfterTheManualCheckKeepsTheLoadsAttemptAcrossAReconnect`：
    取消以 UNKNOWN 结束、数了 `OPERATION_RECOVERY_REQUIRED`，对象是 A，不断入口指向谁。
  - `MultiDemandJourneyG2Tests.LateLoadCancellationPress` `WithAnInFlightCancellationOnFileTheConfirmationNamesItsDemandNotTheSelectedRow`：
    断「A 的取消向量在档、还在等清空」时确认框复述 A；不是 UNKNOWN 结束，也不是第二条需求。
- 票面给的接缝（`RecoveryVectorG2Tests`／`StationDeadlineExpiredG2Tests` 一族）不建 `MainViewModel`，断不到视图层；
  `MultiDemandJourneyG2Tests` 的夹具已有全部控制面（两条需求、在途取消授权、`SetUnreadable`、视图模型），不用加夹具。
  调度 10-13 同意改放这一族。

## 新用例

`MultiDemandJourneyG2Tests.AnInFlightCancellationOfTheSecondDemandEndingUnknownLeavesTheRecoveryEntryOnThatDemand`
（`IntegrationSlice=FP-IS-02`，`ProtocolVector=CV-LOAD-CANCELLATION-ALL-EMPTY`）。

构造：A 在 5 号仓装好关门、结算；B 在 1 号仓开门，操作员不动；清单选中行放在 A；经视图模型按「取消装货」。替身在授权前把
1 号仓锁反馈设成读不到，取消向量的前检拒绝这扇交接过来的门，`LoadCancellationResult` 报 `UNKNOWN`（demandId=B）。

断三处：

- (a) 视图层，等到后再持续 500 ms：`ViewModel.CanRequestLoadCancellation`、`CanPressLoadCancellation` 为真；屏幕快照里
  `OnLoadCancellationClick` 可见可按；确认文案以「将要取消的任务：子批 SUBLOT-B，」开头、不含 `SUBLOT-A`；选中行仍是 A。
- (b) 最后一条带 operation 的已发布事件（屏幕学习当前操作的唯一输入）：`AttemptB`、`RecoveryRequired`。
- (c) 经按键路径再按一次：`LoadCancellationStartRequested` 仍只有 B 那一条；所有 `LoadCancellationResult` 都是 B；
  日志簿里的向量仍是 B；A 的完成记录没动；`UnlockCount` 仍为 2（没开任何门）。返回值与屏上反馈不钉，原因见下面「疑点」。

## 反向验证（本机时段 10-13 18:40:58～18:46:22，看板持有者 hmi#164）

每次只改 `src/SQCD.Agv.Wpf/WireToGateBusinessService.RecoveryVectors.cs` 一处，改前新用例已提交（`9955765`），跑
`dotnet test --filter "ProtocolVector=CV-LOAD-CANCELLATION-ALL-EMPTY"`（26 个用例），跑完 `git checkout --` 还原，
`git status` 干净。

| 注入 | 改法（基线行号） | 新用例 | 现有 25 个用例 | 证据 |
| --- | --- | --- | --- | --- |
| I1 指向 A | `LoadCancellationSubject` 向量分支 `vector.DemandId` → `selectedDemandId ?? vector.DemandId`（:647） | 红 | 24 绿，`WithAnInFlightCancellationOnFileTheConfirmationNamesItsDemandNotTheSelectedRow` 红 | `05-reverse-i1.txt` |
| I2 指向空 | 同一处向量分支条件改为 `false`，不给对象（:644） | 红 | 24 绿，同上一条红 | `06-reverse-i2.txt` |
| I3 屏上操作不是 B | `PublishRecoveryVectorOperation` 的操作号 `context.SlotOperationAttemptId ?? context.PrimaryId` → `context.PrimaryId`（:4657） | 红 | 25 绿 | `07-reverse-i3.txt` |

新用例三次都红在同一个等待上：

```
Timed out after 5s waiting for: the recovery entry to be up over B, the cancelled demand. UI errors: []
```

I1、I2 下另一条红的现有用例断的是同一个确认文案（向量在档时复述向量的需求），与本票判据同源，不是回归（调度已知）。
还原后（`08-reverse-restored.txt`）：26 个用例全绿，退出码 0。

还原之后对新用例做过一处放宽：第一次按键原有 `Assert.False(...)`，改成只等按键完成、不断返回值。它不是判据：第一次按下的
结果由「服务端收到 B 的 UNKNOWN 报文」那几条断言证明，三次注入都红在其后的等待上。改后单跑新用例绿。

## 本机自检

- `02-architecture-unittests.txt`：`SQCD.Agv.UnitTests` 的 `ArchitectureTests`（含 `IntegrationSliceTraitArchitectureTests`）203/203，退出码 0。
- `03-architecture-g2.txt`：G2 项目 `ArchitectureTests` 12/12，退出码 0。
- `04-multidemand-family-green.txt`：`MultiDemandJourneyG2Tests` 一族 141/141，退出码 0。
- `dotnet format --verify-no-changes`：退出码 0。
- 车载端全量 G2 走 CI。

## 疑点（范围外，已开票 trytoreachpeak0/8005-agv-onboard-hmi#343）

`01-repress-diagnostic.txt`：UNKNOWN 之后，在途取消入口一直可按。按下去车上对 B 的向量重跑一次，但结果键与第一次相同，
发件箱直接拿已确认的 UNKNOWN 收尾，不发新结果（技术日志原文「恢复向量结果的DurableAck不在首次发送时到达，按发件箱已确认的
结果收尾：type=LOAD_CANCELLATION，…，outcome=UNKNOWN」），向量一直留着，按键返回 false；这一次的提示去重键与第一次相同，
被去重器吞掉，`Events` 一条不增，屏上没有任何反馈。这份诊断是在清空并关门之后按的（诊断版本按前调用了
`io.CloseDoor(0, cargo: false)`），仍然结不了案。是设计还是缺陷由新票判断，本票用例不钉这两点。
