# 本机 Integration Slice 矩阵

| Slice | 本机覆盖 | 本机结果 | 外部/现场剩余 |
| --- | --- | --- | --- |
| IS-00 | G2 会话恢复、断线重连、快照替换/冲突、DurableAck、SQLite journal 恢复 | 已由 G2 证据工具执行 | ControlServer G2、联合 G3 |
| IS-01 | CV-DEMAND-ACCEPT-TO-PICKUP 的两个只读旅程快照、持久化后 SnapshotAppliedAck、revision 冲突 | 已由 G2 证据工具执行 | MES 重读、AcceptedDemandSnapshot、RIoT、可信到站、联合 G3 |
| IS-02 | slots-simulator 18+14 黑盒测试；服务器冻结仓位集；装货 journal-before-pulse；重复 attempt 不重复执行 | 本机通过 | 真实 IO、锁/门/光幕、现场多仓动作 |
| IS-03 | 可替换车辆安全 provider；STOPPED/MOVING/UNKNOWN/过期；未知快照零 IO；安全策略 fail-closed | 本机通过 | 真实停稳/驻车信号接线和现场时效 |
| IS-04 | 八仓卸货全空结果、锁闭和输出复位；未知状态拒绝 | 本机通过 | 真实卸货仓位反馈 |
| IS-05 | 断线后的 journal checkpoint、安全收尾和重连恢复测试 | 本机通过 | 真实网络/TLS 断链 |
| IS-06 | stable messageId/content hash；same-content 重试；different-content 冲突；首结果/未确认 outbox 重放 | 本机通过 | ControlServer 幂等和联合重放 |
| IS-07 | 未授权恢复动作全部由策略阻断；状态、停稳、仓位和输出条件逐项 fail-closed | 本机通过安全边界 | 正式恢复 session、授权、人工/机械动作、硬件恢复和 ControlServer 业务闭环 |

本表中的“本机通过”只表示 OnboardHmi、Core/Infrastructure、Fake ControlServer
和 slots-simulator 的可重复验证，不把同事负责的 ControlServer 业务或现场硬件
结果标记为完成。
