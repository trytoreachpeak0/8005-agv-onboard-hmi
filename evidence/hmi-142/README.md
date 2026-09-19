# hmi#142 证据

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/142

会话心跳由写死 5 秒改为 ADR-cross-0027 的 2 秒、并改成可配。服务端配对票是
trytoreachpeak0/8005-agv-control-server#234（连续 6 秒无合法消息即判失联），本票要先于它合入：
5 秒心跳碰 6 秒阈值，每一拍都擦线。

## 先红后绿

开工基线 `e6968c7a`（`w2g/fp-v2-impl`，含 hmi#128、hmi#130、hmi#129）。

| 红 | 在哪个提交上 | 红成什么样 |
| --- | --- | --- |
| `red/01-session-heartbeat-cadence-042ee6c.txt` | `042ee6c`（测试提交，实现还是 5 秒） | `TheSessionHeartbeatKeepsTheAdrCadenceOnTheRealTimer`：「7 秒窗口里只收到 1 条 Heartbeat，就绪后各段间隔为 5.01 秒」 |
| `red/02-appsettings-session-heartbeat-042ee6c.txt` | `042ee6c` | `WireToGateExamplesConfigureTheAdrSessionHeartbeatInterval`：两份出厂配置的 `wireToGate` 节都没有 `sessionHeartbeatIntervalMs` |

另外三份是反向验证——实现落地之后，把实现逐项改回去，确认每条新测试真的盯着一个行为，而不是
写完实现顺手写出来的自证：

| 红 | 把什么改回去 | 结果 |
| --- | --- | --- |
| `red/03-reverted-implementation-all-heartbeat-tests.txt` | 间隔写死 5 秒、节拍改回「发完再 `Task.Delay`」 | 10 条里 6 红；4 条绿的是非法间隔被拒那条 `Theory`，它盯的是另一个行为 |
| `red/04-ack-roundtrip-added-to-next-wait.txt` | 只把节拍改回「发完再 `Task.Delay`」，默认值仍是 2 秒 | 只有 `ASlowHeartbeatAckDoesNotPushTheNextHeartbeatLate` 红：「就绪后各段间隔为 1.99 秒、3.01 秒」——`HeartbeatAck` 慢 1 秒，那 1 秒被算进了下一次等待 |
| `red/05-config-validation-absent.txt` | 去掉启动校验里的心跳那一段 | `ASessionHeartbeatIntervalOutsideTheAdrBoundsIsRejected` 四个值全红 |

绿：`green/01-heartbeat-g2.txt`（心跳相关 10 条，`296cfeb`）、
`green/02-config-and-architecture-unit.txt`（`ConfigurationTests` 与四个架构测试共 76 条）。

## 测试分工

- `SessionHeartbeatCadenceG2Tests`（2 条，真实计时器，各 7 秒）：默认节拍就是 2 秒；`HeartbeatAck`
  慢 1 秒时节拍不跟着往后挪。只有走真实计时器才看得见默认值本身写错。
- `SessionHeartbeatPacingG2Tests`（5 条，注入 `ManualTimeProvider`，合计约 1 秒）：差 0.1 秒不早发、
  配置值取代默认、装货进行中不落拍、断链后在新一代会话上恢复、非法间隔被拒。节拍断言钉到毫秒，
  不靠墙钟。
- `ConfigurationTests` 3 条：出厂配置写的是 2000、代码默认值与 ADR 的三个数一致、启动校验拒掉
  0／负数／>= 3000。

新测试不带 `IntegrationSlice` 与 `ProtocolVector` 标记：心跳不属于任何一条冻结向量，而切片标记必须
恰好是所声明向量的投影（`IntegrationSliceTraitArchitectureTests`）。四个架构测试已单独跑绿。

## 全量 ONBOARD_HMI_G2 与布局检查

见 `green/` 下对应目录，命令经 `Invoke-HeavyLocal.ps1 -Ticket hmi#142` 执行。

## 真装置 L2

CI `l2.yml` 的 `rig=real` 作业，场景 `real-onboard-compensate-then-reconnect` 一遍，作会话层改动的回归：
心跳变快会改变真装置上的报文节奏。run 号与结论见下。
