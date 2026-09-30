# hmi#221 本机全量 G2

命令（从本票 worktree 跑，协议仓用 `scratch/` 下 `protocol-v2.0.0` 的普通克隆）：

```text
pwsh -File C:/Users/szy/Desktop/8005-workspace-v2/Invoke-HeavyLocal.ps1 -Ticket hmi#221 pwsh -File scripts/run-w2g-g2.ps1 -ProtocolRoot C:/Users/szy/Desktop/8005-workspace-v2/scratch/hmi221-protocol-v2.0.0
```

## 本目录存的这一次：审查修改之后，`290d4f0`，一次通过

- 提交 `290d4f02818c3bf8661eb770c8edba8cf004359e`，证据目录 `evidence/g2/protocol-v2.0.0/20260930T152754537Z-290d4f02818c`（被 gitignore，本目录是它的摘录）
- `Status: PASS`
- `SQCD.Agv.UnitTests`：`Passed! - Failed: 0, Passed: 636, Skipped: 0, Total: 636`
- `SQCD.Agv.WireToGateG2Tests`：`Passed! - Failed: 0, Passed: 508, Skipped: 0, Total: 508`
- 出站 schema 校验：11186 行，0 违约；登记在案的故意违约 3 行（一行是别的用例的，两行是本票两条故意发畸形应答的用例）。
  本票两条消息：`ManualStationClearanceConfirmationRequested` 产品发出 34 行、`ManualStationClearanceConfirmationResult`
  假服务端发出 30 行
- `dotnet format --verify-no-changes` 通过（`dotnet-format-verify.log` 为空）

## 审查之前跑过五次里的前四次，照实记下

审查之前在 `d4c9644`／`e38cb258` 上一共跑了四次，前三次整轮 `FAIL`，第四次 `PASS`（单元 636、G2 500）。

| 次 | 提交 | 红的用例 | 归属 |
| --- | --- | --- | --- |
| 1 | `d4c9644c` | `WireToGateG2Tests.MessagesBetweenTheActivationResultAndItsAckAreHandledAndTheSessionStaysUp`（期望 3 个快照确认、读到 2 个） | 既有用例，等的是仓位命令、断的是快照确认数。已报调度，并入 hmi#228 |
| 2 | `d4c9644c` | `ManualStationClearanceG2Tests.ADroppedSessionLeavesAnUnknownThatIsResubmittedUnderTheSameIdAfterReconnecting`（`ArgumentNullException: shown`） | **本票的用例**，见下 |
| 2 | `d4c9644c` | `MultiDemandJourneyG2Tests.ARefusalAtANewerRevisionWhileTheWorklistIsAnotherStationDoesNotKeepTheRequest`（操作记录里找不到那一句） | 既有用例，等的是业务服务的字段、读的是视图模型的操作记录。已报调度，并入 hmi#228 |
| 3 | `e38cb258` | `StationDeadlineExpiredG2Tests.AResultRefusedAcrossAReconnectIsDeliveredOnTheNextSessionAndSettledOnce(moment: AfterReady)`（等 10 秒超时） | 既有用例，只有现象、没读机理。已报调度，并入 hmi#228 |

### 本票那一条：第一次的修正没有修到根上

`e38cb258` 把它归因于「等完又读了一次视图模型」与「重连后会话还在收尾」，改了等待的写法。那两处确实该改，但**不是根因**：
第四次全量的通过带有运气成分，审查修改期间单跑时它又红了（12 次里 2 次），两条新写的重连用例每次都红。

根因是夹具的配置，产品无关：业务状态快照用的是假服务端内置的那一份，`observedAt` 每次发送现取。重连之后假服务端重发
修订号 1，内容却和第一次不同，车载端按协议以 `SNAPSHOT_REVISION_CONTENT_CONFLICT` 让新会话失败。重连后的步骤只有赶在
这之前跑完才绿。真实服务端重发一个修订号时内容不变。`290d4f0` 把两份快照都改成固定内容，重连的三条用例连跑十遍 3/3，
每遍 2 秒。

这个根因是读出来的：等待超时的信息里加上了视图模型、业务服务、会话状态、车载端日志与会话客户端诊断，诊断里直接写着
`接收循环失败收尾：generation=2，InvalidDataException：SNAPSHOT_REVISION_CONTENT_CONFLICT`。中间还推过一个错的解释
（假服务端不回 `SafetyStateChanged` 的确认），实读发现那个开关默认就是开的，作废。
