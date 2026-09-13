# `ONBOARD_HMI_G2`：`8a2fed9` 上十片全部 **`PASS`**（已发布的 `protocol-v1.0.0`）

**它取代 `../20260912-protocol-v1.0.0-98f4e06/`，作为这十片车载端这一半的现行证据。**那一份绑的是 `98f4e06`，原样保留、一字未改。

## 为什么要重跑

`8a2fed9` 修了一处车载端缺陷：装载记完结果后恢复状态缓存不刷新，「修正装货」入口要等一次无关的会话事件才出现。
控制服务端 2026-09-13 按 `REQ-0237` / ADR-cross-0054 补上了装载后的离站等待，那段时间里的下一次事件就是离站本身，
入口因此总在修正窗口关掉之后才出现。说明见 `docs/W2G_CORRECTION_ENTRY_STALE_AFTER_LOAD.md`。

同一提交给 G2 的假 IO 加了 `SimulateOperatorLoad`（默认关闭），新增 `FP-IS-02` / `CV-LOAD-CORRECTION` 测试
`ACompletedLoadOffersTheCorrectionEntryAsSoonAsItsResultIsRecorded`。产品代码改动落在全仓构建里，所以十片都随新提交重出证。

## 结论

| 片 | `status` | selected | recorded | build／test／format | `protocol.g1Status` |
| --- | --- | --- | --- | --- | --- |
| `FP-IS-00` | **`PASS`** | 11 | 11 | 0／0／0 | `PASS` |
| `FP-IS-01` | **`PASS`** | 5 | 5 | 0／0／0 | `PASS` |
| `FP-IS-02` | **`PASS`** | 6 | 6 | 0／0／0 | `PASS` |
| `FP-IS-03` | **`PASS`** | 9 | 9 | 0／0／0 | `PASS` |
| `FP-IS-04` | **`PASS`** | 2 | 2 | 0／0／0 | `PASS` |
| `FP-IS-05` | **`PASS`** | 5 | 5 | 0／0／0 | `PASS` |
| `FP-IS-06` | **`PASS`** | 8 | 8 | 0／0／0 | `PASS` |
| `FP-IS-07` | **`PASS`** | 19 | 19 | 0／0／0 | `PASS` |
| `FP-IS-14` | **`PASS`** | 2 | 2 | 0／0／0 | `PASS` |
| `FP-IS-15` | **`PASS`** | 3 | 3 | 0／0／0 | `PASS` |

十份身份逐字段一致：

- `implementationCommit`：`8a2fed9a4eeb8f74524174e3626114b2ab7d0007`（分支 `w2g/b3-on-v2`），每份 `summary.json` 的 `workingTreeStatus` 为空
- `protocolRepositoryCommit`：`9f22db825d52ad86c1d803bd0c1925dcc58d6793`，`protocol-v1.0.0` tag 存在且指向它
- `protocolManifestSha256`：`a0e1deed…`
- `protocolApprovalStatus`：**`APPROVED_RELEASE`**

与 `98f4e06` 那一轮相比，只有 `FP-IS-02` 的测试条数变了：5 → 6，多出的就是上面那条新测试。其余九片逐片相同。

## 装置

在 `hmi-b3` 工作树里直接跑（`w2g/b3-on-v2@8a2fed9`，干净）。`-ProtocolRoot` 指向协议克隆 `C:\Users\szy\8005-b3\proto-gov`，
检出 `9f22db8`、带 `protocol-v1.0.0` tag。十片由一个 pwsh 循环依次调用 `scripts/run-w2g-g2.ps1 -Slice`，证据先写到默认根目录，
跑完按上一轮的目录形状原样复制到这里（证据里的路径都是相对的）。

## 未在本轮证明的

- `ONBOARD_HMI_G2` 只证车载端这一半；服务端的 `CONTROL_SERVER_G2` 与联合 G3 在服务端仓 `fp/b2-close`。
- 发布批准本身不在本证据里复核。
- 真实车辆停稳信号、Modbus／锁／门／光幕、现场明文网络不在本目录的主张范围内；真车载端 + slots-simulator 上的修正全流程由服务端 G3 `FP-IS-02` 核对。
