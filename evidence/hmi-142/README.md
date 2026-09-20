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

- `SessionHeartbeatCadenceG2Tests`（1 条，真实计时器，绿时约 4 秒）：服务端 ack 慢 **2.5 秒**的情况下，
  首条心跳与相邻两条都不超过 **3 秒**（`MaximumHeartbeatInterval`，ADR 里唯一有意义的那个界）。
  一条用例同时钉住两件事——默认值是 2 秒，且 ack 的往返不累加到下一拍。只有走真实计时器才看得见
  默认值本身写错。起初拆成两条各 7 秒，合并是 CI 那次红之后做的（见下）；ack 延迟与门槛的这组数字
  是审查 S1 之后定的，见文末。
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

**本票的证据是 run 35482172666，PASS，68 秒**，跑在处理完独立审查意见（含 S2 改 `MessageTimeoutMs`）
之后的 `429e0ff9` 上。证据 `green/rig-compensate-then-reconnect-429e0ff9-35482172666/`
（`_stage/` 下的构建日志已删）。

| 项 | 值 |
| --- | --- |
| control-server | `e3048a255a6de2620160e4831515ec1472a8b046` |
| onboard-hmi | `429e0ff9348724c69f1c1d93a136bd869c57d8e4` |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| 整机已提交内存 | 起 4.22 GiB，min 4.27、峰值 6.94 GiB（42 个采样） |

九条判据 `L2-CR-00` 到 `L2-CR-08` 全 PASS，其中 `L2-CR-03`／`L2-CR-05`（重连之后会话回到 Ready）与
`L2-CR-08`（断开一次只重连一次、到末尾只剩一条连接）正是心跳节奏变化最可能影响的两条。

**为什么跑了三遍，只有这一遍算数。** 一遍真装置只证明它实际跑的那棵树：

| run | onboard | 结论 | 为什么不算数 |
| --- | --- | --- | --- |
| `35477628971` | `6d8bd18e` | PASS 64 秒 | merge 车载端顶端之前 |
| `35480006316` | `405e5b05` | PASS 64 秒 | 同上；这一遍是在调度的「先别派发」送达之前发出的，按规矩没有取消，让它跑完 |
| `35480344066` | `881aa990` | PASS 83 秒 | merge 之后、但在审查改动（S2 动了 `MessageTimeoutMs`）之前 |
| **`35482172666`** | **`429e0ff9`** | **PASS 68 秒** | **本票证据** |

`881aa990` 那遍之所以非跑在合并之后的树上不可（调度 2026-09-20 为这张票破了一次「无产品文件交集就
不强制带入顶端重跑」的常规）：本票与期间合入的 hmi#140、hmi#139 在产品文件上没有交集，但文件交集
只是代理指标。本票把心跳频率翻了一倍多，而 hmi#140 改的正是**接收循环在什么情况下终止**——心跳的
回执就是从那条循环回来的，单位时间里那条循环要多处理一倍多的往返。这种耦合不落在文件上，落在
运行时，而且只有真装置会把车载端 WPF 与模拟器真起起来跑完整条路径。审查后又跑一遍，是因为 S2 改的
`MessageTimeoutMs` 同样落在会话收发路径上。

## CI 上红过一次，红的不是本票的用例

`ONBOARD_HMI_G2` 在 `74ceb542`（merge 集成分支之后）红过一次，run
[35478019226](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/actions/runs/35478019226)。
红的是 hmi#127 留下的既有用例，G2 的 293 条里只有它，单元测试 449 条全过：

```
SQCD.Agv.WireToGateG2Tests.StationDeadlineExpiredG2Tests.AnAttemptRefusedWhileRecoveryRequiredStillGetsItsResultOutWhenReissued [FAIL]
  Error Message:
   Timed out after 10s waiting for: a mid-session SessionReadiness
```

原文在 `red/07-ci-35478019226-station-deadline-flake.txt`（从 run 的 `g2-evidence` artifact 里取的
`logs/dotnet-test-release.log`；CI 日志正文只抬了一行「dotnet-test-release exited with code 1」）。

**与心跳改动无关，理由是机理而不是「跑了没复现」**：那条用例的 `StationDeadlineExpiredG2Tests.Harness`
只调 `business.Start()` 与 `ConnectAndRecoverAsync`，**全程没有 `session.Start()`**，而心跳循环只在
`session.Start()` 拉起的 `RunAsync` 里跑。那条用例从头到尾一条 `Heartbeat` 都没发过，2 秒还是 5 秒
对它没有任何输入。

两组本机对照都复现不出来（这台机器比 CI 那台空闲，本机不是合适的复现环境，两组都只能作旁证）：

| 组 | 跑法 | 结果 |
| --- | --- | --- |
| 集成分支顶端 `226ff87`，不含本票任何改动 | 全 G2 项目 × 3 轮，Release | 283 条全绿（`green/05-baseline-…`） |
| 本票分支 | `StationDeadlineExpiredG2Tests` 单类 × 5 轮，Release | 34 条全绿（`green/05b-…`） |
| 本票分支，墙钟用例合并之后 | 全 G2 项目 × 3 轮，Release | 292 条全绿（`red/06-…` 的 D 段） |

调度那边另有一条独立观察：同一个用例、同一个等待对象（`a mid-session SessionReadiness`），
2026-09-20 在别的票的全量上已经红过一次，单独连跑 6 次全过。修那条用例的等待写法由调度另开票，
不在本票边界内。

**本票自己做的是减少对并行负载的贡献**：原来两条真实计时器用例各占 7 秒墙钟，合并成一条带 1 秒
ack 延迟的，绿时约 4 秒。反向验证（`red/06-merged-cadence-reverse-verification.txt`）确认判别力没降：

