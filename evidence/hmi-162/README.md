# onboard-hmi#162 红绿证据

`WireToGateBusinessService` 里两个进程内状态——`_recoveryAnnouncedAttemptId`（这条恢复宣告本进程已经发过）
与 `_owedRecoveryEntry`（因为别的操作占着屏幕而欠着的那条恢复入口）——**绝不能跨重启存活**。
一旦存活，重启后恢复流程会以为宣告已经发过、什么都不发，恢复入口就从操作员屏幕上消失。
那正是 hmi#109 的死法：CI 与单元测试全绿，只有真装置的恢复场景看得见。

在本票之前，这条红线只靠两张票的警示和一段注释。现在由
`tests/SQCD.Agv.UnitTests/RecoveryMarkerPersistenceArchitectureTests.cs` 守着——**本票的价值就是把这条
红线从「只有真装置看得见」挪到 CI 能看见的地方**。

## 一个标记要跨重启存活，需要两步；两步都守

先写到盘上，重启时再读回到字段里。只写不读是无害的（attempt id 本来就在 journal 里），而**读回那一步
必定要经过一次对字段的写**。所以：

| 守卫 | 守哪一侧 | 判据 |
| --- | --- | --- |
| `EachMarkIsWrittenOnlyByItsOneWriter` | 读回 | 每个字段只有一个写入口。写的形状认四种：简单赋值、复合赋值（`??=` 等）、解构赋值、以 `ref`／`out` 传出（hmi#176 的教训） |
| `EveryWayToSetAMarkHasARegisteredCaller` | 读回 | 能写标记的六个方法，每个调用方都登记在案并写明理由。**启动时恢复一个标记，必然是一个新调用方**，它会红，直到有人回答「它传进去的值从哪来」 |
| `TheMarksAndTheirSettersNeverLeaveTheBusinessServiceSource` | 写出 | 两个字段名和六个方法名，在 `src/`、`tools/` 的产品文件里只出现在 `WireToGateBusinessService.cs`。覆盖另外三个 partial 文件、别的类、字符串（反射、JSON 键）、XAML 与配置 |
| `OnlyRegisteredMembersTouchTheMarks` | 写出 | 文件内碰字段的成员恰好是登记的那几个 |
| `NoMemberThatTouchesAMarkNamesAPersistenceApi` | 写出 | 这些成员里不出现持久化 API 的词（journal、序列化、文件、流、outbox、SQLite、settings……，不区分大小写） |
| `TheseGuardsTellALeakFromTheCodeAsItIs` | 自检 | 以上每条判据都是源码文本的纯函数，这里喂合成源码，每一种泄漏各自红、合规写法不红，外加一条钉住的已知盲区 |

**票面给的「名字只出现在一个文件里」为什么单独不够**：它的理由是「要传出去，名字就得出现在别处」。
对别的文件成立，对同一个文件不成立——**这个文件今天就在把 `_owedRecoveryEntry` 里读出的值交给
`_logger.Write`**。在同一个文件里写一句 journal 调用，名字还在同一个文件里。后两条守卫补的就是这个。

**没有一条断言是计数。** 每张登记表都与源码实际情况做双向集合比较：多了会红，登记了但代码里已经没有了
也会红，所以登记表不会悄悄过期；扫描器变瞎时找到的是空集，也不等于登记表。

## 「只扫一个文件」站得住的条件，逐条核过（调度要求照 #176 的清单核，不默认）

| 条件 | #176（`MainViewModel`） | 本票（`WireToGateBusinessService`） |
| --- | --- | --- |
| 字段只有类自己能碰 | 属性 `private set` | 字段 `private` ✓ |
| 类只有一个文件 | 非 partial ✓ | **partial，另有三个文件**（`.HardwareRecovery`、`.ManualChargingReturn`、`.RecoveryVectors`）✗ |
| 没有子类 | `sealed` ✓ | `sealed` ✓ |
| XAML 写不进来 | 只单向绑定 | 私有字段不能绑定 ✓ |
| 没有反射 | ✓ | `src/`、`tools/` 无 `BindingFlags.NonPublic`／`GetField`／`UnsafeAccessor` ✓ |
| 没有源生成器参与 | — | `obj/` 里没有生成的部分 ✓ |

**partial 这一条与 #176 不同**：另外三个文件可以直接读写这两个私有字段。名字边界那条守卫恰好能接住它——
在那三个文件里碰字段，名字就会出现在那里。红证据 `03` 就是这一种。

## 写入收口（产品代码改动，逐条）

非注释改动只有这些，全是收口，**不改任何判断条件、锁的范围与时序**：

- `TryClaimRecoveryAnnouncement`：`_recoveryAnnouncedAttemptId = attemptId;` → `MarkRecoveryAnnounced(attemptId);`。
  仍在同一把锁里——C# 的 `lock` 是可重入的，写入口自己再进一次同一把锁，比较与写入仍是一步。
- 新增 `ExchangeOwedRecoveryEntry(next)`：在锁里放入新值、交回旧值。`_owedRecoveryEntry` 的一处赋值
  （`OweRecoveryEntry`）与两处置 null（`ForgetOwedRecoveryEntry`、`PublishOwedRecoveryEntry`）都改走它。
  `OweRecoveryEntry` 原先自己拿锁做「取旧值、写新值」，现在这一步整个在写入口的锁里，原子性不变。

**`_owedRecoveryEntry` 的赋值与置 null 合成了同一个入口**，票面要求写明理由：置 null 就是「还掉这笔欠账」，
而 hmi#156 数漏的恰好是欠账的释放点（三方一起数漏了 25%）。合成一个入口之后，任何新的置位或清除方式都
必然是这个入口的一个新调用方，而调用方是登记在案的。分成两个入口也能做，但会让「清除」这一侧多出一个
需要单独登记的门，而清除正是出过错的那一侧。

