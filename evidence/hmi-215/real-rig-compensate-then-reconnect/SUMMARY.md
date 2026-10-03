# L2 场景证据：real-onboard-compensate-then-reconnect

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261003T062300726Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `3cf62fecd28c402fe9fc54a144b20a85583edd4b` |
| onboardHmiCommit | `b9cd47f74e456a75093c9aa97083db0e7795005e` |
| protocolFaultProxy | `True` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-37102766873-1\_stage\l2-20261003T062300726Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端的会话经协议故障代理建立（否则断开注入不到这条链路上） | PASS | `>= 1 SessionHello through the proxy` | `1` |
| 前半段到位：重启后中断结算把空着关上的装货报 UNKNOWN，服务端判 RecoveryRequired，旅程停摆 | PASS | `UNKNOWN / RecoveryRequired / Blocked` | `UNKNOWN / RecoveryRequired / Blocked` |
| 补偿清空走到对账：工作流 Reconciled、ALL_EMPTY，需求 Cancelled，旅程以 CANCELLED_BY_LOAD_COMPENSATION 收尾 | PASS | `Reconciled / ALL_EMPTY / Cancelled / Completed/CANCELLED_BY_LOAD_COMPENSATION` | `Reconciled / ALL_EMPTY / Cancelled / Completed/CANCELLED_BY_LOAD_COMPENSATION` |
| 补偿对账之后会话回到 Ready（断开之前的前提） | PASS | `Ready / READY` | `gen 2 / Ready / READY` |
| 补偿对账之后，恢复会话快照与补偿命令都已结清：CLOSED 那份快照被车确认，旧 revision 被取代，补偿命令被补偿结果结算 | PASS | `>= 2 recovery rows, 0 live, CLOSED snapshot acknowledged` | `5 rows, 0 live (ExceptionRecoverySessionSnapshot[70e2df4a] state=OPEN ack=False fenced=True; ExceptionRecoverySessionSnapshot[775198fb] state=ACTION_SELECTED ack=False fenced=True; LoadCompensationCommand[f56e7096] state= ack=True fenced=False; ExceptionRecoverySessionSnapshot[857fe172] state=EXECUTING ack=False fenced=True; ExceptionRecoverySessionSnapshot[1ddcb790] state=CLOSED ack=True fenced=False)` |
| 重连之后会话在新世代回到 Ready | PASS | `gen > 2 / Ready / READY` | `gen 3 / Ready / READY` |
| 补偿会话留下的恢复会话快照与补偿命令，一条都没有被重放进新会话 | PASS | `0 of 5 replayed` | `0 of 5 replayed` |
| 重连之后车还接得了单：下一条需求被受理并派车，车在取货点收下扫码并交给服务端 | PASS | `SublotSubmitted 1 条` | `SublotSubmitted 1 条` |
| 断开一次只换来一次重连：之后只有一条连接，而且到收尾还开着（没有哪一端撕会话） | PASS | `3 connections, 1 open` | `3 connections, 1 open (#1: onboard->server ended: IOException; #2: relay disconnected on request; #3: open)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
