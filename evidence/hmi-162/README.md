# onboard-hmi#162 红绿证据

`WireToGateBusinessService` 里两个进程内状态——`_recoveryAnnouncedAttemptId`（这条恢复宣告本进程已经发过）
与 `_owedRecoveryEntry`（因为别的操作占着屏幕而欠着的那条恢复入口）——**绝不能跨重启存活**。
一旦存活，重启后恢复流程会以为宣告已经发过、什么都不发，恢复入口就从操作员屏幕上消失。
那正是 hmi#109 的死法：CI 与单元测试全绿，只有真装置的恢复场景看得见。

在本票之前，这条红线只靠两张票的警示和一段注释。现在由
`tests/SQCD.Agv.UnitTests/RecoveryMarkerPersistenceArchitectureTests.cs` 守着——**本票的价值就是把这条
红线从「只有真装置看得见」挪到 CI 能看见的地方**。

本目录是**审查之后**的版本。审查确认产品代码是纯重构；守卫有三处中等问题，下面「审查改了什么」逐条写。

## 一个标记要跨重启存活，需要两步；两步都守

先写到盘上，重启时再读回到字段里。只写不读是无害的（attempt id 本来就在 journal 里），而读回**这个字段**
必定要经过一次对它的写。

| 守卫 | 守哪一侧 | 判据 |
| --- | --- | --- |
| `EachMarkIsWrittenOnlyByItsOneWriter` | 读回 | 每个字段只有一个写入口。写的形状认：简单赋值、复合赋值、解构赋值、`ref`／`out` 传出，以及**拆成两行的赋值与 `ref`** |
| `EveryWayToSetAMarkHasARegisteredCaller` | 读回 | 能写标记的六个方法，每个调用它们的方法都登记在案并写明理由。**从一个新方法里恢复标记会红**；已登记方法里多一处调用不会（见盲区） |
| `TheMarksAndTheirSettersNeverLeaveTheBusinessServiceSource` | 写出 | 两个字段名和六个方法名在 `src/`、`tools/` 产品文件里只出现在 `WireToGateBusinessService.cs` |
| `OnlyRegisteredMembersTouchTheMarks` | 写出 | 文件内碰字段的成员恰好是登记的那几个 |
| `MembersThatTouchAMarkReachOnlyTheirListedFields` | 写出 | **审查后新增。** 这些成员能用到的类字段是一张封闭的白名单（锁、两个标记、在途集合、`_logger`、`_clock`），`_executor`／`_vectorExecutor`／`_session` 这类能把值带出进程的字段一个都够不着 |
| `NoMemberThatTouchesAMarkNamesAPersistenceApi` | 写出 | 这些成员里不出现持久化 API 的词（不区分大小写）；现在只负责白名单看不见的静态 API（`File.`、`JsonSerializer`） |
| `TheseGuardsTellALeakFromTheCodeAsItIs` | 自检 | 每条判据都是源码文本的纯函数，喂合成源码双向验证，外加两条钉住的已知盲区 |

**没有一条断言是计数。** 每张登记表都与源码做双向集合比较：多了会红，登记了但代码里已经没有了也会红。

## 审查改了什么

审查确认**产品代码是纯重构**：去注释后非注释改动 5 处，`TryClaim` 的比较加写入仍在同一把锁里一步完成，
没有一步变两步，也没有锁内变锁外。问题都在守卫上：

**M-2：词表认不出这个文件真正写盘的方式。** 这个类里**没有 `_journal` 字段**。它真正写盘走的是
`_executor.MarkResultRecordedAsync`／`RecordPendingResultAsync`（背后是 journal）、`_session.Send*Async`
（发件箱，服务端还能重放回来），以及 `_session.Journal.*`。前两种名字里一个词表里的词都没有；我当时的
合成泄漏写的是 `_journal.Write...`，一个本仓根本没有的写法。审查在登记成员里把标记交给
`_executor.MarkResultRecordedAsync`，6 条全绿。

**这和 #176 第一版是同一个错，只是换了一条判据**：#176 那次没认出被守文件自己写字段的方式，这次没认出
被守文件自己写盘的方式。教训被用在了上次出问题的那个位置，没有被用在上次出问题的那个形状上。
所以这次：