两个字段的 XML doc 写明了为什么不能持久化、失效时长什么样（指向 hmi#109），并指向这个测试类。

## red/：真实文件上的反向验证

每份开头记着注入前的 blob、预期只有哪几条红、命令与限度；同名 `.patch` 是那次注入。测试读的是源码文本，
注入不需要重编；测试程序集先以 `0 Error(s)` 编过。还原用按字节的备份，五次都按 blob 核对
（`WireToGateBusinessService.cs` `6290ce86`、`.HardwareRecovery.cs` `679f73e1`）。

| 文件 | 注入 | 预期 | 实际 |
| --- | --- | --- | --- |
| `01-write-outside-the-writer` | `ReleaseInFlightAttempt` 里直接写 `_owedRecoveryEntry = null;` | 写入口那条红；「只有登记成员能碰」那条红（多了一个碰字段的成员）。其余不动 | 6 条里红 2 条，正是这两条；报 `WireToGateBusinessService.cs:1162 ReleaseInFlightAttempt` |
| `02-writer-hands-the-mark-to-a-serializer` | 写入口 `MarkRecoveryAnnounced` 里把字段交给 `JsonSerializer.Serialize` | **只有持久化 API 那条红** | 6 条里红 1 条。命中 `JsonSerializer`、`Serialize`，也顺带命中注入时起的变量名 `persisted`——光 `JsonSerializer` 就足以让它红 |
| `03-other-partial-file-reads-the-mark` | `WireToGateBusinessService.HardwareRecovery.cs` 里读 `_recoveryAnnouncedAttemptId` | **只有名字边界那条红** | 6 条里红 1 条，报的就是那个 partial 文件 |
| `04-new-caller-restores-the-mark` | 新加 `RestoreRecoveryMarkAtStartup(restored)` 调 `MarkRecoveryAnnounced`——**票面没写到、最该防的那一步：启动时把标记读回来** | **只有调用方登记那条红** | 6 条里红 1 条：`new: RestoreRecoveryMarkAtStartup -> MarkRecoveryAnnounced` |
| `05-ref-write-replaces-the-writer` | `PublishOwedRecoveryEntry` 里用 `Interlocked.Exchange(ref _owedRecoveryEntry, null)` 取代写入口 | 写入口那条红（`ref` 写入在入口之外）；调用方登记那条红（登记的那次调用不在了）。其余不动 | 6 条里红 2 条，正是这两条 |

`01` 与 `05` 各红两条，第二条都是另一条守卫从另一个角度看到了同一处改动，理由写在预期栏里，不是事后补的。
**`02`、`03`、`04` 各只红一条**，说明每条守卫守的是不同的东西，不是一红全红。

## 双向自检里抓到了守卫自己的一个洞

第一版的持久化 API 词表区分大小写，只认大写的 `Journal`；合成泄漏 `_journal.WriteRecoveryMarkAsync(...)`
——journal 字段真实的命名方式——**它没认出来**。合成自检第一次跑就红在这里，改成不区分大小写后才绿。
没有这条自检，这个洞会带着「守卫全绿」一起合进去。

## green/

- `unit-tests.txt` —— `dotnet test tests/SQCD.Agv.UnitTests -c Release`，**510 通过、0 失败**（基线 504，新增 6 条）
- `dotnet-format-verify.txt` —— `exit=0`，零诊断
- `g2-multi-demand-journey.txt` —— `MultiDemandJourneyG2Tests` 单类 **47 条全过**。欠账与宣告标记的行为用例
  集中在这个类里；本票把三处直接写字段改成了经写入口，这里验行为未变。全量 G2 走 CI（本机内存吃紧）
- `probe-which-tests-reach-the-writers.txt` —— **上面那 47 条全绿有没有走到改动**：临时让两个写入口开头抛异常，
  同一个类 **47 条里 7 条红**，恢复后全绿。所以那 47 条里确实有 7 条走到了本票改动的写入口。
  输出里没有异常信息本身（异步路径把它吞了，用例按行为红），两次运行唯一的差别就是探针，归因成立。
  **前两次探针作废**：注入后编译失败（`CS0162` 不可达代码、`CS8602` 空引用，都被当成错误），测试跑的是旧二进制、
  照样全绿。先看 `0 Error(s)` 才发现，第三次改成编译器算不出来的条件才有效

## 这组守卫守不到什么（写在测试类的 remarks 里，这里摘要）

- **它追名字，不追值。** 登记成员把标记复制到一个局部变量，交给一个名字里看不出持久化的方法，而那个方法
  在别处落盘——每条守卫都绿。这个盲区按当前结果钉在合成自检的最后一条里；将来扫描器学会了数据流，
  那一条会失败，届时把它挪进泄漏那一组。
- **持久化 API 词表是一张表**，名字里一个词都不沾的 API 会漏过去。
- **`_logger.Write` 是按前提放行的，不是查出来的**：技术日志只写不读，`src/` 里没有代码把它读回进程状态
  （2026-09-21 实查）。哪天有代码读日志恢复状态，这个放行就不成立了。
- 成员边界靠 `dotnet format` 的四格缩进。
- **登记表的理由栏是人写的**：它能保证每个调用方都有一句理由，保证不了那句理由是对的。

## 不做的

没有改 `InFlightAttemptReleaseArchitectureTests`、恢复流程的判断与时序、协议、服务端、发件箱与日志的持久化结构。
**真装置不需要**：本票不改产品行为，买的是「红线失效时 CI 会红」，而这条红线失效时原本只有真装置的恢复场景看得见。
