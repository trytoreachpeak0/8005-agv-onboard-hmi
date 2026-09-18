# `ONBOARD_HMI_G2`：`29fbf65e` 上十片全部 **`PASS`**（已发布的 `protocol-v2.0.0`）

批次 5 出口（8005-agv-control-server#90）修复后的重跑。**它取代 `../20260918-protocol-v2.0.0-9748c418/`，作为这十片车载端这一半的现行证据。**
那一份绑的是 `9748c418`：它的十片同样全 `PASS`，但那个提交带着 onboard-hmi#112 的回归（重启进 `RecoveryRequired` 后四个管理员恢复入口不出现，G2 看不到），
修复（PR #114）改了产品代码，按「修好后从头重跑、不拼接」在新提交上重出。旧目录原样保留、一字未改。

## 结论

| 片 | `status` | selected／recorded | build／test／format | 出站 schema 校验行数 | 违约 |
| --- | --- | --- | --- | --- | --- |
| `FP-IS-00` | **`PASS`** | 17／17 | 0／0／0 | 324 | 0 |
| `FP-IS-01` | **`PASS`** | 5／5 | 0／0／0 | 62 | 0 |
| `FP-IS-02` | **`PASS`** | 59／59 | 0／0／0 | 801 | 0 |
| `FP-IS-03` | **`PASS`** | 39／39 | 0／0／0 | 265 | 0 |
| `FP-IS-04` | **`PASS`** | 7／7 | 0／0／0 | 无 | — |
| `FP-IS-05` | **`PASS`** | 7／7 | 0／0／0 | 138 | 0 |
| `FP-IS-06` | **`PASS`** | 9／9 | 0／0／0 | 88 | 0 |
| `FP-IS-07` | **`PASS`** | 81／81 | 0／0／0 | 810 | 0 |
| `FP-IS-14` | **`PASS`** | 2／2 | 0／0／0 | 28 | 0 |
| `FP-IS-15` | **`PASS`** | 4／4 | 0／0／0 | 79 | 0 |

各片选中的测试数与 `9748c418` 那一轮逐片相同。PR #114 新增的 `RecoveryEntryNotificationViewModelTests` 不带向量 trait，不进任何切片（纯界面呈现，
在全量测试里跑）。九片出站校验共 2595 行零违约；`FP-IS-04` 选中的是执行器单元测试、不发协议报文，`schemaConformance` 按 `run-w2g-g2.ps1` 的约定为 `null`。

十份 `gate-result.json` 的身份逐字段一致：`implementationCommit` `29fbf65e0b4d58c80849d5e6d0e44f40903c411e`（`w2g/fp-v2-impl` 顶端，PR #114 的合入提交），
`protocolTag` `protocol-v2.0.0`，`protocolRepositoryCommit` `86575456c847041515b7b75e8851a00e0d939804`，`protocolManifestSha256`
`4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7`，`protocolApprovalStatus` **`APPROVED_RELEASE`**。成对写法 `(AGV_FULL_PRODUCT, 3)`。

## 装置

在 detached 于 `29fbf65e` 的临时工作树里跑（干净），`-ProtocolRoot` 指向协议克隆 `repos\8005-agv-protocol`（`86575456`），`-EvidenceRoot` 放在工作树外，
十片依次经 `Invoke-HeavyLocal.ps1 -Ticket cs#90` 调用 `scripts/run-w2g-g2.ps1 -Slice <id>`，跑完两个工作树都干净，再按原目录形状复制到这里。单片 42～117 秒。

## 未在本轮证明的

- 只证车载端这一半；服务端 G2 与联合 G3 在服务端仓 `8005-agv-control-server`。
- 恢复入口在真窗口里出现与否不在 G2 的覆盖面上：它由服务端真装置 L2 `real-onboard-compensate-then-reconnect` 与 journey G3 的恢复场景验证（本轮同一绑定上均已转绿）。
- 真实车辆、现场 IO、光幕极性、锁反馈时序与机械弹开不在本目录的主张范围内。
