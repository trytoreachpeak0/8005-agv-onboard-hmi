# onboard-hmi#171 红绿证据

操作员扫一个不在当前作业清单里的子批，v2 车载端会进严重故障锁存，而 v2 上没有任何代码能把它解开；
同时横幅说「已停止开门」，那句话是假的。

每个 `red/` 文件开头记着跑它时的 commit、注入了什么、对应审查的哪一条、预期只有哪一条红、命令，
后面是 `dotnet test` 的原始输出；同名 `.patch` 是那次注入的 diff。

**注入的还原不用 `git checkout`**（那会连同文件里未提交的真实改动一起抹掉），用按字节的备份还原，
还原后按 blob 哈希核对：`MainViewModel.cs` `b8fde930`、`OnboardController.cs` `277e198c`，两次都对上。

## 一个贯穿全部四份红证据的限度

**这四次都是 `--filter` 单类跑的，所以「只有这一条红」的范围是那个测试类之内**，不是全仓。
单类而不是全量，是因为本机内存吃紧（15.31 GiB 常只剩 2–3 GiB），车载端全量 G2 只走 CI；
单类的内存占用和全量差一个量级，由调度在 2026-09-21 明确批准（他的判断，不是引用现成规矩——
看板上那条「本机只跑单个测试类」原文的主语是服务端票）。

全量的绿由 CI 承担：run `35532577643`，`test` 作业全绿。

## red/

| 文件 | 注入 | 对应 | 预期 | 实际 |
| --- | --- | --- | --- | --- |
| `01-parallel-path-bypasses-the-gate` | `RefreshWireToGateInputStateCore` 里 7 处 `AllowRecoveryEntry(x)` 换成 `x` | 产品缺陷一：被称为「唯一屏障」的那段 `if` 既没有测试，也不是唯一的 | 只有 `BothRefreshPathsKeepTheRecoveryEntriesClosedWhileALatchStands` 红 | 12 条里红 1 条，正是它（`FatalFaultLatchViewModelTests.cs:166`，九条断言里的第一条 `CanRequestWireToGateRecovery`） |
| `02-blocking-back-to-constant-false` | `PublishGuidance` 的 `blocking` 退回 `banner.HasError` | 产品缺陷二：一个恒为 false 的开关和不存在是一样的 | 只有 `AnInFlightSlotOperationTakesTheBannerBackFromARefusal` 红 | 12 条里红 1 条，正是它 |
| `03-operator-notice-never-expires` | `OperatorNoticeHold` 由 8 秒改成 24 小时 | 判据路条目 5：本票新立的红线要有自己的护栏 | 只有 `ARefusalStopsHoldingTheBannerOnceItHasExpired` 红 | 12 条里红 1 条，正是它 |
| `04-reset-never-asks-the-peer` | 构造时丢掉传进来的对端在途查询，恒取 `() => false` | 审查 S2：复位判据「没有在途装卸」在 v2 上恒真 | 只有 `ClearingIsRefusedWhileTheWireToGateExecutorIsStillOpeningADoor` 红 | 35 条里红 1 条，正是它 |

**第四次的注入点选在构造那一步，不是删掉判据本身**，因为真实的失败模式是「接线没接上」——
`_operationLock` 只覆盖 MVP 的 `SubmitScanAsync`，在 v2 上永远拿得到，所以那条判据在 v2 上是空的。

**这四份红证据要证的是这张票最重的一句话**：不是「缺一条测试」，是「缺的那条测试本来会抓到一个
已经存在的缺陷」。两路审查独立跑、互不知情，在两处各自到达了同一个地方。没有原始输出撑着，
那句话就只是一句话。

## green/

- `unit-tests.txt` —— `dotnet test tests/SQCD.Agv.UnitTests -c Release`，**497 通过**（基线 468）
- `dotnet-format-verify.txt` —— `exit=0`

## rig-4e86c69/

真装置 L2 在 CI 上跑（`l2.yml` 的 `real-rig` 作业），run
[`35532850661`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35532850661)，
场景 `real-onboard-compensate-then-reconnect-01` **PASS，127 秒**。

三端（从 `Run real-onboard L2 scenarios` 那一步的输出行读，不是 checkout 段）：
control-server `1017563501da293532856784a313ce73f26391b4`、
onboard `4e86c698b373952f9b772df4678e8fa655beb182`、
simulator `fb5f7c593742bf98bc3957b8729a38aad5321f28`。
`RIG_COMMIT_GUARD` / `RIG_DESKTOP_LOCK` / `RIG_DEADLINE` 命中 1 次、是脚本源码回显，真触发 0。

