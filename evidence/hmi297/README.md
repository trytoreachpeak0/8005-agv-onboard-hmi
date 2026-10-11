# hmi#297 证据：诊断 runner 的 -StageRoot 入口转绝对路径

票：trytoreachpeak0/8005-agv-onboard-hmi#297。被测脚本 `scripts/run-staged-g3-recovery-ack-drop.ps1`。

## 为什么用离线复现，不实跑

这个 runner 实跑要起真实模拟器、服务端和车载端 WPF 窗口，占本机桌面时段约 10 分钟；它本身是诊断工具，
不出正式切片证据。为一张只改一行入口的诊断脚本票占时段不值得，票面也允许离线最小复现（修复批调度
2026-10-11 同意）。离线复现照脚本原写法演示票面点名的三处路径消费者，各用一个替身：

| 处 | 脚本里的写法 | 替身 |
| --- | --- | --- |
| A 起 dll | `Start-Process dotnet -ArgumentList @(Join-Path $StageRoot 'simulator\…dll') -WorkingDirectory (Join-Path $StageRoot 'simulator')`（:389-391，:402-404、:420-422 同形） | 一个小探针 dll，报告自己从哪里加载 |
| B 服务端库 | `$env:ConnectionStrings__ControlServer = 'Data Source=' + (Join-Path $StageRoot 'controlserver.db')`，由工作目录在 `control\` 的子进程读（:329） | 同一个探针，用 `Microsoft.Data.Sqlite` 打开该连接串，报告建出的库文件位置。这一处探针 dll 用绝对路径起，免得先撞上 A |
| C 代理记录 | `[IO.File]::WriteAllText((Join-Path $StageRoot 'proxy-events.ndjson'), …)`，即 `RecoveryAckDropProxy.RunAsync` 的第一次写（:47、:87） | 在本进程里做同样的 .NET 调用 |

调用者的 PowerShell 位置是 `<WorkRoot>\caller`，进程的 .NET 当前目录是 `<WorkRoot>\elsewhere`
（交互会话里 `cd` 之后两者就会不同），`-StageRoot` 传相对路径 `stage`，调用者本意是 `<WorkRoot>\caller\stage`。

复现脚本从被测脚本文本里找入口转换行，找到就原样执行，找不到就不执行，所以修复前后是同一条命令：

```
pwsh -File evidence/hmi297/repro-relative-stageroot.ps1 -ScriptPath <被测脚本> -WorkRoot <新目录>
```

## 红：修复前（被测脚本 `1dd1847`，即测试提交 `54dad85` 上的版本）

`red/repro-script-1dd1847.result.json`，三处都不在 stage 下：

- A：`dotnet` 退出码 1，`Could not execute because the specified command or file was not found.`；
  标准输出里 dotnet 说明 `dotnet-stage\simulator\Probe.dll does not exist`。按子进程工作目录推算，
  它找的是 `caller\stage\simulator\stage\simulator\Probe.dll`。
- B：子进程工作目录是 `caller\stage\control`，打开库时报 `SQLite Error 14: 'unable to open database file'`；
  推算位置 `caller\stage\control\stage\controlserver.db`（目录不存在所以打不开；若目录恰好存在，就会静默建出一个错位置的库）。
- C：.NET 把路径解析到 `elsewhere\stage\proxy-events.ndjson`，写入失败（`Could not find a part of the path`）。
  读代码推断（未实跑）：实跑时这个异常落进代理的 Task，监听不会开，表现为等端口 58114 超时；
  不过实跑会更早停在 A，模拟器起不来，脚本先在等端口 1502 时超时。

「推算」两项是按子进程报告的工作目录算出来的，不是子进程读出来的，结果文件里字段名就叫 `inferredResolution`。

护栏在同一版本上两条都红：`red/guard-script-1dd1847.txt`。

## 绿：修复后（被测脚本 `7e52c09`）

`green/repro-script-7e52c09.result.json`，`allUnderStage: true`：

- A：探针从 `caller\stage\simulator\Probe.dll` 加载；
- B：库建在 `caller\stage\controlserver.db`；
- C：记录写在 `caller\stage\proxy-events.ndjson`。

护栏两条都绿：`green/guard-script-7e52c09.txt`。

## 护栏的注入取红

把真脚本里的转换行挪到第一次使用 `$StageRoot` 的那一行（`$proxyTranscript = Join-Path $StageRoot …`）之后，
护栏变红：`red/guard-injected-conversion-after-first-use.diff` 是注入的改动，`.txt` 是测试输出。
测试类里另有一条 `AConversionMovedAfterTheFirstUseIsReported` 在内存里做同样的挪动，每次跑都会证明检查不是空的。

## 护栏覆盖不到什么

`tests/SQCD.Agv.UnitTests/StagedG3ScriptPathArchitectureTests.cs` 只读脚本文本，断言形如
`$StageRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($StageRoot)` 的一行
写在其余每一行非注释的 `$StageRoot` 之前。它：

- 证明不了转换本身对（换成一个形状相同但语义错的调用照样绿），那由上面的离线复现证明；
- 跟不进点源（dot-source）的文件或别处定义的函数；
- 只管这一个脚本，不管 `scripts/` 下其他脚本。

## 复现脚本自己踩过的坑

第一版复现脚本把 `$simulatorReport?.entryAssembly` 写成了这样：PowerShell 里 `?` 可以作变量名的一部分，
这被解析成一个叫 `simulatorReport?` 的空变量，于是 A、B 不论修没修都判 false。修复后的绿结果暴露了它
（子进程明明在 stage 下起来了，判定却是 false）。改成 `${simulatorReport}?.entryAssembly`，并在传给 `Test-UnderStage` 时再套一层括号
（命令参数模式下 `?.` 不被当成成员访问）之后，红绿两边
都用改好的同一份复现脚本重取，修复前的被测脚本取自临时 detached worktree。所以红那边的 false
不是复现脚本自身的 bug：同一个判定函数在绿那边判出了 true。
