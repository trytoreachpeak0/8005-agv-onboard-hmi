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

## 审查后补充（PR #268 审查）

审查要求把 A 和 B 各自钉住：

- A 由用例 2（`AReplayedDeclarationWhoseAnswerWasGivenUp…`）钉住。回拒到达之后，断言日志里没有「收到重复的SlotFaultDeclarationCommand」。原因是重发路径在动手之前会先写这一句，而 A 在任何重发之前就回拒了。
- B 由用例 4（`AnAcknowledgedAnswerGivenUpLater…`）钉住。在用例末尾直接调用 `ResendSlotFaultDeclarationResultAsync(FirstDeclarationId)`，断言它抛出 `DURABLE_MESSAGE_ABANDONED`，而且线上的应答条数不变。
- 用例 3 的注释改成它真正测的东西：A 拦下，回拒因为连接不在发不出去，由 D 记日志；下一次会话再回。同时加了一条断言，确认 D 那句日志确实出现。

`mutations-3-review.txt` 与 `mutation-X2-no-A-where.txt`、`mutation-X3-no-B-where.txt` 记录了取红结果。每个变异都整体重新编译，按文件内容还原。

| 变异 | 结果 |
| --- | --- |
| 改后不加变异 | 5/5，退出码 0 |
| X2：只去掉 A | 1 红（用例 2，`Assert.DoesNotContain`），退出码 1 |
| X3：只去掉 B | 1 红（用例 4，`Assert.Throws`：没有抛出异常），退出码 1 |
| 全部还原后重新编译 | 退出码 0 |

### 整类运行在本机负载下的一条红（`class-runs-under-load.txt`）

本轮改完后，`StationDeadlineExpiredG2Tests` 整类加 G2 架构测试跑了两遍，两遍都是 79/80。红的都是 `ALateAckOfARowGivenUpLeavesItGivenUpAndTheSessionGoesOn`，在「等迟到确认的日志」那一步超时。当时本机空闲内存约 2.15 GB，另一张票的全量也在这台机器上跑。

- 这条用例单独跑 6 次，6 次都通过。
- 对照：上一轮的 `1ccf854` 在负载较轻时整类跑过 80/80。这次在同样负载下重跑，同样是 79/80，红的是同一族里的另一条 `AnAckArrivingAfterALoadCancellationAbortedItsSendIsTaken…`，也是在等迟到确认的日志时超时。
- 这一族用例靠真实时钟：先把确认扣住，超过消息超时后才放出，再限时等日志出现。本票没有改它们走的代码。这一点是推断，依据有二：`ALateAckOfARowGivenUp…` 扣的是 `OperationProgress` 的确认，不经过判故障（读到的；另一条我没有读）；本轮改动只有用例断言、日志文案和注释，而且对照里不含本轮改动的 `1ccf854` 也红了。

结论：这两次红来自负载下的时序，和本轮改动无关。已作为票外发现转给调度。

## 全量 `ONBOARD_HMI_G2`（v3，`g2-full-143ed61/`）

调度放行本机时段后运行，期间本机没有其他测试在跑。命令是 `pwsh -File scripts/run-w2g-g2.ps1 -ProtocolRoot scratch/hmi266-protocol-v3`，不带 `-Slice`；协议克隆是普通克隆，detached 在 `3f091cb2`。

- 车载端 `143ed61`，工作树干净。脚本退出码 0，`Status: PASS`。从 14:23:14 跑到 14:28:50（CST）。
- UnitTests 853/853，G2 738/738。和 hmi#264 的 v3 全量对账：853 条相同；G2 是 733 条加本票 5 条。
- schema 收尾：检查 18926 行，0 处新违规，4 处已登记的故意违规（同 hmi#264）。
- 行数比 hmi#264 的 19540 行少。推断原因是心跳、进度重发这类周期报文的数量取决于实际运行时长，这次没有并行负载，跑得更快；没有逐条核实。
- 迟到确认那一族（hmi#270）这次没有红。
