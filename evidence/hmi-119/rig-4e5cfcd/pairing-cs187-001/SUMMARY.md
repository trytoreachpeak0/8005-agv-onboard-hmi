# L2 场景证据：hmi119-resume-rejected-pairing

结论：**FAIL**

失败原因：Timed out after 90s waiting for: a new recovery session opened for the same vehicle. Last observed: (nothing)

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T075705543Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-6` |
| controlServerCommit | `aa17b05938213626d6a0634abb57fb904287ad85` |
| onboardHmiCommit | `4e5cfcdb5aa5b30ef203cf06efb96f7438bd4a6e` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260919T075705543Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 重启进入 RecoveryRequired 后四个管理员恢复入口都出现 | PASS | `申请恢复=True, 补偿清空=True, 故障交接=True, 强制机械恢复=True` | `申请恢复=True, 补偿清空=True, 故障交接=True, 强制机械恢复=True` |
| 车回 SlotOperationCommandRejected：correlationId = 续行命令 messageId，attempt 正确，原因码 LOCK_NOT_CLOSED（已注册） | PASS | `correlationId 3ddbcb61-736f-ec53-b1b5-f42c85980d7d / c2d3965a-b061-465a-9f46-a2edeeed637c / LOCK_NOT_CLOSED` | `correlationId 3ddbcb61-736f-ec53-b1b5-f42c85980d7d / c2d3965a-b061-465a-9f46-a2edeeed637c / LOCK_NOT_CLOSED / 服务端应答 DurableAck` |
| 续行被挡之后没有任何开锁：UNLOCKING 进度条数不变，装载仓输出复位、门仍关着 | PASS | `UNLOCKING 1 不变 / CLOSED / 输出 0` | `UNLOCKING 1 → 1 / CLOSED / 输出 0` |
| 服务端收口：续行工作流 RecoveryRequired，恢复会话 CLOSED 并带原因码 | PASS | `workflow RecoveryRequired / session CLOSED` | `workflow RecoveryRequired / session {"ExceptionRecoverySessionId":"6f1f8b83-a1bd-6752-8e2d-5a9620b74760","RequestId":"1fe41410-7bbb-48e3-b44c-a2b2f9c69104","RequestContentHash":"f89d4e9ef4b3c49d8d7e3e30933f275a27396c4269dda3a5bbf66b32614c23df","AgvId":"AGV-L2-001","EventId":"1fe41410-7bbb-48e3-b44c-a2b2f9c69104","DemandId":"4877ae65-4acb-4742-bfc1-2d850c3e2dd1","SlotsJson":"[1]","AdministratorId":"L2-OPERATOR","AdministratorRole":"MAINTENANCE_ADMINISTRATOR","Reason":"现场维修完成，申请恢复原仓位操作。","State":"CLOSED","Revision":3,"SelectedAction":"RESUME_AFTER_REPAIR","ForcedRecoveryGeneration":0,"OpenedAt":"2026-09-19 07:57:54.3149637+00:00","UpdatedAt":"2026-09-19 07:57:54.5845019+00:00"}` |
| CLOSED 的 ExceptionRecoverySessionSnapshot 下发到车并被车确认 | PASS | `CLOSED snapshot acknowledged` | `messageId 0ef1be54-cfff-fa5c-be3b-1eff48841957 acknowledged` |
| 需求与旅程仍阻断：装载仍 RecoveryRequired，旅程仍 Blocked | PASS | `RecoveryRequired / Blocked` | `RecoveryRequired / Blocked` |
| 会话关闭后车载端恢复入口重新出现（补偿清空） | PASS | `offered` | `True` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