### 为什么这一轮要重跑，以及它到底证明了什么

上一轮真装置绑的是 `9ae6131b`，`4e86c698` 之后 `git diff 9ae6131b..4e86c698 -- src/` 有
7 个文件、177 行增，所以那一轮对不上最终 head，作废。

**但重跑的理由不止「证据要对得上 head」。** 那 177 行里 `MainViewModel.cs` 占 108 行，
主要一处正是恢复入口的闸门——而 `hmi#109` 出事就在同一个地方：09-19 那次改动让恢复入口消失，
**CI 与单测全绿，只有真装置这个场景看得到**。链条是这样接上的：

1. 场景在补偿前**轮询车载端界面上「补偿清空」按钮的可用性**——`Wait-L2RealButtonOffered`，90 秒，
   循环体是 `[bool]$Onboard.ButtonAvailable($Name)`，**经 UI Automation 直接量界面**，
   每次轮询记进 journal 的 `onboard-compensation-entry`；
2. 等不到就 `Add-L2RealNotReached` 把之后全部断言记为「未走到」，理由写死是
   「车载端没有给出『补偿清空』入口」；等到了才 `Invoke-L2RealConfirmedButton` 真按下去；
3. `hmi#109` 正是撞在这道门上，也是「车载端界面改动要跑真装置恢复场景」那条规矩的来源；
4. 所以这一轮 PASS 覆盖的是：那 177 行没有让「补偿清空」这个入口消失，且按下去之后补偿真的走通。

### 边界

- **只覆盖「补偿清空」这一个入口。** 闸门后面挂着 9 个属性，场景只走它自己那条路。
- **只验证了我没把闸门做过严，没验证做得够严。** 入口该出现时还在 → 这一轮覆盖；
  锁存时该消失 → **这一轮完全不覆盖**，那一面由
  `BothRefreshPathsKeepTheRecoveryEntriesClosedWhileALatchStands` 守（见 `red/01`）。
- **这一轮不能拿来回应「该关的时候关上了」那一面的任何问题。**

### 耗时：66 秒 → 127 秒，是机器负载，不写「单变量对照」

三端代码里确实只有车载端变了（1 和 3 与上一轮逐字相同），但**机器负载不受控，实测差约 1.7 倍**，
所以不用「单变量对照」这个词。三条互相独立的测量：

| 观测 | 本轮 | 上轮 | 倍数 |
| --- | --- | --- | --- |
| 环境准备段（`timeline.jsonl` 的 `Environment is up` 之前） | 75.3s | 43.5s | **1.73×** |
| 场景本体段 | 49.9s | 20.9s | 2.39× |
| 整机已提交内存 平均 | 8.03 GiB | 4.85 GiB | 1.66× |
| 整机已提交内存 峰值 | 11.36 GiB | 6.74 GiB | 1.69× |

**环境准备段里跑的是服务端构建启动、FieldOps 往数据库导数据、模拟器发布缓存——车载端界面代码
一行都不参与。** 它慢 1.73 倍，是一次不含本次改动的纯负载测量，和内存的 1.66 倍几乎重合。
单步最夸张的是 `slot-model-preseed:import-area-assignments`，1.7s → 15.9s（9.4 倍），
那是数据库导入，和 `MainViewModel` 没有任何关系。87 步里 38 步（44%）变慢且分散——
**集中在一两步才是逻辑变化的样子。**

场景本体那段 2.39 倍比 1.73 多出来的部分**分不开**：可能是负载在 WPF、模拟器、服务端三进程
并跑时更重，也可能是改动。不猜。

**慢反而加强了这一轮的结论，理由是判据的方向性：** 场景里的判据是超时型的（等入口 90 秒、
等对账、等重连），**环境变差只会让它们更容易红，不会更容易绿**。在内存压力高 1.7 倍的机器上
全部按时满足，比在空闲机器上 PASS 更硬。这也顺带排除了「闸门让入口晚出现」——
真那样的话慢的会是等按钮那一步，而慢的是数据库导入和进程启动。

（这个方向性只对超时型判据成立。「断言某事没发生」那一类反过来：环境变差更容易假绿，
因为那件事可能只是还没来得及发生。）
