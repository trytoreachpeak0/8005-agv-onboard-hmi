# `ONBOARD_HMI_G2`：`372186f` 上十片全部 **`PASS`**（已发布的 `protocol-v1.0.0`）

**它取代 `../20260914-protocol-v1.0.0-8d19fee/`，作为这十片车载端这一半的现行证据。**那一份绑的是 `8d19fee`，原样保留、一字未改。

## 为什么要重跑

`372186f` 把 MVP 线的 `ab346ed`（「恢复请求每次发送都用新 messageId，被拒过的 attempt 能再开恢复会话」）移植到 v2：
恢复会话请求、恢复动作提交与补偿清空申请每次发送都用新的 messageId，不再复用日志里存着的旧 id。起因是服务端仓
control-server#36 的真装置 L2 `real-onboard-restart-with-open-recovery-session` 在 `b960108` 上红（`C:\g3dbg\ros-001`）：
「申请恢复」被拒后，重启的车按「补偿清空」复用了被拒动作的 messageId，服务端判内容冲突掐连接。移植之后同一场景
8/8 PASS（`C:\g3dbg\ros-003`，服务端 `c261e8e6` 加一处场景判据修正、车载端 `372186f`）。
产品代码改动落在全仓构建里，所以十片都随新提交重出证。

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
| `FP-IS-07` | **`PASS`** | 25 | 25 | 0／0／0 | `PASS` |
| `FP-IS-14` | **`PASS`** | 2 | 2 | 0／0／0 | `PASS` |
| `FP-IS-15` | **`PASS`** | 3 | 3 | 0／0／0 | `PASS` |

十份身份逐字段一致：`hmi.commit` 为 `372186f`（分支 `w2g/b3-on-v2`），每份 `workingTreeStatus` 为空；
`protocol.headCommit` / `tagCommit` 为 `9f22db8`，`protocol-v1.0.0` tag 存在；`protocol.approvalStatus` 为 **`APPROVED_RELEASE`**。

与 `8d19fee` 那一轮相比，只有 `FP-IS-07` 的测试条数变了：23 → 25，多出的是 `372186f` 移植的两条
`RecoveryVectorG2Tests.RecoveryCanBeRequestedAgainAfterTheServerRefusedTheSession` 与
`RecoveryVectorG2Tests.ARequestIdTheServerAlreadyHoldsIsNotSentAgain`（`CV-EXCEPTION-COMPENSATE`）。其余九片逐片相同。

## 装置

与上一轮相同：在 `hmi-b3` 工作树里直接跑（干净），`-ProtocolRoot` 指向 `C:\Users\szy\8005-b3\proto-gov`（`9f22db8`），
十片依次调用 `scripts/run-w2g-g2.ps1 -Slice`。`-EvidenceRoot` 这一轮指到仓外（`C:\g3dbg\g2-372186f\<片>`），免得前几片写出的
证据让后几片的 `workingTreeStatus` 不再为空；跑完按原目录形状复制到这里。

## 未在本轮证明的

- `ONBOARD_HMI_G2` 只证车载端这一半；服务端的 `CONTROL_SERVER_G2` 与联合 G3 在服务端仓 `fp/b2-close`。
- 界面按钮与接线没有单元测试可覆盖，由服务端 L2／G3 在真车载端上用 UI Automation 驱动核对。
- MVP 线的恢复会话快照确认规则（`a696add`、`86fe0a4`）没有随本提交移植，v2 车载端仍不回恢复会话快照的
  `SnapshotAppliedAck`；本目录不对它作主张。
- 发布批准本身、真实车辆与现场 IO 不在本目录的主张范围内。