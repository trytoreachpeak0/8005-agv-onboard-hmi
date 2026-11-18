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

（审查返工后的样子；第一版只比摘要，审查 M1 指出光幕漏了，见下面「审查返工」。）

1. `WireToGateSessionClient.PublishSafetyStateSnapshotAsync` 返回快照带的内容 `WireToGateSentSafetySnapshot`：安全摘要，加上逐仓状态所依据的原始 IO 读数（以前返回 `bool`）。另有一个带回调的重载，在信封建好、马上写出之前回调一次，带同样的内容。
2. 快照被确认后（`AdoptAnsweredSnapshot`），用 `SafetyStateChanged` 去重所用的同一个完整签名比对：摘要加每个仓的锁、光幕、开锁输出原始读数。快照的签名与此刻读数的签名相同，此刻读数算已报；不同，签名清空，下一轮把此刻读数作为一条新的 `SafetyStateChanged` 报出去，版本高于快照。服务端见到快照之后有变化涉及这些仓，就不再用快照里的逐仓状态（control-server `SessionSlotReadings`）。下一轮由快照 ack 引起的会话状态发布触发（`OnSessionStateChanged`），它排在本次持有的发送锁后面。
3. 快照已写出、ack 没等到（超时等失败）：服务端可能已经收下了，手上是什么不确定，签名清空，并请求一轮上报。
4. 什么也没写出（会话此刻发不了、版本号不合法、读数或构造时抛异常）：签名不动，不请求上报。不再在发出前预先清空签名（审查 L1）。

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

## 审查返工（PR #420 审查 M1、L1、L2）

- **M1**：第一版只比摘要。光幕在快照读取时读成「空」、ack 前恢复「有货」，摘要不变，之后不再上报；服务端拿快照里 `physicalState=EMPTY` 当这个仓的读数（`SessionSlotReadings`、`OnboardDispatchFactsReader.HasUnsecuredSlot`）。两扇锁互换状态同理。改为比完整签名。
- **L1**：第一版在发出前就清空签名，写出之前失败（读数或构造抛异常）也会补报一条内容相同的变化，白白抬一次版本、让服务端在途的出发前检查过期。改为客户端在写出前回调，业务层只在写出后失败时才清签名、请求上报。
- **L2**（变异 M6：「发不了时不恢复签名」存活）：发出前不再预先清空，也就没有「恢复」这一步，M6 对应的代码不存在了。对应的风险由 L1 的用例覆盖。
- **L3**（`AdoptAnsweredSnapshot` 里的 `_lastSafetySignature = null` 多余）：预先清空去掉之后，这一句不再多余，是唯一清空的地方，保留。

新用例（同一文件）：

| 用例 | 9c6950d 上 | 返工后 |
| --- | --- | --- |
| `ALightCurtainBounceCaughtByASnapshotIsFollowedByAChange`（由审查探针 P1 改写） | 红 | 绿 |
| `TwoLocksTradingPlacesUnderASnapshotAreFollowedByAChange` | 红 | 绿 |
| `ASnapshotAnswerThatFailsBeforeWritingLeavesTheReportedStateAlone`（假 IO `FailNextRead`：下一次读数抛一次异常，正好落在快照读取上） | 红（多发 v5，内容与 v3 相同） | 绿 |

`red-rework.txt` 是取红，`mutations-rework.txt` 是返工后的变异：

| 变异 | 文件 | 结果 |
| --- | --- | --- |
| R1 只比摘要（即 9c6950d 的做法） | 业务 | 杀（光幕、互换 2 条） |
| R2 失败一律清签名，不管写没写出 | 业务 | 杀（写出前失败那条） |
| R3 失败一律不清签名 | 业务 | 杀（ack 丢失 2 条） |
| R4 客户端写出前不回调 | 客户端 | 杀（ack 丢失 2 条） |
| R5 确认后一律算已报 | 业务 | 杀（4 条） |
| R6 确认后一律补报 | 业务 | 杀（守护项），三次重跑都杀；第一次那一遍 8 条全绿，跑的是未变异代码（R6 生效必红守护项，R5 残留必红 4 条），是增量构建没编进去，未再深究 |

审查探针 P1～P3 在返工后临时放进来跑过，3 条全绿，没有入库。

## 其他 DI 与同类路径清单

