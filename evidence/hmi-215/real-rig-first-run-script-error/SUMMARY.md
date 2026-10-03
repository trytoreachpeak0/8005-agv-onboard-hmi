# L2 场景证据：real-onboard-slot-fault-declaration

结论：**FAIL**

失败原因：在此对象上找不到属性“PayloadJson”。请验证该属性是否存在。

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261003T062420216Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `3cf62fecd28c402fe9fc54a144b20a85583edd4b` |
| expectedActionOverdueThreshold | `00:00:20` |
| onboardHmiCommit | `b9cd47f74e456a75093c9aa97083db0e7795005e` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-37102766873-1\_stage\l2-20261003T062420216Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 判定被受理并已下发到车：202、PENDING、sentToVehicle，demandId 与尝试是这次装货的 | PASS | `202 / PENDING / True / d2f34870-7f23-4a43-9481-9fad96584e86 / d8f78cee-2c2a-125e-b963-63cb503fede1` | `202 / PENDING / True / d2f34870-7f23-4a43-9481-9fad96584e86 / d8f78cee-2c2a-125e-b963-63cb503fede1` |
| 线上 SlotFaultDeclarationResult：APPLIED、同一个尝试、problem 为空，服务端 DurableAck | PASS | `APPLIED / d8f78cee-2c2a-125e-b963-63cb503fede1 / problem null / DurableAck` | `APPLIED / d8f78cee-2c2a-125e-b963-63cb503fede1 / problem {"declarationId":"f92adaa1-ab4e-4795-94a5-913f4a7ff74c","slotOperationAttemptId":"d8f78cee-2c2a-125e-b963-63cb503fede1","outcome":"APPLIED","problem":null} / DurableAck` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
