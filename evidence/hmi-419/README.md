# hmi#419 证据：仓锁 1→0→1 抖动后只上报了中间的「未锁」

## 现场（11-18 agv02，车载端 d3357398）

车载日志（`C:/work/8005-scratch/fleet-20261117/agv02-onboard.txt`）的最后几行：

| 时刻 | 事件 |
| --- | --- |
| 07:56:42.107 | 6 号仓锁 DI 0→1（关门） |
| 07:56:42.114 | `SafetyStateChanged` v12278，departureSafe=True |
| 07:56:42.248 | 6 号仓锁 DI 1→0（抖动开始）——之后**没有**「安全状态变化」行 |
| 07:56:42.353 | 「会话中途安全快照」v12279，LOCK_NOT_CLOSED，ioObservedAt=42.248 |
| 07:56:42.357 | 6 号仓锁 DI 0→1（抖动结束）——之后**没有**「安全状态变化」行 |
| 07:56:42.399 | 快照 ack，会话状态发布 v12279 |

服务端在 v12278 之后要了一次快照：6 号仓带着「期待动作超时」告警，服务端对触及超时仓的变化都会再要快照（control-server `OnboardMessageProcessor.AffectsAnOverdueSlotAsync`）。

## 哪一层吞掉了 0→1

- **IO 采样：不是。** `ModbusTcpIoModuleClient` 每轮轮询（100 毫秒）不论变没变都发 `SnapshotChanged`；日志里两次变化都记到了。
- **上报合并：不是。** `RequestSafetyStateChange` 只置一个待办标记，工作循环每一轮都读实时 IO，不会丢掉最终态。
- **去重签名：是。** `WireToGateBusinessService.QueueSafetyStateChangeAsync` 用 `_lastSafetySignature` 表示「服务端手上拿着的」，与此刻读数相同就不发。会话中途回应 `SafetyStateSnapshotRequested`（`AnswerSafetyStateSnapshotRequestAsync`）发出的快照替换了服务端手上的安全状态，却没有动这个签名。快照读到抖动中途的 0，并且在等 ack 期间一直持有安全发送锁；两次 IO 变化请求的上报都排在锁后面，等 ack 回来才运行，那时锁已读回 1，签名与抖动前那条 v12278 相同，于是什么都不发。服务端停在快照的 UNLOCKED 上。
- **发送节流：没有这一层。**

握手快照的同一类问题已在 hmi#399、#401 修过（`HandshakeSnapshotHolds`），会话中途快照这条路没有跟上。现场版 d3357398 与集成分支 `2c2845f` 在 `WireToGateBusinessService.cs`、`ModbusTcpIoModuleClient.cs`、`WireToGateSafetyEvaluator.cs` 上没有差别；`WireToGateSessionClient.cs` 只差一处清单 `Eqp` 字段，与这条路径无关。

## 修法

1. `WireToGateSessionClient.PublishSafetyStateSnapshotAsync` 返回快照里带的安全摘要（以前返回 `bool`）。
2. 回应快照请求时，发出前先把签名清空；快照被确认后，按握手同一条规则核对：快照摘要与此刻读数的摘要相同，就把此刻读数当作已经报过；不同，签名保持为空，下一轮把此刻读数作为一条新的 `SafetyStateChanged` 报出去，版本高于快照。下一轮由快照 ack 引起的会话状态发布触发（`OnSessionStateChanged`），它排在本次持有的发送锁后面，所以一定在签名清空之后运行。
3. 快照的 ack 没等到（超时等失败）：服务端可能已经收下了快照，手上是什么不确定，签名保持为空，并请求一轮上报。
4. 会话此刻发不了快照（什么也没发出去）：签名恢复原值。

不做「每次快照后都补报一条变化」：服务端对超时仓每收到一条变化就要一次快照，两边会无限互相触发。用例 `ASnapshotThatCarriesThePresentReadingIsNotFollowedByAChange` 守这一点。

不改动的：真实的「没锁」照常立即报出（没有加延时或去抖）；行驶中门锁读不到的宽限（cs#789）不在这条路径上；服务端、协议取值、界面与恢复入口都不动。

## 测试

`tests/SQCD.Agv.WireToGateG2Tests/StationDeadlineExpiredG2Tests.LockBounceAfterSnapshot.cs`，夹具是真业务服务加一条 1 号仓装货：

