# OnboardHmi 开发接管说明

> 当前最具体的两项开发任务见 [`WANG_KUN_FIRST_INTEGRATION_WORK_PACKAGE.md`](WANG_KUN_FIRST_INTEGRATION_WORK_PACKAGE.md)，包括 HMI 第一次联调和独立模拟器外部自动化控制接口。

## 目的

## 当前本机交付状态（2026-08-26）

当前 OnboardHmi_MVP 工作区已经按现场联调前计划完成一轮可重复的本机闭环：

- protocol v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279 的 G1 通过；
- HMI 生成独立 evidence/g2/protocol-v0.1.1/<run>/，含 summary、NDJSON
  transcript/journal、原始日志和测试结果；
- IS-00/IS-01 的 Onboard HMI G2 本机结果 PASS，IS-01 使用
  CV-DEMAND-ACCEPT-TO-PICKUP；
- Production 配置占位值拒绝、可替换车辆安全信号 provider、八仓装/卸货和
  recovery fail-closed 矩阵已纳入代码/测试；
- scripts/run-local-validation.ps1 已跑通 protocol G1、Release build/test/
  format、slots-simulator 18+14、W2G/Legacy 审计和 UI 布局审计；
- 当前回归基线：Unit 80/80、W2G G2 13/13、Release 0 warning/0 error。

以上是 OnboardHmi 本机证据，不是 ControlServer G2、联合 G3、真实车辆信号、
现场明文网络或硬件验收。最新证据目录必须在每次运行后重新生成，不能继承旧
protocol 版本证据。

本文件交给车载端负责人王昆接管 `WIRE_TO_GATE` OnboardHmi 开发。后续 OnboardHmi 产品代码、测试和实现判断由车载端负责人本人完成或审查批准；AI 不再把自己的实现表述为车载端负责人已完成的工作。

## 先确认提交归属

远程 `OnboardHmi_MVP` 历史中包含 AI 候选代码，不能仅根据 Git Author 名称判断为人员亲自开发：

| 提交 | 实际含义 |
| --- | --- |
| `bc56fa9aebd98e8cd488fa1f30a0d95bfd40c93e` | 王昆提交的初始 OnboardHmi 基线，提交者为 `Kun Wang`。 |
| `2eeecf6dd44dc041e3db79da06d225d7d3ec77f2` | AI 创建的 WIRE_TO_GATE 迁移骨架。 |
| `f265cd8a1a617a092e6df4babdc086cc92c876ef` | AI 编写的八仓、恢复、模拟器、测试和 WPF 候选实现。 |
| `398a957662cacb6c0f49ddd78da12d896a1469be` | AI 修正恢复报告 Schema。 |
| `c41160c34c411f0f602f7c695e8c880ac6c19ae4` | AI 将候选绑定到上一版正式协议；当前 release 已升级为 `protocol-v0.1.1`。 |

后四个提交显示的 Git Author 为 `Zhengyu Shao`，原因是开发终端使用了该 Git 身份；这不表示郑宇或王昆本人编写、复核或批准了其中代码。

用户已选择由王昆从自己的 `bc56fa9` 基线继续开发。当前产品树已通过可追溯的 revert 恢复到该基线，只额外保留 README 提示与本交接文档。`bc56fa9..c41160c` 仍可在 Git 历史中作为 AI 参考候选查看，但不会作为当前产品代码，也不能称为车载端完成版本。

## 不可改变的协议身份

