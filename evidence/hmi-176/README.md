# onboard-hmi#176 红绿证据

九个恢复入口（补偿清空、修正装货、强制机械取出、故障交接……）的**写入路径**现在有结构守卫了：
`tests/SQCD.Agv.UnitTests/RecoveryEntryWriteSiteArchitectureTests.cs`。

它承担的是 hmi#171 那条行为判据成立的前提。
`BothRefreshPathsKeepTheRecoveryEntriesClosedWhileALatchStands` 断的是「严重安全故障锁存着的时候，
那两条刷新路径走完，九个入口都关着」，**而它成立的前提是「只有那两条路径写这九个属性」**——
它自己证明不了这一点，因为它只调那两个已知入口。新加第三条路径直接赋值，它一声不响，
而其中三个入口按下去会经 `WireToGateRecoveryVectorExecutor` 真的开仓门。

## 判据长什么样，以及为什么不能照票面那句话写

两条路径**语义等价、结构不同**：

- `RefreshWireToGateInputStateCore`：七行各自 `AllowRecoveryEntry(...)`，**函数式**；
  另外两个入口在共用子过程 `RefreshForcedIsolationCore` 里，写法相同。
- `ApplyWireToGatePresentationCore`：`if (RecoveryEntriesBlockedByFatalFault) { 九个全 false; return; }`，
  正常分支直接取业务值、不经 `AllowRecoveryEntry`，靠那个 early return 保护，**语句式**。

所以判据是两条并列，不是一条：

> 九个属性的每一个赋值点，**要么右边是 `AllowRecoveryEntry(...)`，要么落在同一个成员里一道有效的
> `RecoveryEntriesBlockedByFatalFault` early return 之后**。

三个字眼是判据本身，不是实现细节：

1. **「有效的」的全部内容是那个 `return;`**。少了它，控制流往下走到正常分支，紧接着按业务值把九个
   入口全部写回来——而那个块里九个 `false` 看起来完全正确。红证据 `02` 就是这一种。
2. **「同一个成员里」**。守卫在 A 方法、赋值在 B 方法，正是这张票要抓的那种第三条路径；
   去掉成员边界，合成例的最后一条会变绿。
3. **两条并列**。只扫 `AllowRecoveryEntry(` 会把 early-return 那条判成「没有闸门」；
   只认 early return 会看不见函数式那条。红证据 `01` 与 `03` 分别落在这两侧。

## 「两条路径」是从操作员那一侧数的，赋值点落在三个方法里

票面（与 hmi#171 的注释）说的是两条刷新路径，而源码里写这九个属性的成员有三个：
`RefreshForcedIsolationCore` 是另外两条共用的子过程，最后两个入口只在它里面写。
守卫按**成员**登记，不按「路径」，否则那个子过程会落在判据外面。

## red/

每个 `red/` 文件开头记着注入前的 blob、注入了什么、预期只有哪几条红、命令与限度，后面是
`dotnet test` 的原始输出；同名 `.patch` 是那次注入的 diff。

**注入的还原不用 `git checkout`**（那会把同文件里未提交的真实改动一起抹掉——本票正好改了
`MainViewModel.cs` 的三处注释），用按字节的备份还原，三次还原后都按 blob 哈希核对过：
`MainViewModel.cs` `3371bfe2`，三次都对上。

| 文件 | 注入 | 预期 | 实际 |
| --- | --- | --- | --- |
| `01-third-write-path` | 新加一个方法 `RefreshRecoveryEntriesFromSomewhereElse`，直接按业务值写 `CanRequestLoadCompensation` | `EveryWriteToARecoveryEntryInTheViewModelIsGuarded` 红（报出新方法那一行），`TheScannerStillSeesTheRealWriteSites` 红（赋值点 25→26、成员集合多一个）。其余三条不动 | 5 条里红 2 条，正是这两条。核心那条报的是 `MainViewModel.cs:1660 RefreshRecoveryEntriesFromSomewhereElse` |
| `02-early-return-loses-its-return` | `ApplyWireToGatePresentationCore` 的守卫块里删掉那一句 `return;`，九个 `false` 原样留着 | 核心那条红，报出 `ApplyWireToGatePresentationCore` 里**全部 16 个**赋值点；自检那条红（语句式那条路径已不存在，「两种写法都认得出」失守）。其余三条不动 | 5 条里红 2 条，正是这两条。核心那条报出 1606–1650 共 16 行 |
| `03-functional-wrapper-removed` | `RefreshWireToGateInputStateCore` 里一处 `AllowRecoveryEntry(x)` 换成 `x` | **只有核心那条红，且只报那一行**——计数与成员集合都没变，自检那条不该动 | 5 条里红 1 条，只报 `MainViewModel.cs:568 CanRequestLoadCorrection`，其余 24 个赋值点仍判合规 |

**第三份是这三份里最要紧的一份**：前两份证明「守卫会红」，它证明的是**守卫不会一红全红**——
判据是逐个赋值点的，注入一处只指一处。一个恒红或大面积红的守卫，下一次出事时没人能从它的输出里
读出改哪里。

三份红证据各自的「多红的那一条为什么该红」都写在上表的预期一栏里，不是事后补的解释。

## 守卫自带的双向自检，不只是这三次注入