| 用例 | 内容 | 基线 | 修复后 |
| --- | --- | --- | --- |
| `ALockBounceCaughtByAMidSessionSnapshotEndsWithTheClosedLockReported` | 装货完成、安全态报完；服务端要快照，快照读到抖动中途的 0 并在等 ack；IO 事件 1→0，100 毫秒后 0→1，再放行 ack。服务端最后持有的必须是锁闭，版本高于快照 | 红（停在 v4 快照 LOCK_NOT_CLOSED） | 绿 |
| `ALockBounceCaughtByASnapshotWhoseAckIsLostEndsWithTheClosedLockReported` | 同上，快照的 ack 被服务端收下后丢掉 | 红 | 绿 |
| `ALockBounceNobodyAnnouncesIsStillReportedAfterTheSnapshot(ackLost: false/true)` | 读数在等 ack 期间变化，但没有任何 IO 事件 | 红 ×2 | 绿 |
| `ASnapshotThatCarriesThePresentReadingIsNotFollowedByAChange` | 快照内容等于此刻读数后，IO 连发 5 次，不应再有 `SafetyStateChanged` | 绿（守护项） | 绿 |

- `red-base.txt`：基线源码（`2c2845f`）上跑这 5 条的结果。
- `test-runs.txt`：修复后连跑三遍；`MultiDemandJourneyG2Tests`、`WireToGateG2Tests`、`RecoveryVectorG2Tests` 整类与单元测试项目。`MultiDemandJourneyG2Tests` 整类那一遍有 3 条红（取消与故障闩锁类，不发快照请求，不经过改动的代码），单独重跑两遍都通过；调度确认这是车载端 G2 在并发负载下的已知偶发超时。
- `mutations.txt`：每次只改 `WireToGateBusinessService.cs` 一处，核对只改了一个文件，跑完只还原它。

| 变异 | 结果 |
| --- | --- |
| M1 快照后一律把此刻读数当作已报（去掉摘要比对） | 杀（2 条红） |
| M2 快照后一律补报一条变化 | 杀（守护项红） |
| M3 发快照前不清签名 | 杀（ack 丢失 2 条红） |
| M4 摘要不同时另外请求一轮上报 | 存活 → 查明多余（ack 的会话状态发布已请求一轮），已删（`7f3ba81`） |
| M5 快照回应失败后不请求上报 | 杀（无事件、ack 丢失那条红） |

M1、M2 第一次写成 `sent is ...` 的条件，被可空性检查拦在编译阶段，`mutations.txt` 里是改用 `_disposed` 后的重跑结果。

## 其他 DI 与同类路径清单

| 路径 | 是否同类 | 处置 |
| --- | --- | --- |
| 仓锁、光幕、开锁输出回读三路 DI → `SnapshotChanged` → 安全状态上报 | 同一条管线，同一个去重签名 | 本修复在签名层，三路一并覆盖 |
| 车辆安全信号（`OnVehicleSafetySignalChanged`）、故障闩锁变化 | 同一条管线 | 一并覆盖 |
| 握手时的全量 `SafetyStateSnapshot` | 同类，已由 hmi#399、#401 修过 | 不动 |
| `SafetyStateChanged` 自身等 ack 期间 IO 又变 | 不是：签名记的是发出去的内容，下一轮与此刻读数比 | 不动 |
| 补发、迟到 ack、换连接重绑的 `SafetyStateChanged` | 不是：签名记的是那条被确认的内容（`SettlePassedSafetyChangeAsync`） | 不动 |
| 告警快照 `OnboardAlarmSnapshot`（含期待动作超时） | 不是：每秒拿「服务端已确认的告警」与当前告警板比，不同就重报 | 不动 |
| 出发前安全检查答复 `PreDepartureSafetyCheckResult` | 不是同一类：它是对一次检查的一次性答复，不改服务端持有的安全状态版本（服务端只读其结论）。抖动中途答成 UNSAFE 后服务端是否会重查，属于服务端 | 范围外，报调度 |
| 装卸执行器按 IO 判结果（`WaitForLockerAsync`） | 不是安全状态上报；抖动落在判定时刻会影响单次结果 | 范围外，报调度 |
