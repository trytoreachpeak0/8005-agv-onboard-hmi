# 王昆：HMI 第一次联调与模拟器自动化工作包

## 交付目标

本机无需现场即可先执行的计划和验收矩阵见：

- docs/LOCAL_VALIDATION_RUNBOOK.md
- docs/LOCAL_INTEGRATION_MATRIX.md
- docs/LOCAL_RECOVERY_MATRIX.md
- docs/LOCAL_UI_ACCEPTANCE.md

本工作包由王昆负责完成两项开发：

1. 完成 OnboardHmi 第一次可重复联调所需的产品开发；
2. 完成独立仓位模拟器的外部自动化控制接口。

最终应能执行一条可重复的多仓装货/卸货测试：HMI 通过正式协议接收业务命令，通过 Modbus 操作独立模拟器；测试程序通过模拟器 HTTP 控制面放货、取货、关门和注入故障；HMI 读取反馈并可靠上报结果。

## 仓库与起点

| 内容 | 仓库/分支 | 当前起点 |
| --- | --- | --- |
| OnboardHmi | `trytoreachpeak0/8005-agv-onboard-hmi` / `OnboardHmi_MVP` | `05bf9f4781828dcd4e63cbb7349e4cebd6a25a85`；产品树等同王昆 `bc56fa9` 基线，仅多交接文档。 |
| 仓位模拟器 | `trytoreachpeak0/slots-simulator` / `main` | `0a778c9439d7c0fb0f25791ccce00b5caf7e6b4b`；Modbus 基线，现有测试 18/18 PASS。 |
| 共享协议 | `trytoreachpeak0/8005-agv-protocol` | `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`。 |
| ControlServer | `trytoreachpeak0/8005-agv-control-server` / `ControlServer_MVP` | `02c2f3ca168f14d09f3fa1fc01b9654ff7e833ff`；目前正式会话恢复/心跳可联调，业务命令分发仍需双方对接。 |

协议固定身份：

- manifest SHA-256：`a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`
- Schema bundle SHA-256：`e04296e9bcf48c341bc91fef5731f6f465a5ecdbb9adedc17f3bac58e193d30c`
- vectors SHA-256：`fc5902b71d1b276c674f8a21c738d27193ddcbaf9b352951deffbaf1488d356e`

`protocol-v0.1.1` 是非破坏性符合性修订：消息字段、类型、枚举、方向和 `ProtocolVersion=1` 不变；W2G-IS-01 使用专用轨迹 `CV-DEMAND-ACCEPT-TO-PICKUP`，通用重试和首结果重放归入 W2G-IS-06。Onboard 只读展示 `UpcomingStopPlanSnapshot` 与 `CurrentStopWorklistSnapshot`，不读取 MesIngest、不选择、排序或绑定 Demand。

不得从协议仓 `main` 猜字段，也不得在 HMI 和 ControlServer 两边各自维护私有消息版本。

## 工作一：完成 HMI 第一次联调开发

### 第一次联调的范围

第一轮至少打通：

1. HMI 与 ControlServer/Fake ControlServer 建立精确 release 会话；
2. 完成能力快照、安全快照、journal 报告、readiness 和 heartbeat；
3. 接收一个 `WIRE_TO_GATE` 当前站点任务和服务端冻结的多仓位集；
4. 操作员提交一次 Sublot；
5. HMI 对目标仓位执行开锁并等待物理闭环；
6. 测试程序通过模拟器 HTTP 放货并关门；
7. HMI 从 Modbus 读取占用、锁闭和输出复位，可靠上报装货结果；
8. 关卡卸货时重复相反闭环，全部目标仓为空后上报成功；
9. 同命令重放不重复开锁，断联/UNKNOWN 时禁止新动作。

当前 ControlServer 只足以验证正式会话恢复和心跳；业务命令尚未完整真实分发。因此第一次开发分两步验收：

- A：使用按 `protocol-v0.1.1` Schema/向量实现的 Fake ControlServer 完成业务流；W2G-IS-01 必须覆盖 `CV-DEMAND-ACCEPT-TO-PICKUP`；
- B：ControlServer 补齐分发后，不改 HMI 业务逻辑，直接换成真实 ControlServer 重跑同一场景。

Fake PASS 只能称为 HMI G2/联调准备通过，不能称为最终 G3。

### 现有代码入口

王昆基线已有：

