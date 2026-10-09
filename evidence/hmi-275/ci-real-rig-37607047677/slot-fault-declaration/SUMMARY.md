# L2 场景证据：real-onboard-slot-fault-declaration

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261007T102640629Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `e53d1dfc12560a08cf0befba9120efcc170ace72` |
| expectedActionOverdueThreshold | `00:00:20` |
| onboardHmiCommit | `f45c3b38e218b54dfdb9e1ad7b0983c4642f1236` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `3.0.0` |
| protocolReleaseIdentity.tag | `protocol-v3.0.0` |
| protocolReleaseIdentity.commit | `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c` |
| protocolReleaseIdentity.protocolVersion | `4` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e` |
| protocolReleaseIdentity.schemaBundleSha256 | `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43` |
| protocolReleaseIdentity.vectorsSha256 | `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-37607047677-1\_stage\l2-20261007T102640629Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 判定被受理并已下发到车：202、PENDING、sentToVehicle，demandId 与尝试是这次装货的 | PASS | `202 / PENDING / True / 655fe22a-d2fe-42ec-940e-80043493d879 / e5f6e911-454f-d65a-8b5c-16897f6eb873` | `202 / PENDING / True / 655fe22a-d2fe-42ec-940e-80043493d879 / e5f6e911-454f-d65a-8b5c-16897f6eb873` |
| 线上 SlotFaultDeclarationResult：APPLIED、同一个尝试、problem 为空，服务端 DurableAck | PASS | `APPLIED / e5f6e911-454f-d65a-8b5c-16897f6eb873 / problem null / DurableAck` | `APPLIED / e5f6e911-454f-d65a-8b5c-16897f6eb873 / problem null / DurableAck` |
| OperationResult：overallOutcome UNKNOWN；1 号仓 UNKNOWN 且 reasonCodes 含 SLOT_FAULT_DECLARED；其余目标仓 NOT_STARTED | PASS | `UNKNOWN / slot 1 UNKNOWN [SLOT_FAULT_DECLARED] / 其余 NOT_STARTED` | `UNKNOWN / {"demandId":"655fe22a-d2fe-42ec-940e-80043493d879","slotOperationAttemptId":"e5f6e911-454f-d65a-8b5c-16897f6eb873","operationType":"LOAD","overallOutcome":"UNKNOWN","slotResults":[{"slotNo":1,"outcome":"UNKNOWN","finalPhysicalState":"EMPTY","lockState":"UNLOCKED","unlockOutputState":"RESET","reasonCodes":["SLOT_FAULT_DECLARED"]}],"observedAt":"2026-10-07T18:27:34.6833513+08:00","journalCheckpoint":"ACTIVE_UNLOCK_SET","resultContentSha256":"5c2b7e810d538e72057b6920ecb270f6db307b6e749023f67579930e5054f502"}` |
| 旅程转 Blocked，原因 LOAD_RESULT_REQUIRES_RECOVERY | PASS | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` |
| 装货仓位操作 RecoveryRequired（既有 UNKNOWN 结算） | PASS | `RecoveryRequired` | `RecoveryRequired` |
| 判定记录 APPLIED，审计带判定时车载端最近上报的读数与观测时刻 | PASS | `APPLIED / slot 1 / e5f6e911-454f-d65a-8b5c-16897f6eb873 / readings with observedAt` | `APPLIED / slot 1 / e5f6e911-454f-d65a-8b5c-16897f6eb873 / {"observedAt":"2026-10-07T10:27:33.6756265+00:00","safetyStateVersion":6,"lockState":"UNLOCKED","physicalState":"EMPTY","unlockOutputState":"RESET","changedSinceObserved":false}` |
| 判定之后零开锁：空关后 10 秒门仍关着，这次装货的 UNLOCKING 进度条数与判定前相同 | PASS | `CLOSED/* / UNLOCKING 1` | `CLOSED/EMPTY/1/0 / UNLOCKING 1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