两端正式协议权威是独立仓库 [`8005-agv-protocol`](https://github.com/trytoreachpeak0/8005-agv-protocol) 的不可变 release：

- tag：`protocol-v0.1.1`
- commit：`1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- manifest SHA-256：`a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`
- Schema bundle SHA-256：`e04296e9bcf48c341bc91fef5731f6f465a5ecdbb9adedc17f3bac58e193d30c`
- vectors SHA-256：`fc5902b71d1b276c674f8a21c738d27193ddcbaf9b352951deffbaf1488d356e`

本次为非破坏性符合性修订：消息字段、类型、枚举、方向和 `ProtocolVersion=1` 不变；W2G-IS-01 改用专用向量 `CV-DEMAND-ACCEPT-TO-PICKUP`，通用重试与首结果重放向量归入 W2G-IS-06。Onboard 对 Demand 仍只保留已承诺旅程和当前停靠工作两个只读投影，不读取 MesIngest、不选择或绑定 Demand。

不得跟踪协议仓 `main`、复制并私改 Schema，或在 OnboardHmi 内另建一套消息定义。任何跨端协议变更必须同时更新协议仓 Schema、样例、向量和兼容性说明，并由两名真实负责人重新批准。

## 车载端必须完成的实现

### W2G-IS-00 — 会话与恢复

- 明文 TCP/NDJSON 会话、外部凭据、精确 release 身份拒绝。
- `SessionHello`、能力与安全快照、`RecoveryStateReport`、`SessionReadiness` 五步恢复。
- session generation fencing、心跳丢失安全阻断、pending result 原 `messageId` 重放。
- 重启后从持久 journal 与实时 IO 重新建立事实，未知状态不得自动放行。

### W2G-IS-01 — 服务端权威旅程（`CV-DEMAND-ACCEPT-TO-PICKUP`）

- 只消费 ControlServer 发布的 Demand、当前站点 worklist 与旅程投影。
- OnboardHmi 不选任务、不决定仓位、不调用 MesIngest 或 RIoT。
- 相同 revision/相同内容幂等；同 revision/不同内容稳定拒绝。

### W2G-IS-02 — 机台取货与多仓装货

- 机台扫码只提交 Sublot 核验，不由本地重新分类任务。
- 严格执行服务端冻结的完整目标仓位集。
- 开锁前持久化 `PREPARED`；同一 attempt 重放不得产生第二次 IO 脉冲。
- 批量结果必须包含每个目标仓的最终占用、锁、输出复位和可追溯证据。

### W2G-IS-03 — 发车安全

- 八仓事实、车停稳、门锁、开锁输出复位、UNKNOWN 和快照时效共同决定安全状态。
- `ControlServerVehicleSafetySignalProvider` 通过 `GET
  /api/onboard/v1/vehicle-safety` 的明文 HTTP 投影后台轮询车辆状态；Bearer 凭据只从
  `CONTROL_SERVER_ONBOARD_CREDENTIAL`（或显式配置的环境变量名）读取，不保存 RIoT
  凭据，也不把响应写入 journal 或日志。
- 只有车辆身份匹配、`observedAt` 未过期且 `motionState=STOPPED` 才能映射为
  `VehicleMotionState.Stopped`；`MOVING`、`UNKNOWN`、`MT_NA`、连接/认证/超时、
  格式错误、身份不匹配和过期均 fail-closed 为 `Unknown`。读取失败会立即替换上一份
  `STOPPED` 快照，不复用旧状态。
- OnboardHmi 只提供车载物理事实和执行安全闭环，不拥有移动目标或 RIoT 订单权威。
- 断联或安全事实过期时禁止新开锁和新动作。
- 明文传输会暴露 Bearer/credentialProof，并允许网络中间人篡改 STOPPED 投影；两端必须
  同版本切换，不能将明文 OnboardHmi 与旧 TLS ControlServer 混用。

### W2G-IS-04 — 关卡批量卸货

- 关卡无需二次扫描 Sublot、输入工号或等待外部 PDA。
- 只执行服务端下发的完整卸货仓位集。
- 所有目标仓均为空、上锁且输出复位后才能报告成功。

### W2G-IS-05～07 — 断联、重放与异常恢复

- 断联只允许当前 ActiveUnlockSet 安全收尾，绝不扩大开锁集合。
- 可靠消息以稳定业务键和 `messageId` 防重；同键异内容必须显式冲突。
- 支持结果未知对账、进程崩溃恢复、补偿、故障货物交接、强制机械恢复及人工充电返回。
- 所有危险恢复动作必须有服务端授权和持久审计；UI 不得用普通按钮绕过。

## UI 与 IO 边界

- 获选原型 A“旅程导引台”是布局与交互权威：目标视口为 1024×768、100% 缩放；主界面采用左侧旅程、中央唯一主动作、右侧固定八仓；主旅程、当前步骤、关键阻断和八仓状态不得依赖危险滚动，技术日志放在次级层级。
- 主界面保持“左侧旅程、中央唯一主动作、右侧固定八仓”；生产代码不得依赖原型项目或假数据。
- 八仓 IO 是车载端独占物理权威。ControlServer 只发业务命令，不发送原始 DI/DO。
- HTTP IO Simulator 仅用于确定性软件测试，不证明真实 Modbus、接线、锁、光幕或工控机资格。
- Golden WPF 预览、DPI/目标视口验证和真实硬件动作需要单独授权，不能由本地截图或模拟器 PASS 代替。

## Git 历史中的 AI 候选范围

`bc56fa9..c41160c` 曾涉及 52 个文件，现已从当前产品树回退；如需了解思路，可只在 Git 历史中审查：

- `WireToGateSessionClient`、正式 release 身份和五步恢复；
- SQLite `OnboardExecutionJournal`；
- `WireToGateSlotExecutor` 与八仓状态抽象；
- HTTP IO Simulator、Fake ControlServer 与 Conformance 工具；
- 原型 A 的 WPF 候选窗口；
- W2G-IS-00～07 的候选 G2 测试入口。

已知不能直接验收为完成版本的缺口：

- 生产 WPF 尚未完成 ControlServer Demand、worklist、扫码、批量操作及异常恢复消息的真实分发闭环；
- 完整 W2G-IS-00～07 G3 尚未通过；
- 首次 `RecoveryStateReport` Ack 丢失的跨 generation 重放缺陷已在当前分支修复：
  pending wire/hash 会在新连接发送前原子更新，原始红证据仍保留在
  `evidence/g3/20260826-recovery-ack-drop-cc6e2b9-0455147/SUMMARY.md`；完整真实双端
  回归需用全新数据库/journal 重跑 `scripts/run-staged-g3-recovery-ack-drop.ps1`；
- 真实 IO、目标工控机和 Golden WPF 尚未验证；
- 真实 RIoT、车辆、Map、站点和现场旅程属于联合门禁，不能由 Onboard 单仓自测证明。

## 本地验证命令

从干净工作区运行王昆基线现有的构建和测试：

```powershell
dotnet build .\SQCD_8005AGV.slnx -c Release
dotnet test .\SQCD_8005AGV.slnx -c Release
dotnet format .\SQCD_8005AGV.slnx --verify-no-changes --no-restore
```

本仓已有 scripts/run-w2g-g2.ps1，会先校验
8005-agv-protocol@1531489e42e328f28bfe0c51ed3f8c56e5ce0279 的 protocol-v0.1.1
release 身份与 manifest 哈希，再生成新的证据目录。当前脚本覆盖 Onboard HMI 的
IS-00/IS-01 G2；IS-02～IS-07 的本机安全/IO/可靠性矩阵见
docs/LOCAL_INTEGRATION_MATRIX.md，正式跨端业务结果仍需按
integration-slices/index.json 由双方补齐。只有全部切片通过、证据绑定负责人确认
的完整 commit，才可称为联合 G2/G3 通过。

一键执行本机所有不依赖现场的验证：

    .\scripts\run-local-validation.ps1

## 接管完成的最低记录

王昆接管完成时应在仓库提交中明确记录：

- 采用“重新实现”还是“审查并改造 AI 候选”；
- 本人确认的完整 OnboardHmi commit；
- 精确 `ProtocolReleaseIdentity`；
- Release 构建与测试结果；
- W2G-IS-00～07 每个 G2 证据位置；
- 已知限制和仍需 G3／硬件／现场验证的项目。

未留下上述人员确认时，AI 测试通过、Git Author 名称、文档齐全或模拟器演示均不能替代车载端负责人接管。
