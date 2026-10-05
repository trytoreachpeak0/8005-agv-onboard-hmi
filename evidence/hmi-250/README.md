# hmi#250 证据：迟到的 DurableAck 不再断开会话

基线 `w2g/fp-v2-impl` `0d857753`。全部为本机单类 `--filter` 运行，无并行负载。

## 缺陷是什么

车载端发出一条可靠消息后等服务端的 `DurableAck`。等待结束（超时，或者发出它的执行器被中止、取消了这次等待）时，等待者会被撤掉。此后再到达的 ack 在接收循环里找不到主人，一路落到末尾，被当成未处理的消息，整代会话随之断开：

```
接收循环失败收尾：generation=1，InvalidDataException：收到未处理的WIRE_TO_GATE消息：DurableAck。
```

在 `StationDeadlineExpiredG2Tests` 的夹具里，会话断开后不会重连，所以后面的 `OperationResult` 永远送不到服务端，用例在 10 秒处超时。batch-p3 上 `TheSameDeclarationAgainIsAnsweredWithTheFirstAnswerAndNothingIsStoppedTwice` 的那次红，推断就是这个原因：判故障中止执行器时，恰好打断了一次正在等 ack 的重新提示。那次失败的原文（TRX）已经随 hmi#219 的 worktree 删掉，取不到，所以这一条是推断，不是实读。

## 修复前的红（`red-before-fix.txt`）

这时只加了用例和替身服务端的 `OperationProgressAckDelay` 开关，产品代码没动。两条主用例都失败，原因正是上面那句日志：

- `AnAckArrivingAfterItsSendTimedOutIsTakenAndTheSessionGoesOn`：ack 比 `MessageTimeout` 晚 1 秒到。
- `AnAckArrivingAfterALoadCancellationAbortedItsSendIsTakenAndTheSessionGoesOn`：ack 只晚 1 秒，没有超过 2 秒的超时，所以等待者被撤掉只可能是因为装货取消中止了执行器。

## 修复后

`StationDeadlineExpiredG2Tests` 全类 58/58 通过（基线 50 条，加新增 8 条）。UnitTests 中的 `ArchitectureTests` 98/98 通过。`dotnet format --verify-no-changes` 通过。

## 变异（`mutations.txt`）

| 变异 | 改了什么 | 被哪些用例杀掉 |
| --- | --- | --- |
| M1 | 去掉迟到 ack 分支，改回直接断开 | 两条主用例、早已确认、已放弃 |
| M2 | 去掉内容哈希核对 | 对不上 `content` |
| M3 | 不看「已放弃」，直接记为已确认 | 已放弃 |
| M4 | 去掉消息类型核对 | 对不上 `type` |
| M5 | 去掉 `correlationId` 与 `acceptedMessageId` 必须相同的核对 | 对不上 `correlation` |

`mutations.txt` 中第一段 M3 作废：它写成 `if (false)`，编译时被当成不可达代码报错（`M3 build 1 Error(s)`），测试实际跑的是上一次构建留下的 M2 程序。后一段 M3 改用能编译通过的写法重做，结果有效。

M5 是审查开始后按调度要求补的。第一版提交里这条核对没有用例覆盖：替身推送的 ack 这两个字段总是一致，删掉核对也不会有用例变红。现在替身可以推送两者不一致的 ack，新加的 `correlation` 用例断言车载端照旧断开。