- `src/SQCD.Agv.Core/Ports.cs`：现有端口抽象；
- `src/SQCD.Agv.Core/DomainModels.cs`：现有领域状态；
- `src/SQCD.Agv.Application/OnboardController.cs`：当前单任务/单仓操作编排；
- `src/SQCD.Agv.Infrastructure/ModbusTcpIoModuleClient.cs`：Modbus FC01/FC02/FC05 客户端；
- `src/SQCD.Agv.Infrastructure/Configuration.cs`：IO、工作流和连接配置；
- `src/SQCD.Agv.Wpf/App.xaml.cs`：组合根；
- `src/SQCD.Agv.Wpf/ViewModels/MainViewModel.cs`：生产界面状态与命令；
- `src/SQCD.Agv.Wpf/appsettings.json`：现有端口、点位和八仓映射；
- `tests/SQCD.Agv.UnitTests/OnboardControllerTests.cs`：当前业务行为测试；
- `tests/SQCD.Agv.UnitTests/ModbusTcpIoModuleClientIntegrationTests.cs`：现有 Modbus 集成测试。

建议新增或重构的责任文件：

- Core：正式协议身份、业务投影、slot operation/journal 领域类型；
- Application：session/readiness、命令分发、批量仓位执行和恢复协调器；
- Infrastructure：TLS/NDJSON 协议客户端、SQLite journal、可靠 outbox/inbox；
- WPF：把正式旅程状态接入原型 A 信息结构，不在 ViewModel 内重写协议状态机；
- Tests：按 W2G-IS-00～07 建立逐切片测试和第一次联调 harness；
- `scripts/run-first-integration.ps1`：启动依赖、运行场景、收集证据并清理进程。

文件名可按王昆习惯调整，但职责边界不能混合。

### HMI 必须使用的正式消息面

第一轮至少覆盖以下协议消息：

- 会话：`SessionHello`、`SessionAccepted/Rejected`、`Heartbeat/Ack`；
- 恢复：`CapabilitySnapshot`、`SafetyStateSnapshot`、`RecoveryStateReport`、`SessionReadiness`；
- 旅程：`VehicleBusinessStateSnapshot`、`CurrentStopWorklistSnapshot`、`UpcomingStopPlanSnapshot`；
- 取货输入：`SublotEntryRequested`、`SublotSubmitted`、接受/拒绝响应；
- 仓位操作：`SlotOperationCommand`、`OperationProgress`、`OperationResult`、`DurableAck`；
- 发车：`PreDepartureSafetyCheck/Result`；
- 异常：至少保证重复、内容冲突、断联、结果待确认和重启恢复不会产生第二次物理副作用。

字段、枚举、required 和 correlation 规则全部以协议仓 Schema/样例为准。

### 模拟器连接配置

第一次联调使用：

```json
{
  "host": "127.0.0.1",
  "port": 1502,
  "unitId": 255,
  "doStartAddress": 100,
  "diStartAddress": 200,
  "channelCount": 16
}
```

八仓映射保持：DO 0～7、锁反馈 DI 0～7、光幕 DI 8～15。HMI 不调用模拟器 HTTP；只有测试 harness 调用 `127.0.0.1:58006` 控制环境。

### HMI 最低验收条件

- 王昆基线原有 48/48 测试继续 PASS；
- 正式 release 身份不匹配时 fail closed；
- 空 journal 恢复到 READY；非空 pending result 不得假 READY；
- 多仓命令严格使用服务端冻结仓位集；
- 开锁前先持久化，重复命令不产生第二次 Modbus 写；
- 仓位 UNKNOWN、门未锁、输出未复位或断联时禁止成功和发车；
- 装货/卸货结果使用稳定 `messageId` 重放直到 `DurableAck`；
- HMI 重启后读取 journal 和实时 Modbus 状态再决定恢复；
- 第一次联调证据绑定 HMI commit、模拟器 commit、协议 release、配置哈希和场景结果。

### 2026-08-26 G3 阻断与修复：RecoveryStateReport 跨代次重放

真实 `ControlServer_MVP@cc6e2b97e4308fa14b519edf9a0089d0da7d6d14` 与本仓
`OnboardHmi_MVP@045514770da9858a8a49196dede276192e4f2a1b` 的阶段性 G3
曾稳定复现一个恢复阻断：测试代理只丢弃 generation 1 的
`RecoveryStateReport` 第一条 `DurableAck` 后，HMI 会在 generation 2、3、4 的
新连接上逐字重放 journal 中的旧 envelope。messageId 与 payload 保持正确，但
envelope 的 `sessionGeneration` 仍为 1，ControlServer 因此按围栏拒绝
`STALE_SESSION_GENERATION`，会话持续停在 `HANDSHAKE_INCOMPLETE`。

