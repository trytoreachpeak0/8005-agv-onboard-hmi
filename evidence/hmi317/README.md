# hmi#317 操作记录落盘：护栏变异自检证据

票：trytoreachpeak0/8005-agv-onboard-hmi#317（屏幕状态快照 T4，「操作记录」写进技术日志）。PR：#329。

这里只放护栏的变异自检：每次故意改坏一处产品代码，跑定向测试，看护栏和行为测试会不会变红，跑完还原。
没有任何日志样本，所以没有子批号或人员号要脱敏。

## 两轮

| 轮 | 跑在 | 改的文件 | 脚本 | 输出 |
| --- | --- | --- | --- | --- |
| 第一轮（第一部分） | `f2abcd2a`，2026-10-12 | `OperatorRecordOutput/OperatorRecordLog.cs`、`App.xaml.cs` | `mutate-part1.py` | `mutations-part1.txt`（从会话控制台照抄，那一轮没有落文件） |
| 第二轮（第二部分与审查 S1） | `d4c2c739`，2026-10-13 | 只改 `ViewModels/MainViewModel.cs` | `mutate-part2.py` | `mutations-part2.txt`（脚本原样输出） |

两轮都在调度排给的本机时段里跑，单线程，SDK 8.0.425，`dotnet` 只在 worktree 里发起。脚本每一轮写入变异、构建两个测试项目、跑定向测试，然后在 `finally` 里还原原文件。第二轮还会在改完后核对 `git diff --name-only` 恰好只有 `MainViewModel.cs`。

## 结论

- 第一轮 6 个变异都被抓到。M3 那一行 G2 一栏不算数：G2 项目在那个变异下编译失败，测的是上一轮留下的程序集；它只算源码护栏抓到。
- 第二轮 7 个变异都被抓到，没有存活，也没有作废轮（每一轮两个项目都构建成功）。

| 变异 | 源码护栏（UnitTests） | 行为测试（G2）变红的用例 |
| --- | --- | --- |
| P1 业务拒绝绕过 `AppendLogLine` 直接 `Logs.Add` | `LogsIsWrittenOnlyThroughAppendLogLineAndClearedOnlyByTheCommand` | `TheScreenKeepsTheLast300LinesAndTheLogKeepsEveryOne` |
| P2 修正窗口不填来源 | `EveryLineBuiltUnderSrcNamesItsSource` | `TheClosedCorrectionWindowNoticeIsLoggedUnderItsOwnSource` |
| P3 业务拒绝的来源写错 | 不红：护栏只查有没有填来源 | `EachOperatorRecordOnScreenIsOneTechnicalLogLine` |
| P4 `AppendLogLine` 不调 `TrimLogs` | 不红 | `TheScreenKeepsTheLast300LinesAndTheLogKeepsEveryOne` |
| P5 操作员事件的尝试号换成 null | 不红 | `AnOperatorEventLineCarriesItsAttemptAndKind` |
| P6 控制器记录的 `OperationId` 换成 null | 不红 | `AControllerRecordLineCarriesTheControllersOperationId` |
| P7 `CorrectionWindow` 与 `ControllerRecord` 互换 | 不红 | `AControllerRecordLineCarriesTheControllersOperationId`、`TheClosedCorrectionWindowNoticeIsLoggedUnderItsOwnSource` |

第二轮全部跑完后，`git status` 与 `git diff` 都为空。重新构建后复跑同样两组：UnitTests 13 个、G2 18 个全部通过，退出码都是 0。

## 第二轮跑的测试

- UnitTests：`--filter "FullyQualifiedName~OperatorRecordLogArchitectureTests"`，13 个。
- G2：`--filter "FullyQualifiedName~SQCD.Agv.WireToGateG2Tests.OperatorEventVisibilityTests|FullyQualifiedName~SQCD.Agv.WireToGateG2Tests.OperatorRecordLogTests|FullyQualifiedName~TheClosedCorrectionWindowNoticeIsLoggedUnderItsOwnSource"`，18 个。

## 复现

在本仓 worktree 根目录，工作树干净时执行：

```
python -I evidence/hmi317/mutate-part2.py
```

不带参数会依次跑全部变异；也可以只跑某几个，例如 `python -I evidence/hmi317/mutate-part2.py P3-source-wrong`。脚本会改源码再还原，跑之前先提交，跑完核对 `git diff` 为空。
