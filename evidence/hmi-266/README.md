# hmi#266 证据

判故障应答被服务端拒收并放弃之后，车载端用关联到判故障命令的 `ProtocolProblem(SLOT_OPERATION_CONFLICT)` 回拒这条命令。用例在 `tests/SQCD.Agv.WireToGateG2Tests/StationDeadlineExpiredG2Tests.AbandonedDeclarationAnswer.cs`。

所有运行都在本机，用 Debug 构建和定向 `--filter`，结果以退出码为准。

## 修复前红（`red-before-fix-767283a.txt`）

提交 `767283a` 只带四条用例，产品代码是基线 `69172308`。结果是 `Failed: 4, Passed: 0`，退出码 1。四条都在「等回拒」那一步超时。下面每条路径的异常栈都是从日志里实读的：

| 用例 | 路径 | 改前日志里的异常栈 |
| --- | --- | --- |
| `AReplayedDeclarationWhoseAnswerWasGivenUp…` | 一：会话就绪时重发 | `StoreDurableAsync:2010` ← `SendDurableCoreAsync:1755` ← `ResendSlotFaultDeclarationResultAsync:701`，落进兜底 |
| `AReplayHandledAfterTheConnectionWent…` | 二：会话未就绪 | `StoreDurableAsync:2010` ← `SendDurableCoreAsync:1741`（`rebind:false`）← `Resend…:701`，落进兜底 |
| `AnAcknowledgedAnswerGivenUpLater…` | 三：已确认分支不看放弃标记 | 不抛异常；放弃的那一行被第三次发上线 |
| `ALostRefusalIsGivenAgain…` | 回拒丢了再重连 | 红在第一次回拒；重连之后那一半由下面的 M1b 取红 |

同一份输出还记下了票面没写到的一点（裁定 S1）：应答第一次被拒收时，`SendDurableCoreAsync:1800` 抛出 `InvalidDataException(BUSINESS_ID_CONTENT_CONFLICT)`，同样落进兜底。

修复后新增的第五条 `AnAnswerRefusedOnItsFirstSend…` 覆盖首发被拒（S1）和当场回拒（S2）。它是在修复之后才加的，所以没有修复前的红；由 M3、M4 取红。

## 修复后绿（`green-after-fix.txt`）

- `StationDeadlineExpiredG2Tests` 整类加上 G2 架构测试：80/80，退出码 0。只看整类是 76 条，等于 PR #265 记录的 71 条加本票 5 条。
- UnitTests 的 `ArchitectureTests`：100/100，退出码 0。
- 本票 5 条连跑 5 轮，每轮 5/5，退出码 0。
- `dotnet format --verify-no-changes` 退出码 0。

## 变异（`mutations-1.txt`、`mutations-2.txt`、`mutation-*-where.txt`）

每个变异都整体重新编译（`--no-incremental`），只跑本票的 5 条用例；跑完用整文件重写的方式还原，不用 mv 或 cp。全部还原后重新编译，退出码 0。

| 变异 | 改了什么 | 结果 |
| --- | --- | --- |
| M1 | 每个 declarationId 只回拒一次 | 4 红；但第 4 条红在重连之前，因为当场回拒先用掉了这一次。不作为重连那一半的证据 |
| M1b | 每个命令 messageId 只回拒一次 | 3 红。第 4 条红在 `refusal 2`，即重连后同一 messageId 再重放那一步，这是重连那一半的证据。路径二那条也红：断线时回拒没发出去，却已经算作回过 |
| M2 | 去掉 B（重发应答时先查放弃标记） | 0 红，被 A 挡住 |
| M3 | 去掉当场回拒（S2） | 5 红 |
| M4 | 去掉 C 的接住（S1） | 4 红 |
| M5 | 去掉 A（重放时先查放弃标记） | 0 红，被 B 加 C 再加当场回拒挡住 |
| M2+M5 | A 和 B 都去掉 | 路径三那条红：`Expected: 2, Actual: 3`，放弃的那一行又被发上线 |

所以 A 和 B 是互为备份的两道防线，单独去掉任何一道都杀不死，两道都去掉才会红。两道都保留。

M1b 下第五条用例也红了，但那是用例自己的时序竞争：「已执行服务端的人工判故障」这条日志要等执行器停下才写，而用例只等到 `OperationResult` 就直接断言。已改为等待这条日志；修改后连跑 5 轮都是 5/5。