| 路径 | 是否同类 | 处置 |
| --- | --- | --- |
| 仓锁 DI 被中途快照截到 | 本票 | 修（摘要变了） |
| 光幕 DI、开锁输出回读被中途快照截到；两扇锁互换状态 | 同类：摘要不变而逐仓读数变了，服务端拿快照里的逐仓状态当读数 | 修（审查 M1：比完整签名，含每仓三路原始读数） |
| 车辆安全信号、故障闩锁在快照前后变化 | 同类，都在摘要里 | 一并覆盖 |
| 握手时的全量 `SafetyStateSnapshot`：摘要变化 | 同类，已由 hmi#399、#401 修过 | 不动 |
| 握手时的全量 `SafetyStateSnapshot`：摘要不变而逐仓读数变化（如光幕在握手读取后变了） | 同类：第一轮返工后仍只比摘要，`MultiDemandJourneyG2Tests.ALightCurtainThatMovedOnAfterTheHandshakeReadItIsReportedAgain` 在 `c8ff60e` 上红 | 修（调度裁定在本 PR 修）：`WireToGateAcceptedSafety` 加 `Readings`，`HandshakeSnapshotHolds` 比完整签名；漫游内容不变仍不补报 |
| `SafetyStateChanged` 自身等 ack 期间 IO 又变 | 不是：签名记的是发出去的内容，下一轮与此刻读数比 | 不动 |
| 补发、迟到 ack、换连接重绑的 `SafetyStateChanged` | 不是：签名记的是那条被确认的内容（`SettlePassedSafetyChangeAsync`） | 不动 |
| 告警快照 `OnboardAlarmSnapshot`（含期待动作超时） | 不是：每秒拿「服务端已确认的告警」与当前告警板比，不同就重报 | 不动 |
| 出发前安全检查答复 `PreDepartureSafetyCheckResult` | 不是同一类：一次性答复，不改服务端持有的安全状态版本 | 审查判定不开票：服务端在安全版本变化时立即重发检查，版本不变也会在窗口过期后重发，不会卡住 |
| 装卸执行器按 IO 判结果（`WaitForLockerAsync`） | 不是安全状态上报 | 审查判定不开票：主路径要求 300 毫秒稳定窗口，约 0.1 秒的抖动影响不到结果 |

## 第二轮返工：握手快照（调度裁定在本 PR 修）

握手的全量快照有与 M1 相同的问题：`HandshakeSnapshotHolds` 只比摘要。重连握手读到光幕「空」、会话就绪前恢复「有货」，摘要不变，于是不补报，服务端拿握手快照里的 `physicalState=EMPTY` 当这个仓的读数。

- 复现：`MultiDemandJourneyG2Tests.HandshakeSlotReadings.cs` 的 `ALightCurtainThatMovedOnAfterTheHandshakeReadItIsReportedAgain(repeatGeneration: false/true)`，照 #401 的 `AReadingThatMovedOnAfterTheHandshakeReadItIsReportedAgain` 写：握手快照登记后用 `HandshakeReadinessHold` 扣住就绪，期间把光幕恢复。在 `c8ff60e` 上两种都红（`red-handshake.txt`），时序走得到。
- 修法：`WireToGateAcceptedSafety` 加可空的 `Readings`（快照的逐仓状态所依据的原始读数），握手与会话中途快照登记时带上，其他来源为空；`HandshakeSnapshotHolds` 改为比 `SafetySignature(摘要, 读数)` 与此刻签名。读数为空时不算持有（会补报一条，偏保守）。
- #399/#401 的行为不变：原有 9 条用例（漫游内容不变不补报、服务端忘了安全状态时单凭握手快照就绪、漫游期间读数变化、握手后读数又变、握手后多出原因码）照旧全绿。
- 变异（`mutations-handshake.txt`，每个都用 `--no-incremental` 全量重建，并核对被测程序集写入时间晚于变异写入时间）：

| 变异 | 文件 | 结果 |
| --- | --- | --- |
| H1 握手后一律补报（业务层判握手从不持有） | 业务 | 杀：#399 的 `ARoamWithNothingChanged…`、`AServerThatForgotTheSafetyState…`×2 |
| H2 客户端握手记录不带读数（等于从不持有） | 客户端 | 杀：同上 3 条 |
| H3 握手不论内容一律持有 | 业务 | 杀：#401 的 5 条与新用例 2 条 |
| H4 只比摘要（逐仓读数取此刻的） | 业务 | 杀：新用例 2 条 |
| R6 复验：会话中途快照确认后一律补报 | 业务 | 杀：守护项（这次全量重建，程序集时间已核对） |