这不是 ControlServer 放宽围栏的问题。已接受协议决策要求：重连时使用当前
`sessionGeneration` 重新封装待补报语义消息，同时保留原 `messageId` 与 payload；
旧 generation 的消息必须拒绝。

本仓已完成修复：`WireToGateSessionClient` 在重连前保留业务身份和 payload，使用
当前 generation 重新封装，并通过 SQLite journal 事务原子更新 wire/hash；
`FakeControlServer` 已对所有非 `SessionHello` 消息启用当前代次围栏。
`ReconnectDuringRecoveryRebindsDurableReportWithoutUnlockSideEffects` 现在断言
messageId/payload 相同、generation/hash 更新、journal 保存新 wire，以及无第二次
物理副作用。修复后的本端 W2G G2 为 13/13 PASS。

解除完整 G3 门禁仍需提供：本端 G2 回归、Fake 对所有非 `SessionHello` 消息的当前
代次校验，以及真实双端首 Ack 丢失后稳定收敛到安全 readiness 的新 G3 证据。原始红
证据由本仓
`evidence/g3/20260826-recovery-ack-drop-cc6e2b9-0455147/SUMMARY.md` 保存；重跑入口为
`scripts/run-staged-g3-recovery-ack-drop.ps1`。修复后生成新的证据目录，不覆盖原红
证据。

## 工作二：完成模拟器外部控制接口

详细契约放在模拟器仓库：

- `docs/EXTERNAL_AUTOMATION_CONTROL_API.md`
- GitHub：`https://github.com/trytoreachpeak0/slots-simulator/blob/main/docs/EXTERNAL_AUTOMATION_CONTROL_API.md`

核心要求：

- 保持 Modbus `127.0.0.1:1502` 为 HMI IO 数据面；
- 新增 loopback HTTP `127.0.0.1:58006` 作为测试控制面；
- 支持 reset、snapshot、放货/取货、关门、传感器覆盖和 Modbus 通信故障；
- 每次状态变化递增 revision，支持 `expectedRevision` 防止测试竞态；
- HTTP 绝不提供开锁接口；开锁必须来自 HMI Modbus DO；
- 原有 18/18 测试保持 PASS，并新增真实 HTTP+Modbus 黑盒测试。

## 推荐联调脚本顺序

```text
1. 启动 Simulator AutomationHost：Modbus 1502 + HTTP 58006
2. POST /api/v1/reset，保存 runId/revision
3. 启动 Fake ControlServer 或真实 ControlServer
4. 启动 HMI，等待正式 session READY
5. 下发目标仓位 [1,2] 的装货命令
6. 断言 HMI 对 1、2 号仓产生 Modbus 开锁动作
7. HTTP 将 1、2 号仓 cargo 设为 OCCUPIED，并逐仓 close-door
8. 断言 HMI 读取 DI 后只上报一次装货成功
9. 重放同一命令，断言没有第二次开锁
10. 下发卸货命令；HTTP 设为 EMPTY 并 close-door
11. 断言 HMI 上报全部空仓、锁闭、输出复位
12. 注入 UNKNOWN/NO_RESPONSE/断联，断言 HMI fail closed
13. 收集双方日志、journal、模拟器 snapshot 和结果 manifest
14. 停止并确认所有测试进程与端口已清理
```

## 最终交付清单

王昆完成后提供：

- OnboardHmi 完整 commit 和分支；
- SlotSimulator 完整 commit 和分支；
- `protocol-v0.1.1` 精确身份（新运行生成新 G2 证据，不能继承 v0.1.0 证据）；
- HMI Release 构建和全部测试结果；
- 模拟器原有 18 项与新增控制 API 测试结果；
- 一条可复现的第一次联调命令；
- 正常装货/卸货、重复、断联、UNKNOWN 的机器证据；
- 尚未通过的真实 ControlServer G3、真实 IO、目标硬件或现场项目清单。

完成以上内容后再约双方第一次正式 G3；不要用手工演示或模拟器 PASS 代替人员确认和真实对端证据。
