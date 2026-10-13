# hmi#337 证据：出发前检查 UNSAFE/UNKNOWN 分支不再依赖上报滞后

用例：`MultiDemandJourneyG2Tests.APreDepartureCheckNamingTheFirstDemandIsAnsweredAsForOneItem`
（`tests/SQCD.Agv.WireToGateG2Tests/MultiDemandJourneyG2Tests.Paths.cs`）。只改测试，`FakeIoModuleClient` 未动（票面二选一取 B）。

## 机理

`FakeIoModuleClient.PulseUnlockAsync` 与 `SetUnreadable` 都不发 `SnapshotChanged`，变化后的读数要等下一次别的东西唤醒业务层的安全上报才报出去。
会话状态发布正是这样一个唤醒点：`WireToGateBusinessService.OnSessionStateChanged` → `RequestSafetyStateChange`。
用例原来在 `Delay(300)` 后读 `SafetyStateVersion` 再发检查；读版本与车端处理检查之间若有一次唤醒，已接受版本前移一位，
检查按 `expectedSafetyStateVersion < accepted` 被回 `PREDEPARTURE_CHECK_EXPIRED`（`WireToGateBusinessService.cs:4119`）。

## 注入

读版本之后，假服务端 `SendSessionReadinessAsync()` 发一条 `SessionReadiness`（会话 `PublishUnderGate` → `StateChanged`），
再等 `SafetyStateVersion` 前移最多 3 秒，然后照原样发检查。注入点正好落在票面说的窗口里：它就是「读 version 与车端处理检查之间的一次会话状态发布」，
且等到版本前移才发检查，把窗口撑到确定发生。注入 diff 见 `mutations/00-*`、`01-*`、`04-*`。

## 结果

| 轮 | 被测 | 内容 | 结果 | 文件 |
| --- | --- | --- | --- | --- |
| 1 | 基线 f487a16 | 叠注入 5 遍 | UNSAFE 5/5 红，回 `PREDEPARTURE_CHECK_EXPIRED`；读版本时门开尚未上报（`reportedDepartureSafeAtRead=True`，2→3） | `red/01-*`、`red/02-*` |
| 1 | 5055bfb | 原版 5 遍 | 5/5 绿 | `green/01-*` |
| 1 | 5055bfb | 叠注入 5 遍 | 5/5 绿，版本 3→3 不再前移 | `green/02-*` |
| 1 | 5055bfb | M1：去掉 `io.PublishSnapshot()` | UNSAFE 红：5 秒内门开从未上报 | `red/03-*` |
| 1 | 5055bfb | M2：等待去掉内容比对，叠注入 3 遍 | **存活**（3/3 绿）：后面仍有 `Delay(300)` 替它补上了上报 | `mutations/r1-m2-survived-*` |
| 1 | 5055bfb | 本类 + 两组架构测试；UnitTests 两组架构测试 | 152/152、203/203 绿 | `green/03-*`、`green/04-*` |
| — | a0cb811 | `dotnet format ./SQCD_8005AGV.sln --verify-no-changes --no-restore`（G2 第一步同口径） | exit 0 | `green/09-*` |
| 2 | a0cb811 | 原版 3 遍 | 3/3 绿 | `green/05-*` |
| 2 | a0cb811 | 叠注入 3 遍 | 3/3 绿，版本 3→3 | `green/06-*` |
| 2 | a0cb811 | M2 叠注入 | **红**：UNSAFE、UNKNOWN 两例都超时等不到 Result，版本 2→3 | `red/05-*` |
| 2 | a0cb811 | M1 | UNSAFE 红 | `red/04-*` |
| 2 | a0cb811 | 本类 + 两组架构测试 | 152/152 绿 | `green/07-*` |

`green/08-r2-injection-log.txt` 的前 9 行是第 2 轮叠注入（绿），最后 3 行是 M2 那一遍（红）。

第 1 轮 18:11～18:32 期间另一张票误跑了约 4 分钟纯 pwsh 脚本；本表的红都是确定性的（日志直接给出版本前移或上报缺失），不受影响，M2 已在第 2 轮无干扰时段重测。

## 结论

- 票面推断复现：注入下原判据 UNSAFE 5/5 回 `PREDEPARTURE_CHECK_EXPIRED`。
- 新判据：UNSAFE/UNKNOWN 分支先 `PublishSnapshot`，再等「最近一次上报内容相符且已被接受」，之后不再有沉淀延时。注入下判对；去掉通知（M1）或去掉内容比对（M2）都红。
- UNKNOWN 分支原来同样依赖 300 毫秒延时（第 2 轮 M2 下也红），一并改。
