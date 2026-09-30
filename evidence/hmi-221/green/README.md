# hmi#221 本机全量 G2

命令（从本票 worktree 跑，协议仓用 `scratch/` 下 `protocol-v2.0.0` 的普通克隆）：

```text
pwsh -File C:/Users/szy/Desktop/8005-workspace-v2/Invoke-HeavyLocal.ps1 -Ticket hmi#221 pwsh -File scripts/run-w2g-g2.ps1 -ProtocolRoot C:/Users/szy/Desktop/8005-workspace-v2/scratch/hmi221-protocol-v2.0.0
```

## 通过的那一次（本目录存的就是它）

- 提交 `e38cb25892e910ec2cce73b408de20ee8f0bd662`，证据目录 `evidence/g2/protocol-v2.0.0/20260930T115650389Z-e38cb25892e9`（被 gitignore，本目录是它的摘录）
- `Status: PASS`
- `SQCD.Agv.UnitTests`：`Passed! - Failed: 0, Passed: 636, Skipped: 0, Total: 636`
- `SQCD.Agv.WireToGateG2Tests`：`Passed! - Failed: 0, Passed: 500, Skipped: 0, Total: 500`
- 出站 schema 校验：10707 行，0 违约，1 条登记在案的故意违约（别的用例的）。本票两条消息：
  `ManualStationClearanceConfirmationRequested` 产品发出 22 行，`ManualStationClearanceConfirmationResult` 假服务端发出 23 行，全部通过
- `dotnet format --verify-no-changes` 通过（`dotnet-format-verify.log` 为空）

## 之前三次没有通过，照实记下

一共跑了四次，前三次整轮 `FAIL`，每次红的用例都不同。

| 次 | 提交 | 红的用例 | 归属 |
| --- | --- | --- | --- |
| 1 | `d4c9644c` | `WireToGateG2Tests.MessagesBetweenTheActivationResultAndItsAckAreHandledAndTheSessionStaysUp`（期望 3 个快照确认、读到 2 个） | 既有用例，等的是仓位命令、断的是快照确认数。已报调度，并入 hmi#228 |
| 2 | `d4c9644c` | `ManualStationClearanceG2Tests.ADroppedSessionLeavesAnUnknownThatIsResubmittedUnderTheSameIdAfterReconnecting`（`ArgumentNullException: shown`） | **本票的用例，写法有问题**，见下 |
| 2 | `d4c9644c` | `MultiDemandJourneyG2Tests.ARefusalAtANewerRevisionWhileTheWorklistIsAnotherStationDoesNotKeepTheRequest`（操作记录里找不到那一句） | 既有用例，等的是业务服务的字段、读的是视图模型的操作记录。已报调度 |
| 3 | `e38cb258` | `StationDeadlineExpiredG2Tests.AResultRefusedAcrossAReconnectIsDeliveredOnTheNextSessionAndSettledOnce(moment: AfterReady)`（等 10 秒超时） | 既有用例，这一族在并行负载下不稳。已报调度 |

第 2 次里本票那一条是用例的写法，产品没有改，修在 `e38cb258`：

- 等待辅助方法原来是「等条件成立，再重新读一次视图模型」，两次读之间入口可能已经变了；改成把满足条件的那一次读数带回来。
- 重连的握手返回时新会话还在收尾，入口会短暂收起再出现；改成先等线上两个方向都安静，再取入口、再按。

修后本票整类单跑三遍 27/27，第 3、4 次全量里本票的用例都通过。

另外三条既有用例与本票的改动无关：本票在接收循环里只加了一个 `ManualStationClearanceConfirmationResult` 分支，排在旅程快照与
`SessionReadiness` 的处理之后；视图模型多读一次业务服务的清桩视图。三条的失败各有自己的机理（见上表），都是「等一样、读另一样」
或固定时限，当时本机空闲内存约 3.5 GiB（共 15.3 GiB）。
