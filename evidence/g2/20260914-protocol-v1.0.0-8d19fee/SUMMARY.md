# `ONBOARD_HMI_G2`：`8d19fee` 上十片全部 **`PASS`**（已发布的 `protocol-v1.0.0`）

**它取代 `../20260914-protocol-v1.0.0-3fb8a6e/`，作为这十片车载端这一半的现行证据。**那一份绑的是 `3fb8a6e`，原样保留、一字未改。

## 为什么要重跑

`f14f8af` 给 `FP-IS-07` 的两条向量补了操作员入口（「强制机械恢复」「充电后返回服务」按钮与手动充电返回服务的业务方法），
说明见 `docs/W2G_FP_IS_07_OPERATOR_ENTRIES.md`。`8d19fee` 在它之上只补了那份说明的 L2 调试结果，产品代码与 `f14f8af` 相同。
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
| `FP-IS-07` | **`PASS`** | 23 | 23 | 0／0／0 | `PASS` |
| `FP-IS-14` | **`PASS`** | 2 | 2 | 0／0／0 | `PASS` |
| `FP-IS-15` | **`PASS`** | 3 | 3 | 0／0／0 | `PASS` |

十份身份逐字段一致：`hmi.commit` 为 `8d19fee`（分支 `w2g/b3-on-v2`），每份 `workingTreeStatus` 为空；
`protocol.headCommit` / `tagCommit` 为 `9f22db8`，`protocol-v1.0.0` tag 存在；`protocol.approvalStatus` 为 **`APPROVED_RELEASE`**。

与 `3fb8a6e` 那一轮相比，只有 `FP-IS-07` 的测试条数变了：20 → 23，多出的是
`TheManualChargingReturnEntrySendsTheAdministratorAndLeavesTheHoldToTheServer`（受理、拒绝两例）与
`TheManualChargingReturnEntryIsNotOfferedWithoutAVerifiedAdministrator`。其余九片逐片相同。

## 装置

与上一轮相同：在 `hmi-b3` 工作树里直接跑（干净），`-ProtocolRoot` 指向 `C:\Users\szy\8005-b3\proto-gov`（`9f22db8`），
十片依次调用 `scripts/run-w2g-g2.ps1 -Slice`，跑完按原目录形状复制到这里。

## 未在本轮证明的

- `ONBOARD_HMI_G2` 只证车载端这一半；服务端的 `CONTROL_SERVER_G2` 与联合 G3 在服务端仓 `fp/b2-close`。
- 界面按钮与接线没有单元测试可覆盖，由服务端 G3 `FP-IS-07` 在真车载端上用 UI Automation 驱动核对。
- 发布批准本身、真实车辆与现场 IO 不在本目录的主张范围内。