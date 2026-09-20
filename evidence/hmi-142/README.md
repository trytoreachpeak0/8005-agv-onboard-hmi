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

两轮，命令都经 `Invoke-HeavyLocal.ps1 -Ticket hmi#142`，协议克隆在 `scratch/hmi142-protocol`。

| 证据 | 车载端 | 结论 |
| --- | --- | --- |
| `green/onboard-hmi-g2-f70bf673/` | `f70bf673`（merge 之前） | **PASS**，出站 schema 4951 行 0 违规 |
| `green/onboard-hmi-g2-c39e5a4/` | `c39e5a4`（merge 了 hmi#134 之后） | **PASS**，出站 schema 5339 行 0 违规 |

两轮的 `failures` 都是空，工作树都干净，build／test／format 三步全过。

布局检查：`green/04-ui-layout-f70bf673.json`、`green/06-ui-layout-c39e5a4.json`，都 **PASS**。

## 真装置 L2

CI `l2.yml` 的 `rig=real` 作业（vm01 交互式 runner），`real-onboard-compensate-then-reconnect` 一遍，
作会话层改动的回归：心跳变快会改变真装置上的报文节奏。

**run 35477628971，PASS，64 秒**。证据 `green/rig-compensate-then-reconnect-6d8bd18e-35477628971/`
（`_stage/` 下的构建日志已删，其余原样）。

| 项 | 值 |
| --- | --- |
| control-server | `1580755582ba62894310e875e0ebaad5246643e5`（`fp/v2-impl` 当时的顶端） |
| onboard-hmi | `6d8bd18eb4867198187c9c4a2c68e167631a9dda`（merge 之前的 PR 头） |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| 整机已提交内存 | 起 3.65 GiB，峰值 6.46 GiB（39 个采样） |

九条判据 `L2-CR-00` 到 `L2-CR-08` 全 PASS，其中 `L2-CR-03`／`L2-CR-05`（重连之后会话回到 Ready）与
`L2-CR-08`（断开一次只重连一次、到末尾只剩一条连接）正是心跳节奏变化最可能影响的两条。

跑的是 merge 之前的 `6d8bd18e`。之后那个 merge（`c39e5a4`）只是把集成分支上 hmi#134 的改动并进来，
没有改本票的任何一行，合并本身也是自动完成、无冲突；merge 之后的全量 G2 已重跑并 PASS。
