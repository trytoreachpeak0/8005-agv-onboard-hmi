# OnboardHmi WIRE_TO_GATE 薄实施 Spec

本目录是现有车载基线向正式 WIRE_TO_GATE MVP 的迁移导航，不复制需求正文。当前单仓 `IRuleGateway`/`IIoModuleClient` 路径和 RuleMock 仅保留为可运行旧基线，不能冒充候选协议或最终八仓 HTTP IO 模拟器。

## 固定身份与权威

- 分支：`OnboardHmi_MVP`
- 协议候选：`72ddde595165468520d9f3a46b25e4aa4eec0c3f`
- manifest：`e878d89e820535fe1eb64b85681b9c2994fb98646309e6ba768219c5c8735f2e`
- 状态：`CANDIDATE_UNAPPROVED`；可用于隔离开发与测试，不得称为正式 release。
- UI：获选原型 A“旅程导引台”源是布局/交互权威；生产代码不得依赖 HTML、假数据或原型词汇。
- 物理：实时 `ISlotIoProvider` 是仓位物理事实入口；ControlServer 不发送原始 DI/DO，车载端不调用 RIoT 或从 MesIngest 选任务。

## 迁移落点

- `SQCD.Agv.Core`：候选身份、`ISlotIoProvider` 业务状态、journal/恢复领域类型。
- `SQCD.Agv.Application`：session、命令去重、PREPARED-before-unlock、ActiveUnlockSet、安全闭环。
- `SQCD.Agv.Infrastructure`：`HttpSimulatorSlotIoProvider`（默认可配置 `127.0.0.1:58006`）、未来 `ModbusSlotIoProvider`、SQLite journal、TLS/NDJSON。
- `SQCD.Agv.Wpf`：按 [`prototype-to-production.md`](prototype-to-production.md) 替换现有单屏结构，保留单一生产信息架构。
- `RuleMock`：逐步由只模拟协议可观察服务端事实的 Fake ControlServer 替代，不拥有任务引擎。

开锁前必须持久化完整命令与 PREPARED；相同 attempt/相同内容重放，不重复 IO；异内容稳定拒绝。断联不扩大 ActiveUnlockSet，重启必须重读实时 IO，UNKNOWN 保持恢复。模拟器通过不表示真实 Modbus、真实 IO、锁、光幕或目标工控机合格。

逐切片约束见 [`slices.md`](slices.md)。
