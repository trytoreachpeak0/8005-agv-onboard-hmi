# 车载端待决：同一 revision 的快照在重连后无法被接受

提交人：ControlServer 侧联调（2026-08-29）
本仓被观测版本：`OnboardHmi_MVP@84b7f3f66ff2f867b18121760f38e26e0bbd6fa5`
对端版本：`ControlServer_MVP@8caf7465b1096903d1f84d6d4788472b0c3c665a`
协议：`protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`

> 本文件是一份**只读联调发现的交接说明**，由用户单次授权写入本仓库。除本文件外未改动本仓
> 任何源码、测试、配置或其它文档。如何修改由本仓负责人决定。

## 结论先说

在真实双端联调中，取货到站后的旅程**无法推进**，服务端与车载端陷入自我维持的重连循环：
150 秒内会话重建 22 次，三条快照一条未被确认，旅程始终停在 `AwaitingPickupArrival`。

原因是一条跨端约定在协议里没有定义，而两端的实现取了互不相容的解释。

## 观测到的循环

```
[14:49:32 WRN] Onboard rejected VehicleBusinessStateSnapshot 2de51bf7-…:
               SNAPSHOT_REVISION_CONTENT_CONFLICT
[14:49:34 ERR] Journey runtime iteration failed closed
               System.IO.IOException: No recovered Onboard peer is connected.
```

服务端一侧同期计数：`ProtocolContentConflictException` 52 次、`IOException` 43 次。

## 机制

车载端把「整个信封的 SHA-256」当作同一 revision 的去重键：

- `src/SQCD.Agv.Contracts/WireToGateProtocol.cs:222`
  `ComputeContentSha256(envelope)` 对 **序列化后的完整信封** 取哈希，因此
  `sentAt`、`sessionGeneration` 等信封字段都计入。
- `src/SQCD.Agv.Infrastructure/WireToGateSessionClient.cs:1047-1076`
  `ApplyJourneyRevision` 以 `_journeyRevisions[messageType] = (revision, contentSha256)`
  记录；再次收到相同 `revision` 而 `contentSha256` 不同时，于 1069 行抛
  `SNAPSHOT_REVISION_CONTENT_CONFLICT`。
- `src/SQCD.Agv.Infrastructure/SqliteWireToGateJournal.cs:460` 是同一规则的持久化版本。

而按协议，`sessionGeneration` 在每次重连后**必须**前进。服务端对未被确认的快照要在新会话中
重发（否则对端永远收不到它错过的那条），重发时信封必然带上新的代次。

于是：**同一 revision 的快照，一旦经历过一次重连，整信封哈希必然改变，就再也不可能被接受。**
这不是时序竞争，是构造上的必然。

需要说明的是，服务端这边也做过一轮排查并修正了自己的一处问题：
`VehicleBusinessStateSnapshot` 的 payload 里原本嵌了每次发布现取的时钟，已改为取信封冻结的
`sentAt`（`ControlServer_MVP@8caf746`）。修正后 payload 内容确实稳定了，但循环依旧——因为
决定判定的是整信封哈希，而 `sentAt` 与 `sessionGeneration` 仍会随重连变化。

## 协议为什么帮不上忙

`protocol-v0.1.1` 的 `schemas/messages/SnapshotAppliedAck.schema.json` 把
`appliedContentSha256` 只声明为 `Sha256` 类型，**没有任何 description 说明它覆盖哪些字段**；
`docs/`、`integration-slices/` 中也没有相关约定。两端各自取了合理但不同的解释，谁都没有违约。

这已经是本轮联调遇到的第三个此类未定义字段（另两个是 `supportsBatchUnlock` 与两个快照的
发送节奏／新鲜度）。

## 需要本仓负责人决定的事

至少有两个方向，各有取舍，服务端不便替本仓选择：

1. **只对业务 payload 取哈希**，把 `sentAt`／`sessionGeneration` 等传输层字段排除在去重键之外。
   语义上更贴近「同一 revision 表示同一业务状态」，且重连后重发天然可接受。
2. **改用 `messageId` + `sessionGeneration` 作为去重键**，保留整信封哈希仅用于完整性校验。

若认为应由服务端承担，服务端可行的做法是**重发时递增 revision**；但那等于在业务状态未改变时
谎报新版本，会污染一个本来正确的不变量，因此服务端不建议，也不会单方面这么做。

无论选哪种，建议同时在协议仓为 `appliedContentSha256` 补上覆盖范围的正式定义，避免第四次
出现同类分歧。协议仓为审批门禁，需两名负责人批准。

## 本仓决定（2026-08-29）

OnboardHmi 采用方向 1，并明确拆分两个不同用途的哈希：

- 同一 revision 的业务一致性使用递归规范化后的 **payload SHA-256** 判断，排除
  `messageId`、`sentAt`、`sessionGeneration` 等传输字段；
- `SnapshotAppliedAck.appliedContentSha256` 继续返回当前收到的**完整信封 SHA-256**，保持与
  ControlServer 现有 outbox 确认逻辑兼容。

SQLite 不增加破坏性迁移：已持久化的原始 `PayloadJson` 是 revision 内容判断的权威来源，旧记录
中的整信封 `ContentSha256` 继续保留用于审计。这样已有 journal 升级后也不会因为哈希语义变化
产生一次新的假冲突。

协议仓对 `appliedContentSha256` 覆盖范围的正式说明仍需双方负责人单独审批；本仓不会在未批准时
单方面修改正式协议身份。

## 复现方式

服务端隔离运行（全新 SQLite 与 journal、临时端口 58105／58107）加真实 OnboardHmi 与
slots-simulator，令旅程推进到取货到站，使其发出
`VehicleBusinessStateSnapshot`／`CurrentStopWorklistSnapshot`／`UpcomingStopPlanSnapshot`
三条快照，随后触发一次重连即可稳定复现。

服务端侧完整分析见
`8005-agv-control-server` 的 `docs/defects/20260829-snapshot-replay-across-session-generations.md`。