| 把实现改回 | 合并后的用例红成什么样 |
| --- | --- |
| 默认写死 5 秒 | 「6 秒窗口里只收到 1 条 Heartbeat，就绪后各段间隔为 5.01 秒」 |
| 节拍改回「发完再 `Task.Delay`」 | 「第 1 与第 2 条 Heartbeat 之间隔得太久，就绪后各段间隔为 2.00 秒、3.05 秒」 |

## 2026-09-20 独立审查之后的改动

审查认可实现本身、四类心跳频率副作用逐项核实都不存在，与 hmi#140 叠加是互补（它新增的
`FinishFailedSession` 会把 `_responseWaiters` 里所有等待者统一置异常，**包含在途心跳**，所以接收循环
一死心跳循环立刻知道，不必等满 `MessageTimeout`）。以下是按审查意见做的改动。

### M1：第二次 CI 红的原文没进过证据目录

`35480605575` attempt 1 的 artifact 已被 attempt 2 覆盖，API 取不回来。原文在
`red/08-ci-35480605575-attempt1-leftover-settlement-flake.txt`（attempt 1 跑完时下载的那份）。
红的是 `ALeftoverWhoseSettlementResultCouldNotBeSentIsStillRestoredAsLastTimes`，不是本票新增的
任何一条。教训记在这里：**当时我把这件事发给了调度，却没写进 PR 正文**——审查只读票面、diff 与 CI，
一次没有解释的随机红落在一张新增了墙钟用例的票上，最自然的怀疑对象就是那条墙钟用例。

### S2：消息超时是那道界的另一扇门（真漏洞，本票修掉）

心跳循环串行地等 `HeartbeatAck`，所以服务端看到的**到达间距是 max(心跳间隔, ack 往返)**，而 ack 往返
的上界就是 `wireToGate.messageTimeoutMs`。那一项原本只校验 `<= 0`：间隔配 2 秒、超时配 10 秒都能过，
服务端慢应答时两条心跳可以隔 10 秒才到，`< 3000` 那道界形同虚设。更要紧的是**出厂值 3000 恰好等于
那道界**，间距可达 3 秒、丢一条正好踩在 6 秒线上，保证在但余量为零。

改法：`MessageTimeoutMs` 与 `SessionHeartbeatIntervalMs` 受同一道界约束，出厂值与代码默认值都下调到
2500，新增 `ConfigurationTests.AMessageTimeoutThatWouldStretchTheHeartbeatGapIsRejected`（3000／10000／
`int.MaxValue` 三个值），并在出厂配置那条用例里钉住 2500。反向验证：去掉那段校验，三个值全红
（`red/09` 的 D 段）。

彻底的解法是把「发心跳」与「等 ack」解耦（更贴 ADR 的「心跳必须独立持续运行」），超出本票边界。

### S1：门槛从手挑的 2.5 秒挪到 ADR 的界上

原来绿路径实测 2.0 秒、门槛 2.5 秒，抗抖余量 0.5；缺陷路径 3.05 秒、门槛 2.5 秒，判别余量也是 0.5。
两个余量反向共用同一个门槛——收紧更易偶发红，放宽就丢判别力，而那个 3.05 是在空闲开发机上测的。
把 `AckDelay` 从 1 秒提到 2.5 秒（用例本地的 `MessageTimeout` 相应放宽到 5 秒，它不是出厂配置），
缺陷路径变成 4.5 秒，门槛就能落在 `MaximumHeartbeatInterval`（3 秒）。实测：绿路径余量 1.0，
缺陷 C 实测 4.53 秒、判别余量 1.5，缺陷 B 仍然红（`red/09` 的 B、C 段）。

审查给的第二条（替身回 ack 前推一次手动时钟）这里做不到：这条用例走的就是真实计时器，注入手动
时钟就不再证明「产品真的走 `Task.Delay` 那条路」了。

### S3：两处偏离票面的测试接缝，连同理由

- 票面写「`Ready` 后 10 秒内不少于 4 条心跳」，实际是「8 秒窗口里不少于 2 条，且首条与相邻两条都不
  超过 3 秒」。条数少了，但界更严（票面没给首条的界），而墙钟从 10 秒降到绿时约 4 秒。
- 票面写「繁忙时间隔仍不超过 2.5 秒」，`TheCadenceHoldsWhileASlotOperationIsInFlight` 走的是手动时钟，
  推一拍就断言到一条，**没有**一个 2.5 秒的墙钟界；原先等待用的是 10 秒默认超时，等于实际界是 10 秒。
  已把等心跳的超时收到 2.5 秒（`HeartbeatArrival`），让用例守的界与名字里说的界一致。

### S4：握手没完成时先说清楚红在哪

`SessionHeartbeatCadenceG2Tests` 等 `Ready` 超时后原本不断言就往下走，会红成「一条 Heartbeat 都没收到」，
把诊断指向心跳而真因在握手。补了一句断言。这正是 hmi#149 要收拾的形状，不该再新增一个。

### 与 ADR 的一处张力（交 program 票处理，不在本票边界内）

ADR-cross-0027 写的是「心跳与失联阈值是项目级统一配置，**不允许按车辆设置不同值**」，而本票按票面
要求把它做成了每台车 `appsettings.json` 里的一项——三台车的配置文件彼此独立，物理上可以配成不同值。
代码里的界（正数且严格小于 3 秒）保证了任何配置都不违反 ADR 的**行为**要求，但「统一」这件事是
运维纪律而不是代码约束。调度会开 program 票让 ADR 措辞与「项目级默认值 + 现场窄幅可调」对齐。
