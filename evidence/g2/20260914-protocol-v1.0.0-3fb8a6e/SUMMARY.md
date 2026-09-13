# `ONBOARD_HMI_G2`：`3fb8a6e` 上十片全部 **`PASS`**（已发布的 `protocol-v1.0.0`）

**它取代 `../20260913-protocol-v1.0.0-04d0088/`，作为这十片车载端这一半的现行证据。**那一份绑的是 `04d0088`，原样保留、一字未改。

## 为什么要重跑

`3fb8a6e` 补上了 `CV-OPERATION-RESULT-UNKNOWN-RECONCILE` 的车载端一半：没能让操作结清的结果记为待结清，
之后每次建会话都在 `RecoveryStateReport.pendingResults` 里列出，报告被确认后按新会话代号原样补发，直到操作结清。
说明见 `docs/W2G_UNKNOWN_RESULT_RECONCILE.md`；服务端一半在 `fp/b2-close@e62b136d`。产品代码改动落在全仓构建里，所以十片都随新提交重出证。

## 结论

| 片 | `status` | selected | recorded | build／test／format | `protocol.g1Status` |
| --- | --- | --- | --- | --- | --- |
| `FP-IS-00` | **`PASS`** | 11 | 11 | 0／0／0 | `PASS` |
| `FP-IS-01` | **`PASS`** | 5 | 5 | 0／0／0 | `PASS` |
| `FP-IS-02` | **`PASS`** | 6 | 6 | 0／0／0 | `PASS` |
| `FP-IS-03` | **`PASS`** | 12 | 12 | 0／0／0 | `PASS` |
| `FP-IS-04` | **`PASS`** | 2 | 2 | 0／0／0 | `PASS` |
| `FP-IS-05` | **`PASS`** | 5 | 5 | 0／0／0 | `PASS` |
| `FP-IS-06` | **`PASS`** | 8 | 8 | 0／0／0 | `PASS` |
| `FP-IS-07` | **`PASS`** | 20 | 20 | 0／0／0 | `PASS` |
| `FP-IS-14` | **`PASS`** | 2 | 2 | 0／0／0 | `PASS` |
| `FP-IS-15` | **`PASS`** | 3 | 3 | 0／0／0 | `PASS` |

十份身份逐字段一致：

- `hmi.commit`：`3fb8a6ebe2caf2d54caffa839fdff65b7a4fa7e2`（分支 `w2g/b3-on-v2`），每份 `summary.json` 的 `workingTreeStatus` 为空
- `protocol.headCommit` / `tagCommit`：`9f22db825d52ad86c1d803bd0c1925dcc58d6793`，`protocol-v1.0.0` tag 存在且指向它
- `protocol.g1CandidateManifestSha256`：`a0e1deed…`
- `protocol.approvalStatus`：**`APPROVED_RELEASE`**

与 `04d0088` 那一轮相比，只有两片的测试条数变了，多出的都是新测试
`AnUnknownResultIsReportedAsPendingAfterARestartAndReplayedOnceTheReportIsAcknowledged`：`FP-IS-03` 11 → 12，`FP-IS-07` 19 → 20
（向量同属这两片，测试两者都标）。其余八片逐片相同。

## 装置

与 `04d0088` 那一轮相同：在 `hmi-b3` 工作树里直接跑（干净），`-ProtocolRoot` 指向 `C:\Users\szy\8005-b3\proto-gov`
（`9f22db8`，带 `protocol-v1.0.0` tag），十片依次调用 `scripts/run-w2g-g2.ps1 -Slice`，跑完按原目录形状复制到这里。

## 未在本轮证明的

- `ONBOARD_HMI_G2` 只证车载端这一半；服务端的 `CONTROL_SERVER_G2` 与联合 G3 在服务端仓 `fp/b2-close`。
- 发布批准本身不在本证据里复核。
- 真实车辆停稳信号、Modbus／锁／门／光幕、现场明文网络不在本目录的主张范围内；真车载端 + slots-simulator 上跨重启的完整顺序由服务端 G3 `FP-IS-03` 核对。
