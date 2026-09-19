# L2 场景证据：hmi119-resume-rejected-pairing

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T085721618Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-6` |
| controlServerCommit | `aa17b05938213626d6a0634abb57fb904287ad85` |
| onboardHmiCommit | `c97d51b50d1a1f2ee6b6c0a2304fff899cfa5b56` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260919T085721618Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 重启进入 RecoveryRequired 后四个管理员恢复入口都出现 | PASS | `申请恢复=True, 补偿清空=True, 故障交接=True, 强制机械恢复=True` | `申请恢复=True, 补偿清空=True, 故障交接=True, 强制机械恢复=True` |
| 车回 SlotOperationCommandRejected：correlationId = 续行命令 messageId，attempt 正确，原因码 LOCK_NOT_CLOSED（已注册） | PASS | `correlationId c3588e4c-67f8-3d54-aec9-59a49507856b / 74ccd53a-dd26-1855-8a28-5a4a29cb6fa0 / LOCK_NOT_CLOSED` | `correlationId c3588e4c-67f8-3d54-aec9-59a49507856b / 74ccd53a-dd26-1855-8a28-5a4a29cb6fa0 / LOCK_NOT_CLOSED / 服务端应答 DurableAck` |
| 续行被挡之后没有任何开锁：UNLOCKING 进度条数不变，装载仓输出复位、门仍关着 | PASS | `UNLOCKING 1 不变 / CLOSED / 输出 0` | `UNLOCKING 1 → 1 / CLOSED / 输出 0` |
| 服务端收口：续行工作流 RecoveryRequired，恢复会话 CLOSED 并带原因码 | PASS | `workflow RecoveryRequired / session CLOSED` | `workflow RecoveryRequired / session {"ExceptionRecoverySessionId":"91915f32-29c5-ba5f-98ff-57d0f4aac0f0","RequestId":"0da5d78a-9c6f-4cbe-9de5-873eb611c3c6","RequestContentHash":"7a5e333663904121f7e81918dd4161c110b43d4e40ceef5ec928124053a5e788","AgvId":"AGV-L2-001","EventId":"0da5d78a-9c6f-4cbe-9de5-873eb611c3c6","DemandId":"e157ea75-2bfe-44de-9fe9-3653986f2fac","SlotsJson":"[1]","AdministratorId":"L2-OPERATOR","AdministratorRole":"MAINTENANCE_ADMINISTRATOR","Reason":"现场维修完成，申请恢复原仓位操作。","State":"CLOSED","Revision":3,"SelectedAction":"RESUME_AFTER_REPAIR","ForcedRecoveryGeneration":0,"OpenedAt":"2026-09-19 08:58:31.7248481+00:00","UpdatedAt":"2026-09-19 08:58:32.1037637+00:00"}` |
| CLOSED 的 ExceptionRecoverySessionSnapshot 下发到车并被车确认 | PASS | `CLOSED snapshot acknowledged` | `messageId c2a8d999-2be1-4253-bbf3-beb6de82e97e acknowledged` |
| 需求与旅程仍阻断：装载仍 RecoveryRequired，旅程仍 Blocked | PASS | `RecoveryRequired / Blocked` | `RecoveryRequired / Blocked` |
| 会话关闭后车载端恢复入口重新出现（补偿清空） | PASS | `offered` | `True` |
| 同车能开新会话：第二次申请被 ExceptionRecoverySessionOpened 受理，会话 id 与被关闭的不同 | PASS | `Opened, new session id` | `Opened 32e0a9e8-5d1e-4c51-8b25-b5c34af93edf` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