- 不是把 `_executor`、`_session` 补进黑名单（那会对下一个接收者重犯），而是**改成白名单**：登记成员只能用
  列出的六个字段，用了任何别的字段就红，不管它叫什么；
- **对每一条判据都按被守文件的真实写法补了合成例**：写字段（跨行）、写盘（`_executor.`、`_session.Send`、
  `_session.Journal`）、调用写入口（`if (!TryClaim...(...))`、`_ = Exchange...(null)`）、读字段（`?.Context`）。

**M-1：「启动时恢复标记必然是一个新调用方」不成立。** 登记粒度是「哪个方法调用了哪个写入口」这一对，
**已登记方法里多一处调用，这一对本来就在表里，不会红**。而最接近 hmi#109 的恰好是已登记的方法：
`RestorePendingRecoveryOperationProjectionAsync` 本来就拿 journal 里的 attempt id 去占宣告权。
细化到调用点只能靠给调用编号，那是计数，所以没做；**把「必然」改成了它实际保证的范围**（从新方法恢复会红），
并把这个盲区钉进合成自检，红证据 `08` 在真实文件上确认它确实看不见。

**M-3：跨两行的赋值看不见。** 现在认字段在行尾、下一行以赋值运算符开头，以及 `ref`／`out` 在行尾、
下一行以字段开头这两种；以 `==` 开头的下一行（比较）不算。拆成多行的解构赋值仍然看不见，写进了限度。

**Q-1**：名字边界的报错现在告诉人正确做法（把访问挪回主文件里已受检查的成员），并明说**不要把第二个
文件加成「家」**——其余每条守卫都只读主文件，第二个家会一下子失去它们全部，而且全绿。

**Q-2**：「读回必定经过一次写」只对**字段本身**成立。同一种失效可以完全不经过字段：往 journal 里记一个
「已宣告」标志、重启后在恢复流程里提前 return。这里看不见它，已写进限度。

**Q-3**：登记一个只返回字段值的读取器，等于开了一个口子；报错只能提醒，拦不住，已写进限度。

另外 `Setters` 的定义原先写的是「直接或间接设置标记的成员」，按字面四个调用方也算。现在定义改为
「写入口，以及唯一职责是置位或清除标记的辅助方法」，并写明登记表为什么停在往上一层：那四个调用方正是
「本进程此刻要宣告这一条」这个决定发生的地方。

## 「只扫一个文件」站得住的条件，逐条核过

| 条件 | #176（`MainViewModel`） | 本票（`WireToGateBusinessService`） |
| --- | --- | --- |
| 字段只有类自己能碰 | 属性 `private set` | 字段 `private` ✓ |
| 类只有一个文件 | 非 partial ✓ | **partial，另有三个文件** ✗ —— 名字边界那条接住它（红证据 `03`） |
| 没有子类 | `sealed` ✓ | `sealed` ✓ |
| XAML 写不进来 | 只单向绑定 | 私有字段不能绑定 ✓ |
| 没有反射 | ✓ | `src/`、`tools/` 无 `BindingFlags.NonPublic`／`GetField`／`UnsafeAccessor` ✓ |
| 没有源生成器参与 | — | ✓ |

## 写入收口（产品代码改动，逐条；审查核过是纯重构）

- `TryClaimRecoveryAnnouncement`：直接写字段改为 `MarkRecoveryAnnounced(attemptId)`，仍在同一把锁里（`lock` 可重入）。
- 新增 `ExchangeOwedRecoveryEntry(next)`：`_owedRecoveryEntry` 的一处赋值和两处置 null 都改走它。
  **合成一个入口的理由**：置 null 就是还掉欠账，而 hmi#156 数漏的恰好是欠账的释放点。

两个字段的 XML doc 写明了为什么不能持久化、失效时长什么样，指向 hmi#109 和这个测试类。
审查之后产品文件只改了一处注释，没有代码：`_recoveryAnnouncedAttemptId` 的 XML doc 原来说「新调用方就是恢复出来
的标记会露面的地方」，与 M-1 是同一个过度声称，已收窄，并点名 `RestorePending…` 要按这个盲区审。非注释改动行为 0。

## red/：真实文件上的反向验证（审查后的守卫，全部重跑）