这三次注入证明的是「2026-09-21 这一天它有判别力」。**明天扫描器变瞎时会不会有人知道，靠的是常驻的
那两条**，它们跟着守卫一起进仓库：

- `TheGuardTellsAThirdWritePathFromACompliantOne` —— 三个合规写法不红、七个错法各自红
  （第三条路径／early return 被删／守卫被行注释掉／守卫被块注释包起来／绕过属性直接写 backing
  field／守卫块里少了 `return;`／守卫在上一个成员里）。判据写成了纯函数，所以能直接喂合成源码；
  hmi#171 那批守卫做不到双向自检，正是因为它们的判据和「去磁盘上读 src」焊在一起。
- `TheScannerStillSeesTheRealWriteSites` —— 扫描器在真实源码上拿得出它真看见了的东西：
  赋值点条数、九个入口每个至少两处、成员集合恰好那三个、**两种写法各自都被认出来过**。
  **工具没生效时的默认输出几乎总是「什么都没发现」，和「确实没有」长得一模一样**，这一条就是为此。

## green/

- `unit-tests.txt` —— `dotnet test tests/SQCD.Agv.UnitTests -c Release`，本次 **504 通过、0 失败**
- `dotnet-format-verify.txt` —— `exit=0`，零诊断
- `g2-fatal-fault-latch.txt` —— `FatalFaultLatchViewModelTests` 单类 12 条全过。本票只改了
  `MainViewModel.cs` 的三处注释，而这一类正是那九个入口的行为判据所在。**车载端全量 G2 走 CI**，
  本机内存吃紧（常只剩 2–3 GiB），这里只跑与本票直接相关的那一类

## CI 那一轮里，新守卫确实被执行了

CI run
[`35552076347`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/actions/runs/35552076347)
的 `ONBOARD_HMI_G2` 作业 `Status: PASS`，`SQCD.Agv.UnitTests` **504 通过**、
`SQCD.Agv.WireToGateG2Tests` **378 通过**（`hmi#149` 那族偶发的 `StationDeadline*` 这轮没撞上）。

**全绿证明不了那五条新测试被执行过**——CI 的作业日志只打 `Status: PASS`，
`dotnet test` 通过时也不打类名，而 504 恰好与本机相同，两个相同的数字不能互相佐证。
所以链条是三段，每段单独成立：

1. 推送前 `git merge-base --is-ancestor origin/w2g/fp-v2-impl HEAD` 成立，基线未前移，
   **CI 那棵树的内容与本机工作树相同**；
2. 本机 `--filter "FullyQualifiedName!~RecoveryEntryWriteSiteArchitectureTests"` 跑出 **499**，
   不带过滤跑出 **504**——新类贡献的正是 5 条；
3. CI 跑出 504。

所以 CI 那 504 里含新类的五条。计数取自 artifact `g2-evidence` 里的
`logs/dotnet-test-release.log`，不是作业日志。

## 一并回答了票面评论的那个问题：这一族守卫里，哪几条守的是写法不是行为

答案登记在代码里，不在这份文档里：
`RecoveryEntryWriteSiteArchitectureTests.GuardLimits`，一行一条，逐条写出它守不到什么。
`EveryGuardInThisFamilyDeclaresWhatItCannotSee` 用反射把那张表钉在两个测试类的 `[Fact]`/`[Theory]`
清单上——**新增一条守卫而不写下它的限度，那里会红**。写成测试而不是一段文档，是因为一份写一次就
不再被碰的限度清单，下一条守卫加进来时没有任何东西提醒作者来补。

结论本身值得写在这里：**这一族没有一条是行为判据。** 记录本文时是十四条，八条守源码形状、六条守另
一条守卫自己（条数会随守卫增减而过时，以那张表为准；**「没有一条是行为判据」不会悄悄过时**——
`EveryGuardInThisFamilyDeclaresWhatItCannotSee` 里有一条断言钉着它，哪天长出一条行为判据那里会红）。
它们问的都是「源码里有没有某个 token、某个形状」，所以**换一个语义等价、token 不同的写法它们就全盲，
而那一刻没有任何东西会红**。

评论点名的 `TheInFlightAggregateReadsBothExecutorsWithAnOr` 是其中一条：它守的是
`HasSlotWorkInFlight` 写成了两个执行器的 `||`，**把两个执行器换成两个同源字段，它仍然绿**；
S2 那条链上三个真实环节（`App` 接线、那个 `||` 的运行时结果、恢复向量执行器的运行时行为）一个都
没有行为判据。本票新增的核心守卫同样是这一类：把 `AllowRecoveryEntry` 掏空成 `=> offeredByBusiness`、
或把 `RecoveryEntriesBlockedByFatalFault` 改成恒假，它照样绿——**那一半由
`BothRefreshPathsKeepTheRecoveryEntriesClosedWhileALatchStands` 接住，两条是互补的，
任何一条单独都不够**。

有一处限度今天没有承担者，写在表里而不是藏着：**新增第十个恢复入口而两张清单都不登记，
这里和行为判据都不会响**。要守住它，得先有一个「什么算一个恢复入口」的机器可判的定义
（例如按 XAML 绑定面或按一个标记接口枚举），本票没有做。
