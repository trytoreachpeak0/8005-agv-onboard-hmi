# `ONBOARD_HMI_G2`：`9748c418` 上十片全部 **`PASS`**（已发布的 `protocol-v2.0.0`）

批次 5 出口（8005-agv-control-server#90）第 3 步。**它取代 `../20260914-protocol-v1.0.0-8d19fee/`，作为这十片车载端这一半的现行证据。**
那一份绑的是 `protocol-v1.0.0`，按规格第 6.5 节在 `protocol-v2.0.0` 发布后不再是现行证据，原样保留、一字未改。

按用户 2026-09-17 决定，门禁不再逐次询问；本轮在调度会话放行的真装置时段内跑。

## 结论

| 片 | `status` | selected／recorded | build／test／format | 出站 schema 校验行数 | 违约 | 与 `8d19fee` 相比（测试名去重） |
| --- | --- | --- | --- | --- | --- | --- |
| `FP-IS-00` | **`PASS`** | 17／17 | 0／0／0 | 322 | 0 | 12 → 18 |
| `FP-IS-01` | **`PASS`** | 5／5 | 0／0／0 | 62 | 0 | 5 → 5 |
| `FP-IS-02` | **`PASS`** | 59／59 | 0／0／0 | 801 | 0 | 6 → 51 |
| `FP-IS-03` | **`PASS`** | 39／39 | 0／0／0 | 265 | 0 | 11 → 35 |
| `FP-IS-04` | **`PASS`** | 7／7 | 0／0／0 | 无（见下） | — | 3 → 6 |
| `FP-IS-05` | **`PASS`** | 7／7 | 0／0／0 | 138 | 0 | 5 → 7 |
| `FP-IS-06` | **`PASS`** | 9／9 | 0／0／0 | 88 | 0 | 8 → 9 |
| `FP-IS-07` | **`PASS`** | 81／81 | 0／0／0 | 812 | 0 | 22 → 72 |
| `FP-IS-14` | **`PASS`** | 2／2 | 0／0／0 | 28 | 0 | 3 → 3 |
| `FP-IS-15` | **`PASS`** | 4／4 | 0／0／0 | 79 | 0 | 4 → 5 |

每片的 `protocol-g1.log` 是协议仓 G1 在发布态的输出，十片都是 `PASS`。

**出站 schema 校验**是 onboard-hmi#74（批次5-25）加的门禁：`WireToGateG2Tests` 经协议序列化器发出的每一行出站报文，按 `protocol-v2.0.0`
的 schema 逐条校验。九片 `linesInViolation` 与 `linesInKnownViolation` 都是 0。

**`FP-IS-04` 的 `schemaConformance` 为 `null`，这是脚本的约定值，不是校验没挂上。**`scripts/run-w2g-g2.ps1` 只在该片选中了
`WireToGateG2Tests` 的测试时要求出站校验结果（选中了却没产出覆盖文件会直接判失败）。`FP-IS-04` 选中的 7 个测试都是
`WireToGateSlotOperationExecutorTests` 与 `OnboardControllerTests` 的执行器单元测试（卸货闭环、一次一扇门的开锁前校验），不发任何协议报文，
所以没有可校验的行。

十份 `gate-result.json` 的身份逐字段一致：

| 字段 | 值 |
| --- | --- |
| `implementationCommit` | `9748c4187e74aeb46cf95557e8f2430e8fe2abf3`（`w2g/fp-v2-impl` 顶端，onboard-hmi PR #111 的合入提交） |
| `implementationBranch` | `w2g/b5-36-g2-evidence`（从 `9748c418` 开的证据分支，跑时尚无提交） |
| `protocolTag` | `protocol-v2.0.0` |
| `protocolRepositoryCommit` | `86575456c847041515b7b75e8851a00e0d939804` |
| `protocolManifestSha256` | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| `protocolApprovalStatus` | **`APPROVED_RELEASE`** |

成对写法：`(AGV_FULL_PRODUCT, 3)`，发布 `2.0.0`。身份比较一律用完整 `ProtocolReleaseIdentity`。

## 测试条数的变化

按 `.trx` 测试名逐片对比 `8d19fee` 那一轮，只有一条旧名字不再出现：`WireToGateG2Tests.ReconnectDuringRecoveryRebindsDurableReportWithoutUnlockSideEffects`
（`FP-IS-00`、`FP-IS-05`）。它由 `e11c292`（onboard-hmi#69，移植 MVP 线 `3ecb490`＋`a56a59d`）有意改名为
`ReconnectDuringRecoverySupersedesInterruptedReportWithFreshHandshake`，断言随行为改为「重连后新的恢复状态报告取代被中断的那份」，本轮两片都跑了它。
其余增加的都是批次 5 车载端各票（onboard-hmi#67、#69～#79、#106、#107、#109）新增的测试。

## 装置

在 `C:\Users\szy\Desktop\8005-workspace-v2\worktrees\b5-36-8005-agv-onboard-hmi`（`9748c418`，干净）里直接跑，`-ProtocolRoot` 指向协议克隆
`repos\8005-agv-protocol`（`86575456`），`-EvidenceRoot` 放在工作树外，十片依次调用 `scripts/run-w2g-g2.ps1 -Slice <id>`，
跑完两个工作树 `git status` 都干净，再按原目录形状复制到这里。每片的控制台输出在 `<id>.console.log`。单片 48～151 秒，合计约 17 分钟。

## 未在本轮证明的

- `ONBOARD_HMI_G2` 只证车载端这一半；服务端的 `CONTROL_SERVER_G2` 与联合 G3 在服务端仓 `8005-agv-control-server` 的 `evidence/g2/20260918-protocol-v2.0.0-06b65688/` 与 `evidence/g3/`。
- 出站门禁只检查出站报文；入站普查没有做（program#61 Q4 的范围）。
- 真实车辆、现场 IO、光幕极性、锁反馈时序与机械弹开不在本目录的主张范围内。
