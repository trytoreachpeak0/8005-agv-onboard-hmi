# hmi#343 第 1 步：取消 B 报 UNKNOWN 之后，三条出路实测

基线：`w2g/fp-v2-impl@db9b2c8`（产品代码未改）。探测代码 `01-probe-source.cs.txt` 是临时 G2 用例
（`MultiDemandJourneyG2Tests` 的 TakeOver 夹具：恢复开关开、凭据在、假服务端应答恢复请求并下发补偿命令），
跑完即从 `tests/` 撤出，只留在这里备查。

构造：A 在 5 号仓装好结算；B 在 1 号仓开门、操作员不动；按「取消装货」，替身在授权前把 1 号仓锁反馈设成读不到，
`LoadCancellationResult` 报 `UNKNOWN`，会话 `RecoveryRequired`。随后把 1 号仓关好、可读（排除门检挡路），再逐条按。

| 出路 | 入口 | 按下 | 证据 |
| --- | --- | --- | --- |
| 装货补偿 | 灰 | 返回 false，`RECOVERY_BLOCKED`「操作被拒绝（RECOVERY_VECTOR_CONFLICT）…」，未发任何报文、未开门 | `01-probe-compensation.txt` |
| 故障货物交接 | 灰 | 同上 | `01-probe-handoff.txt` |
| 强制机械恢复 | 灰 | 同上 | `01-probe-forced.txt` |
| 修复续行（票外） | 亮 | 服务端接受；续作命令重开 1 号仓、提示「请在1号仓放入货物并关门」，把已取消的装货重跑；门关好时替换结果 COMPLETED，但日志簿仍留 `LOAD_CANCELLATION` 向量与未结尝试 B | `01-probe-resume.txt`、`01-probe-resume-ok.txt` |

三条都卡在 `WireToGateBusinessService.RecoveryVectors.cs:1767-1770`（日志簿里是别的类型的向量即抛 `RECOVERY_VECTOR_CONFLICT`）。
真服务端的补偿命令只在车载端提交恢复动作并发 `LoadCompensationRequested` 之后才下发，没有独立的「服务端发起补偿」路径。

结论：车卡在 `LoadCancellationResult_NOT_RECONCILED` 上没有出路。Coordinator11 10-13 裁定收归切换线、进生产前修。
