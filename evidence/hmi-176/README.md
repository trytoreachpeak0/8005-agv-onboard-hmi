# onboard-hmi#176 红绿证据

九个恢复入口（补偿清空、修正装货、强制机械取出、故障交接……）的**写入路径**现在有结构守卫了：
`tests/SQCD.Agv.UnitTests/RecoveryEntryWriteSiteArchitectureTests.cs`。

它承担的是 hmi#171 那条行为判据成立的前提。
`BothRefreshPathsKeepTheRecoveryEntriesClosedWhileALatchStands` 断的是「严重安全故障锁存着的时候，
那两条刷新路径走完，九个入口都关着」，**而它成立的前提是「只有那两条路径写这九个属性」**——
它自己证明不了这一点，因为它只调那两个已知入口。新加第三条路径直接赋值，它一声不响，
而其中三个入口按下去会经 `WireToGateRecoveryVectorExecutor` 真的开仓门。

本目录是**审查之后**的版本。第一版被独立审查找到两处它看不见的形状（下面「审查改了什么」），
红证据已按新守卫全部重跑。

## 判据长什么样，以及为什么不能照票面那句话写

两条路径**语义等价、结构不同**：

- `RefreshWireToGateInputStateCore`：七行各自 `AllowRecoveryEntry(...)`，**函数式**；
  另外两个入口在共用子过程 `RefreshForcedIsolationCore` 里，写法相同。
- `ApplyWireToGatePresentationCore`：`if (RecoveryEntriesBlockedByFatalFault) { 九个全 false; return; }`，
  正常分支直接取业务值、不经 `AllowRecoveryEntry`，靠那个 early return 保护，**语句式**。

所以判据是两条并列，不是一条：

> 九个属性的每一次写，**要么是右边整个就是一次 `AllowRecoveryEntry(...)` 的简单赋值，要么落在同一个
> 成员里一道有效的 `RecoveryEntriesBlockedByFatalFault` early return 之后**（守卫块之内只许写 `false`）。
> 第三种合规写法是属性自己的 setter 把 `value` 写进自己的字段。

只扫 `AllowRecoveryEntry(` 会把 early-return 那条判成「没有闸门」；只认 early return 会看不见函数式
那条。红证据 `01`／`03` 与 `02`／`05` 分别落在这两侧。

## 审查改了什么

独立审查在真实 `MainViewModel.cs` 上做了一批变异。票面点名的那个陷阱第一版守住了；问题出在票面
没点名的地方，两处：

**一、第一版只认简单赋值 `=`（审查中等 1）。** 新加一个成员写
`SetProperty(ref _canRequestLoadCorrection, true, nameof(...))`，五条守卫全绿，连条数自检都不响。
**这一种最要命**：这个文件里每个 setter 都是 `SetProperty(ref _field, value)`，下一个人加第三条路径时
照着现有写法抄，就是这个形状。复合赋值（`|=`）与解构赋值（`(A, B) = (...)`）同样看不见。
现在「一次写」认四种形状：简单赋值、复合赋值、解构赋值、以 `ref`／`out` 传出 backing field。
setter 自己的 `SetProperty(ref _field, value)` 单独算一类合规写法，要求每个入口恰好一处、写的就是 `value`。

**二、「落在 early return 之后」被实现成了「同一成员内、行号在后」（审查中等 2）。** 只要守卫头后面
某处有 `return;` 就算数，不管它在哪一层。守卫包进 `if`、写成 `else if`、塞进 lambda、块内 return
改成 `if (...) { return; }`，核心守卫都绿；最刁的一例是守卫头不带花括号
（`if (RecoveryEntriesBlockedByFatalFault) HasWarning = true;`），后面再有一个无关的 `if` 块带 return，
第一版会把那个无关的 return 当成守卫的。审查说不要求控制流分析，只要求写明盲区并用合成例钉住；
**其中五种用三条便宜的规则就能堵上**，所以堵了：

1. 守卫头在方法体顶层（缩进正好 8 格、以 `if` 开头）；
2. `return;` 在守卫块的第一层；
3. 不带花括号的守卫头，嵌入语句必须就是 `return;`。

外加一条：守卫块之内只许写 `false`（`CanX = true; return;` 也是一道 early return，而它把入口打开了）。

