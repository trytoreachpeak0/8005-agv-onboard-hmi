# `ONBOARD_HMI_G2`：`04d0088` 上十片全部 **`PASS`**（已发布的 `protocol-v1.0.0`）

**它取代 `../20260913-protocol-v1.0.0-8a2fed9/`，作为这十片车载端这一半的现行证据。**那一份绑的是 `8a2fed9`，原样保留、一字未改。

## 为什么要重跑

`04d0088` 补上了 `CV-PREDEPARTURE-SAFETY-EXPIRES` 的车载端一半：收到询问的安全版本已低于本端被接受版本的出发前检查，
不作答，回 `ProtocolProblem(PREDEPARTURE_CHECK_EXPIRED)`，会话不断。说明见 `docs/W2G_PREDEPARTURE_CHECK_EXPIRED.md`；
服务端一半在 `fp/b2-close@6c252816`。产品代码改动落在全仓构建里，所以十片都随新提交重出证。

## 结论

| 片 | `status` | selected | recorded | build／test／format | `protocol.g1Status` |
| --- | --- | --- | --- | --- | --- |
| `FP-IS-00` | **`PASS`** | 11 | 11 | 0／0／0 | `PASS` |
| `FP-IS-01` | **`PASS`** | 5 | 5 | 0／0／0 | `PASS` |
| `FP-IS-02` | **`PASS`** | 6 | 6 | 0／0／0 | `PASS` |
| `FP-IS-03` | **`PASS`** | 11 | 11 | 0／0／0 | `PASS` |
| `FP-IS-04` | **`PASS`** | 2 | 2 | 0／0／0 | `PASS` |
| `FP-IS-05` | **`PASS`** | 5 | 5 | 0／0／0 | `PASS` |
| `FP-IS-06` | **`PASS`** | 8 | 8 | 0／0／0 | `PASS` |
| `FP-IS-07` | **`PASS`** | 19 | 19 | 0／0／0 | `PASS` |
| `FP-IS-14` | **`PASS`** | 2 | 2 | 0／0／0 | `PASS` |
| `FP-IS-15` | **`PASS`** | 3 | 3 | 0／0／0 | `PASS` |

十份身份逐字段一致：

- `implementationCommit`：`04d00881203c02a7b2740291bb8d532bba1b7fae`（分支 `w2g/b3-on-v2`），每份 `summary.json` 的 `workingTreeStatus` 为空
- `protocolRepositoryCommit`：`9f22db825d52ad86c1d803bd0c1925dcc58d6793`，`protocol-v1.0.0` tag 存在且指向它
- `protocolManifestSha256`：`a0e1deed…`
- `protocolApprovalStatus`：**`APPROVED_RELEASE`**

与 `8a2fed9` 那一轮相比，只有 `FP-IS-03` 的测试条数变了：9 → 11，多出的是新测试
`ACheckAskedAboutAnOlderSafetyStateIsRefusedAsExpiredWithoutEndingTheSession` 的两例。其余九片逐片相同。

## 装置

与 `8a2fed9` 那一轮相同：在 `hmi-b3` 工作树里直接跑（干净），`-ProtocolRoot` 指向 `C:\Users\szy\8005-b3\proto-gov`
（`9f22db8`，带 `protocol-v1.0.0` tag），十片依次调用 `scripts/run-w2g-g2.ps1 -Slice`，跑完按原目录形状复制到这里。

## 未在本轮证明的

- `ONBOARD_HMI_G2` 只证车载端这一半；服务端的 `CONTROL_SERVER_G2` 与联合 G3 在服务端仓 `fp/b2-close`。
- 发布批准本身不在本证据里复核。
- 真实车辆停稳信号、Modbus／锁／门／光幕、现场明文网络不在本目录的主张范围内；真车载端 + slots-simulator 上的完整顺序由服务端 G3 `FP-IS-03` 核对。