每份开头记着注入前的 blob、预期只有哪几条红、命令与限度；同名 `.patch` 只含那次注入。
**上一版的 `.patch` 混进了当时还没提交的收口改动**（`git diff` 对着 HEAD 取，每份多出约 111 行），
这一版的收口已经提交，patch 只剩注入本身。还原用按字节的备份，八次都按 blob 核对
（`WireToGateBusinessService.cs` `1f71a9b5`、`.HardwareRecovery.cs` `679f73e1`）；每次都先核过注入后 blob 确实变了。

| 文件 | 注入 | 预期 | 实际 |
| --- | --- | --- | --- |
| `01-write-outside-the-writer` | `ReleaseInFlightAttempt` 里直接写 `_owedRecoveryEntry = null;` | 写入口那条 + 成员登记那条 | 7 条里红 2 条，正是这两条 |
| `02-writer-hands-the-mark-to-a-serializer` | 写入口里 `JsonSerializer.Serialize(字段)` | 只有持久化 API 那条 | 7 条里红 1 条 |
| `03-other-partial-file-reads-the-mark` | `.HardwareRecovery.cs` 里读字段 | 只有名字边界那条 | 7 条里红 1 条 |
| `04-new-caller-restores-the-mark` | 新方法 `RestoreRecoveryMarkAtStartup` 调写入口 | 只有调用方登记那条 | 7 条里红 1 条 |
| `05-ref-write-replaces-the-writer` | `Interlocked.Exchange(ref 字段, null)` 取代写入口 | 写入口那条 + 调用方登记那条 | 7 条里红 2 条，正是这两条 |
| `06-mark-handed-to-the-executor-journal` | **审查 m3b**：登记成员里 `_executor.MarkResultRecordedAsync(标记, ...)`（上一版 6 条全绿） | 只有字段白名单那条 | 7 条里红 1 条 |
| `07-write-split-over-two-lines` | **审查 m6b**：登记成员里跨两行写 `_recoveryAnnouncedAttemptId`（上一版 6 条全绿） | 只有写入口那条 | 7 条里红 1 条 |
| `08-silent-claim-in-a-registered-method-known-blind-spot` | **审查 m5**：`RestorePending…` 里先无声地占住宣告权 | **全绿**：钉住的已知盲区 | 7 条全绿。它证明的是这个限度在真实文件上确实存在 |

## green/

- `unit-tests.txt` —— `dotnet test tests/SQCD.Agv.UnitTests -c Release`，**511 通过、0 失败**（基线 504，新增 7 条）
- `dotnet-format-verify.txt` —— `exit=0`，零诊断
- `g2-multi-demand-journey.txt` —— `MultiDemandJourneyG2Tests` 单类 **47 条全过**（第一轮跑的；审查后产品代码未再改）
- `probe-which-tests-reach-the-writers.txt` —— 临时让两个写入口抛异常，同一个类 **47 条里 7 条红**，证明那 47 条全绿
  确实走到了改动。前两次探针因编译失败作废（跑的是旧二进制），先看 `0 Error(s)` 才发现

## 这组守卫守不到什么（测试类 remarks 里有完整版）

- **已登记方法里多一处调用**（M-1）：登记粒度是方法对写入口；`RestorePending…` 本来就从 journal 取 id。钉在合成自检里，红证据 `08`。
- **它追名字，不追值**：登记成员把标记复制到局部变量、交给本类一个名字不沾持久化的方法去落盘，所有守卫都绿。字段白名单把它收窄到「本类的方法」，没有消除它。钉在合成自检里。
- **不经过字段的同一种失效**（Q-2）：journal 里的「已宣告」标志加恢复时提前 return。
- **登记一个只返回字段值的读取器**（Q-3）只能靠人拒绝。
- 持久化词表对静态 API 是一张表；跨多行的解构赋值看不见；成员边界依赖 `dotnet format` 的四格缩进。
- `_logger.Write` 按前提放行：技术日志只写不读。

## 不做的

没有改 `InFlightAttemptReleaseArchitectureTests`、恢复流程的判断与时序、协议、服务端、发件箱与日志的持久化结构。
**真装置不需要**：本票不改产品行为，买的是「红线失效时 CI 会红」。
