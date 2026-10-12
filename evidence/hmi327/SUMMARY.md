# hmi#327 证据：agv02 约每 4 秒重连一次的定因与修复

## 结论

2026-10-12 REQ-0178 现场，agv02 车载端（`w2g/fp-v2-impl` b2f602ec）在车到等待点 216 之后，约每 4.2 秒整条断开、重连一次，服务端会话代数 106 秒涨了 25 代。

原因：清桩车到等待点时，服务端（control-server 020138c9）暂存的收尾计划快照 `UpcomingStopPlanSnapshot` 只有一条等待点腿，`sequence` 是 2。车载端 `ValidateUpcomingStopPlan` 要求序号从 1 起连续，于是判 `PROTOCOL_SCHEMA_INVALID`，回 `ProtocolProblem` 后接收循环退出、断开连接。服务端没收到确认，下一次会话又重放这张快照，形成循环，直到出现新的计划快照把它取代、或者服务停掉为止。

协议 schema 对 `sequence` 只规定 1～9 的整数（`vendor/8005-agv-protocol/schemas/messages/UpcomingStopPlanSnapshot.schema.json`），不要求从 1 起、也不要求连续，所以是车载端的校验比 schema 窄。本 PR 把它放宽到与 schema 一样；服务端重新编号见 control-server#664。

## 现场材料（全部只读取回，原件在工作区 `evidence/batch89-gaps/hmi327/`）

| 文件 | 内容 |
| --- | --- |
| `01-outbox-two-rejected-rows.txt` | 被拒的两行发件箱记录全文：`77b45b7d…`（planRevision 50，07:54:25Z）与 `3cb1f8f1…`（planRevision 62，12:14:08Z），都是一条 `sequence:2` 的 `WAITING_POINT` 腿，`AcknowledgedAt`、`FencedAt` 都为空 |
| `02-outbox-agv02-snapshots-20261012.txt` | agv02 当天的计划与业务状态快照摘要（原件 118 行，只留 10-12 07:00Z 之后） |
| `03-v2-log-1552-1601.txt` | V2 日志 15:52～16:01：1101 拒收 94 次，每次后面跟一条 1003 |
| `04-v2-log-2012-closeout.txt` | V2 日志 20:12～收工：1101 拒收 50 次；20:27:55 人工确认清桩（2266）收尾之后没有循环 |
| `05-agv02-onboard-log-excerpt.txt` | agv02 车载端日志中会话相关三个 Source 的行，只留循环开始与结束的几段：15:52～15:55、16:07:30～16:10、19:38～19:39:30、20:13:50～20:14:40、20:17:40～20:22 |

原件 05 号全天共有 347 行「接收循环失败收尾」：335 行是 `InvalidDataException：PROTOCOL_SCHEMA_INVALID`，12 行是 `EndOfStreamException`（服务端重启与部署）。按分钟数，循环在三段里每分钟 14 次左右：15:54～16:08（V2 停服结束）、19:33～19:38（19:38:41 新计划 rev51 取代旧行）、20:14～20:18（20:18:08 新计划 rev63 取代）。

## 读到的与推的

- 读到的：服务端只在 `SessionHello` 时分配新代数（control-server `OnboardMessageProcessor.cs`），所以代数涨 25 就是握手了 25 次。
- 读到的：15:52:31 时代数还是 28（`03` 首行附近），到点（2305，15:54:25）之前没有循环。
- 读到的：服务端收尾计划用 `JourneyPlanBuilder.IdleReturnLeg(runtime, move, ...)`，沿用清桩移动停靠的序号 `charger.Sequence + attempt`（control-server `ClearanceMoveShape.cs:85`、`JourneyClosure.cs:263-269`）。
- 推的：第二次及以后的清桩（attempt ≥ 2），途中计划会是 `[1, 3]`，同样会被拒。
- 推的：同一毫秒暂存、同样未确认的两行业务状态快照（`66196451…`、`90ef3b7d…`）排在计划之后，是被同一次断开连带、没被车载端读到；修后会按顺序被接收并确认。

## 修复与验证

改动只在 `ValidateUpcomingStopPlan`：去掉「排序后必须等于 1..n」，每条腿加上 `sequence` 在 1～9 之间的检查；去重保留，线上顺序照旧不检查。信封反序列化不按 schema 查 payload，所以范围必须在这里显式检查。

车载端没有任何地方把 `sequence` 当下标用：全部用法是两处 `OrderBy`（`WireToGateJourney.cs`、`MainViewModel.cs`）、一处原样映射（`WireToGateSessionClient.cs`）、一处界面自动化状态文本（`JourneyPlanLegRow.ItemStatus`）。

| 输出 | 结果 |
| --- | --- |
| `tests/red-before-fix.txt` | 修前：应接受的 `[2]`、`[1,3]`、`[3,1]`、`[9]` 4 条红（`PROTOCOL_SCHEMA_INVALID`），应拒收的 7 条绿 |
| `tests/green-after-fix.txt` | 修后定向 14 条全绿（含现场原始载荷用例与已有的 9 腿、10 腿两条） |
| `tests/mutation-M1-no-lower-bound.txt` | 去掉下限：`[0]`、`[0,1]`、`[-1,2]` 变红 |
| `tests/mutation-M2-no-upper-bound.txt` | 去掉上限：`[10]`、`[1,10]` 变红 |
| `tests/mutation-M3-no-distinct.txt` | 去掉去重：`[1,1]`、`[2,4,2]` 变红 |
| `tests/mutation-M4-old-contiguous-rule.txt` | 恢复旧规则：应接受的 4 条与现场原始载荷用例变红 |
| `tests/green-after-restore.txt` | 还原后 `InboundPayloadSchemaBoundaryTests` 与 `ArchitectureTests` 共 330 条全绿 |

每轮变异都核对过构建 0 错误、退出码 1；修后与还原后退出码 0。