**堵不上的写成了已知盲区**：守卫之后把写入放进 lambda 或本地函数延迟执行，扫描器判合规——要识别它得
知道哪些代码会被推迟执行，那是控制流分析。它按当前结果钉在合成例最后一条里，登记在 `GuardLimits`。
规则 1 靠的是缩进，也就是靠 `dotnet format`，这一点同样登记了。

另外两处也改了：

- **疑 3**：「右边是 `AllowRecoveryEntry(...)`」原先实现成「语句里含这个字符串」，
  `CanX = AllowRecoveryEntry(false) || business;` 会过。现在要求括号一闭合紧跟 `;`，且只认简单赋值。
- **条数自检的报错措辞**：第三条路径落在核心守卫盲区里时，唯一会红的就是条数那一项，而第一版的报错说
  「回到这张表来说明」，等于教人改掉那个数让它闭嘴。现在消息先问三个问题（多出来的是哪一行、锁存期间
  写进去什么、凭什么挡得住），并列出每个写入点与它被判成哪一类，**回答之后**才许可改数。

## red/

每个 `red/` 文件开头记着注入前的 blob、注入了什么、预期只有哪几条红、命令与限度，后面是
`dotnet test` 的原始输出；同名 `.patch` 是那次注入的 diff。测试读的是源码文本，所以注入不需要重编；
测试程序集本身先以 `0 Error(s)` 编过才开始注入（审查那边第一轮就栽在跑了旧二进制上）。

**注入的还原不用 `git checkout`**（那会把同文件里未提交的真实改动一起抹掉——本票正好改了
`MainViewModel.cs` 的三处注释），用按字节的备份还原，每次还原后按 blob 哈希核对 `3371bfe2`，都对上。

| 文件 | 注入 | 预期 | 实际 |
| --- | --- | --- | --- |
| `01-third-write-path` | 新加一个方法，直接按业务值写 `CanRequestLoadCompensation` | 核心那条红（只报新方法那一行），自检那条红（25→26、成员多一个）。其余三条不动 | 5 条里红 2 条，正是这两条；报 `MainViewModel.cs:1660` |
| `02-early-return-loses-its-return` | 守卫块里删掉那一句 `return;`，九个 `false` 原样留着 | 核心那条红（整段 16 个写入点），自检那条红（语句式认不出了） | 5 条里红 2 条，正是这两条；报 1606–1650 共 16 行 |
| `03-functional-wrapper-removed` | 一处 `AllowRecoveryEntry(x)` 换成 `x` | **只有核心那条红，且只报那一行** | 5 条里红 1 条；只报 `MainViewModel.cs:568`，其余 24 处仍判合规 |
| `04-setter-shaped-ref-write` | 新加一个方法，照现有 setter 写法 `SetProperty(ref _canRequestLoadCorrection, true, nameof(...))`（审查中等 1；第一版五条全绿） | 核心那条红（只报那一行），自检那条红（25→26、成员多一个） | 5 条里红 2 条，正是这两条；报 `MainViewModel.cs:1660`，类别 `Unguarded` |
| `05-braceless-guard-borrows-an-unrelated-return` | 守卫头改成不带花括号的 `if (...) HasWarning = true;`，九个 `false` 变成无条件执行，其后跟一个无关的 `if (!_wireToGateEnabled) { return; }`（审查中等 2 最刁的一例；第一版五条全绿） | 核心那条红（整段 16 个写入点），自检那条红（语句式认不出了） | 5 条里红 2 条，正是这两条；报 1605–1652 共 16 行 |

**第三份是最要紧的一份**：它证明守卫不会一红全红，注入一处只指一处。**第四、五份是审查之后才能红的**，
它们之前的「全绿」是审查实测的结果，不是本目录重跑的——本目录只有审查后的守卫。

## 守卫自带的双向自检

注入证明的是「这一天它有判别力」。明天扫描器变瞎时会不会有人知道，靠的是常驻的那两条：

- `TheGuardTellsAThirdWritePathFromACompliantOne` —— **五个合规写法不红、十八个错法各自红**：
  第一版就认得的七种；第一版看不见的五种写法（照 setter 抄的 `ref`、复合赋值、解构赋值、setter 写死值、
  `AllowRecoveryEntry(...) || x`）；第一版判错的六种守卫形状（嵌在条件里、`else if`、塞进 lambda、
  return 套进内层 if、不带花括号的假守卫、守卫块里写 `true`）。最后还有一条按当前结果钉住的已知盲区。
  判据写成了纯函数，所以能直接喂合成源码。
