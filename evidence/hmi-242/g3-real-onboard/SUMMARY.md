# L2 场景证据：g3-unable-to-charge-field-confirmation

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261002T094825989Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `424e149516cbed5c255c09e3ada5590dd2dca613` |
| onboardHmiCommit | `8694d8fce1a54b42d35064470a0eeb3cd571f292` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `2.0.0` |
| protocolReleaseIdentity.tag | `protocol-v2.0.0` |
| protocolReleaseIdentity.commit | `86575456c847041515b7b75e8851a00e0d939804` |
| protocolReleaseIdentity.protocolVersion | `3` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| protocolReleaseIdentity.schemaBundleSha256 | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| protocolReleaseIdentity.vectorsSha256 | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261002T094825989Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车停在 211、单 HANG、开始充电的结果码不是 407802：旅程写 ORDER_HANG，没有任何暂停，周期仍 ACTIVE（系统没有自动确认，REQ-0175） | PASS | `ORDER_HANG \| (none) \| ACTIVE` | `ORDER_HANG \| (none) \| ACTIVE / ORDER_HANG \| (none) \| ACTIVE` |
| 车载端：充电用途、计划当前腿是 211 时「现场确认充不上」入口出现（「接不上充电」可按，没有不可用说明） | PASS | `offered=True \| notice=False` | `offered=True \| notice=False` |
| 车载端发 UnableToChargeFieldConfirmationRequested（L2-OPERATOR、计划里那条 CHARGER 腿的站点、CONNECTION_FAILED），服务端回 Result：CONFIRMED、problem 为空、chargingPolicyDecision=MANUAL_CHARGING_HOLD（名册只有 211、电量 25 低于最低余量 30） | PASS | `L2-OPERATOR / 充电点1 / CONNECTION_FAILED -> CONFIRMED / null / MANUAL_CHARGING_HOLD` | `L2-OPERATOR / 充电点1 / CONNECTION_FAILED -> CONFIRMED /  / MANUAL_CHARGING_HOLD` |
| 车载端界面结果一行 UnableToChargeStatus 报 CONFIRMED（已知红，等 onboard-hmi#242：用途转为 CLEARING_MAINTENANCE 时车载端清掉了结果） | PASS | `CONFIRMED` | `CONFIRMED` |
| Result 之后，业务状态 UNABLE_TO_CHARGE、CLEARING_MAINTENANCE 进发件箱并被车载端确认（VehicleBusinessStateSnapshot → SnapshotAppliedAck） | PASS | `after 10/02/2026 09:50:25 +00:00, acknowledged` | `10/02/2026 09:50:26 +00:00, acknowledged=True` |
| 一条 UNABLE_TO_CHARGE_CONFIRMED 的暂停（确认人 L2-OPERATOR、R-11、现场处置 CONNECTION_FAILED）；周期 UNABLE_TO_CHARGE／CLEARING；人工充电等待 UNABLE_TO_CHARGE_LOW_BATTERY；判定记下一行（DECIDE_CHARGING_POLICY_CENTRALLY、RECORD_FIELD_OBSERVATION） | PASS | `UNABLE_TO_CHARGE_CONFIRMED L2-OPERATOR/R-11/CONNECTION_FAILED \| UNABLE_TO_CHARGE/CLEARING \| UNABLE_TO_CHARGE_LOW_BATTERY \| 1 decided` | `UNABLE_TO_CHARGE_CONFIRMED L2-OPERATOR/R-11/CONNECTION_FAILED \| UNABLE_TO_CHARGE/CLEARING \| UNABLE_TO_CHARGE_LOW_BATTERY \| 1 decided` |
| 车保持原位：RIoT 上恰好一张充电单（duplicate-riot-order），没有任何订单命令，211 仍是这一趟的 | PASS | `1 orders \| 0 commands \| True` | `1 orders \| 0 commands \| True` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
