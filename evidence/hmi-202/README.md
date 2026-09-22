# onboard-hmi#202 修前／修后／变异证据

**要修的事**：`RecoveryVectorG2Tests.WritingTheResultObservationNeverDropsAPendingResultRecordedBeforeIt` 偶发红在
`WriteFunnelRace.cs:167`（`Assert.NotNull(persisted.RecoveryResultObservedAt)`）。用例等服务端收到
`FaultCargoRecoveryResult` 后才读 journal，而假服务端收到即回 `DurableAck`，车载端收到 ack 后结算，把
`RecoveryResultObservedAt` 清成空。读落在结算之后就红。只改测试，不改产品代码。

## 机理实读核对（票面是二手事实）

- 等待：`RecoveryVectorG2Tests.cs:1763` 的 `WaitForResultAsync` 只调 `WaitForInboundAsync`，服务端收到即返回。属实。
- 票面说的结算路径（`RecoveryVectors.cs:2055` → `ForgetSettledRecoveryVectorAsync` `:2483` → `ForgetSettledVector` `:2519`，
  `:2526` 清空）行号都对得上，但**那是失败结果（`releaseSettledFailure`）才走的路**。这条用例目标仓位留空，执行器走
  `context.Slots.Count == 0` 分支返回 `COMPLETED`，`ExecuteRecoveryVectorAndReportAsync` 在 ack 之后走的是
  `CompleteRecoveryVectorStateAsync` → `SettleRecoveryVectorStateAsync`（`RecoveryVectors.cs:2894` 起），同样在锁内把
  `RecoveryResultObservedAt = null`、`RecoveryVector = null`，`PendingResults` 不动。
- 结论：失败形状与票面一致，机理成立；清空观测时间的是成功路径的结算函数，不是票面点名的那个。

## 修法

包装日志 `WriteFunnelRaceJournal` 在目标写入（`EnsureResultObservedAtAsync` 那一次）返回、控制权交回产品之前，从
journal 读一次状态，存为 `StateAfterTargetWrite`。产品这条路径是顺序的：观测时间写入 → 执行器返回 → 发结果 → 收 ack → 结算，
所以这一刻结算按构造还没发生，不靠计时。用例对这份状态断「待发结果还在」与「观测时间已写入」；结果发出后的最终状态只断待发结果。

## 文件

| 文件 | 内容 |
| --- | --- |
| `before/summary.txt` | 修前：`7cf1dcba` 构建，同一条命令背靠背 30 次，**9 次失败** |
| `before/all-failures.txt` | 9 次失败逐条：全部是 `Assert.NotNull() Failure: Value of type 'Nullable<DateTimeOffset>' does not have a value`，全部在 `WriteFunnelRace.cs:line 167` |
| `before/run-01-failure.txt` | 第 1 次失败的完整输出 |
| `after/summary.txt` | 修后：`61ec73d` 构建（dll 时间与 SHA-256 在第一行），30 次，**0 次失败** |
| `mutation/mutation.diff` | 变异：`EnsureResultObservedAtAsync` 不再把观测时间写进 journal（仍返回一个时间，所以结果照常发出、向量照常完成） |
| `mutation/run.txt` | 变异后在修后用例上跑 1 次：红在 `WriteFunnelRace.cs:line 190`，即新的 `Assert.NotNull(justWritten.RecoveryResultObservedAt)`。按字节备份还原后重建，复跑绿 |
| `tools/` | 连跑脚本与变异脚本（替换须恰好命中一处，否则退出） |

命令：`dotnet test tests/SQCD.Agv.WireToGateG2Tests --no-build --filter FullyQualifiedName~WritingTheResultObservationNeverDropsAPendingResultRecordedBeforeIt`，
经 `Invoke-HeavyLocal.ps1 -Ticket hmi#202` 运行。

变异为什么是确定性的红：变异后目标写入之后 journal 里观测时间必然为空，新判据读的就是这一刻，不存在「读晚了才红」的窗口。
反过来，修前判据在变异下同样会红，但它在未变异时也会红（结算之后按设计为空），所以它分不清两者——这正是要改的原因。