- `TheScannerStillSeesTheRealWriteSites` —— 扫描器在真实源码上拿得出它真看见了的东西：setter 以外的
  写入点条数、九个入口每处至少两次、**每个入口恰好认出一个自己的 setter**、成员集合恰好那三个、
  **两种写法各自都被认出来过**。工具没生效时的默认输出几乎总是「什么都没发现」，和「确实没有」长得一模一样。

## green/

- `unit-tests.txt` —— `dotnet test tests/SQCD.Agv.UnitTests -c Release`，审查后 **504 通过、0 失败**
  （条数与第一版相同：新增的都是同一条测试里的合成例，不是新测试）
- `dotnet-format-verify.txt` —— `exit=0`，零诊断
- `g2-fatal-fault-latch.txt` —— `FatalFaultLatchViewModelTests` 单类 12 条全过（第一版时跑的；审查后
  `MainViewModel.cs` 与那个测试文件都没再动）。**车载端全量 G2 走 CI**，本机内存吃紧

## 第一轮 CI 里，新守卫确实被执行了

这一节核的是**审查前那一版**。审查后那一轮的 run 记在 PR 评论里，不再为它单推一次证据——
那会为一个 md 文件多跑一轮 CI。

CI run
[`35552076347`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/actions/runs/35552076347)
的 `ONBOARD_HMI_G2` 作业 `Status: PASS`，`SQCD.Agv.UnitTests` **504 通过**、
`SQCD.Agv.WireToGateG2Tests` **378 通过**。

**全绿证明不了那五条新测试被执行过**——作业日志只打 `Status: PASS`，`dotnet test` 通过时也不打类名，
而 504 恰好与本机相同，两个相同的数字不能互相佐证。所以链条是三段，每段单独成立：

1. 推送前 `git merge-base --is-ancestor origin/w2g/fp-v2-impl HEAD` 成立，基线未前移，
   **CI 那棵树的内容与本机工作树相同**；
2. 本机 `--filter "FullyQualifiedName!~RecoveryEntryWriteSiteArchitectureTests"` 跑出 **499**，
   不带过滤跑出 **504**——新类贡献的正是 5 条；
3. CI 跑出 504。

计数取自 artifact `g2-evidence` 里的 `logs/dotnet-test-release.log`，不是作业日志。

## 一并回答了票面评论的那个问题：这一族守卫里，哪几条守的是写法不是行为

答案登记在代码里：`RecoveryEntryWriteSiteArchitectureTests.GuardLimits`，一行一条，逐条写出它守不到
什么。`EveryGuardInThisFamilyDeclaresWhatItCannotSee` 用反射把那张表钉在两个测试类的
`[Fact]`/`[Theory]` 清单上——**新增一条守卫而不写下它的限度，那里会红**。

**这一族没有一条是行为判据。** 记录本文时是十四条，八条守源码形状、六条守另一条守卫自己（条数会过时，
以那张表为准；「没有一条是行为判据」由一条断言钉着，长出行为判据那里会红）。它们问的都是「源码里有没有
某个 token、某个形状」，所以换一个语义等价、token 不同的写法它们就全盲。

**审查证明了这句话对本票自己的守卫同样成立**——第一版的限度那一栏漏了中等 1、2 两处盲区，那一栏自称
「实读判据写出来的」，而那时并不成立。现在核心守卫那一行写明了：它认得哪四种写的形状、「early return
之后」按缩进与层级近似而不是控制流分析、守卫后延迟执行的写入它判合规、以及它看不见的反射与别的文件。
`EveryGuardInThisFamilyDeclaresWhatItCannotSee` 自己那一行早就写着它判断不了限度说明是否对——
这次正是这种情况，由人读出来的。

评论点名的 `TheInFlightAggregateReadsBothExecutorsWithAnOr`：把两个执行器换成两个同源字段，它仍然绿；
S2 那条链上三个真实环节一个都没有行为判据。本票的核心守卫同样是这一类：把 `AllowRecoveryEntry` 掏空、
或把 `RecoveryEntriesBlockedByFatalFault` 改成恒假，它照样绿——那一半由
`BothRefreshPathsKeepTheRecoveryEntriesClosedWhileALatchStands` 接住，两条互补，任何一条单独都不够。

有一处限度今天没有承担者：**新增第十个恢复入口而两张清单都不登记，这里和行为判据都不会响**。要守住它，
得先有一个「什么算一个恢复入口」的机器可判的定义，本票没有做。
