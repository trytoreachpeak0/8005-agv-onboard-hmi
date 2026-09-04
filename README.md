# 8005 AGV 车载端 HMI

> 车载端负责人接管前请先阅读 [`docs/ONBOARD_DEVELOPER_HANDOFF.md`](docs/ONBOARD_DEVELOPER_HANDOFF.md)。当前产品树已恢复到王昆的 `bc56fa9` 基线；历史中的 AI 候选提交不代表人员本人开发或批准。
>
> 第一次联调与模拟器自动化的具体工作包见 [`docs/WANG_KUN_FIRST_INTEGRATION_WORK_PACKAGE.md`](docs/WANG_KUN_FIRST_INTEGRATION_WORK_PACKAGE.md)。

本仓库是长电科技（宿迁）8005多仓位AGV车载端程序的正式开发仓库，包含车载HMI、仓门业务编排、IO通信和自动测试。

当前提交由原型仓库`SQCD_8005AGV`迁移而来，作为双方开始协议对接前的车载端初始代码基线。当前目标是先稳定跑通：

```text
到站 → 扫描SUBLOT → 规则核验 → 单仓脉冲开锁 → 人工装/卸货并关门 → IO确认 → 结果上报
```

## 当前状态

- WPF车载端、TCP JSON规则客户端和独立RuleMock均可运行。
- 支持康耐德`C2000-A2-KDDA0A0-AD6`所需的Modbus TCP FC01、FC02和FC05。
- 默认支持8仓，软件`slotIndex 0～7`对应物理`1～8号仓`。
- Release构建启用可空检查、推荐级静态分析和警告即错误。
- 具备核心安全规则和自动测试。
- RuleMock默认从“在途”开始，可通过控制台命令模拟到站和离站。
- 本机验证入口已统一为 scripts/run-local-validation.ps1：protocol G1、HMI G2
  证据、slots-simulator 18+14 黑盒测试、W2G/Legacy 边界和 UI 布局审计均可重复执行。
- 当前本机回归基线为 Release build 0 警告/0 错误、Unit 62/62、W2G G2 13/13；
  每次运行的 evidence/g2/ 目录都会绑定协议身份和 HMI 完整 commit。
- 已接入 `WIRE_TO_GATE` 会话恢复、SQLite durable outbox、服务端旅程投影、正式业务命令模型和多货位物理执行骨架；正式绑定 `protocol-v0.1.1`（精确身份见交接文档）。
- WIRE_TO_GATE 业务执行器只接受服务端冻结的货位集合；所有结果先写 outbox，再等待 `DurableAck`。

旧 TCP JSON 规则适配器仍属于早期联调协议，只用于保留现有开发能力，不代表双方最终接口；`WireToGate*` 消息面已按正式 `protocol-v0.1.1` 绑定。后续接口定义、消息示例、版本和兼容规则统一以[`8005-agv-protocol`](https://github.com/trytoreachpeak0/8005-agv-protocol)仓库的不可变 release 为准。

正式规则接口、现场IP、最终IO映射和反馈超时仍应在现场联调后冻结。所有暂定值集中在配置文件中。

## 工程结构

```text
src/
├─ SQCD.Agv.Core            领域模型、安全规则和端口接口
├─ SQCD.Agv.Contracts       临时TCP JSON协议DTO
├─ SQCD.Agv.Application     单仓作业状态机和业务编排
├─ SQCD.Agv.Infrastructure  Modbus、TCP、配置和文件日志
└─ SQCD.Agv.Wpf             车载操作界面、WIRE_TO_GATE业务桥和组合根
tools/
└─ SQCD.Agv.RuleMock        独立规则服务模拟器
tests/
└─ SQCD.Agv.UnitTests       核心自动测试
```

依赖约束和安全设计详见[架构与开发约定](docs/ARCHITECTURE.md)。

## 开发环境

- Windows 10/11 x64
- .NET 8 Desktop Runtime
- Visual Studio 2022或支持.NET 8的更高版本

当前机器可使用较新的.NET SDK构建，但所有项目目标框架固定为.NET 8。

## 构建与测试

在仓库根目录执行：

```powershell
dotnet build .\SQCD_8005AGV.sln -c Release
dotnet test .\SQCD_8005AGV.sln -c Release
dotnet format .\SQCD_8005AGV.sln --verify-no-changes --no-restore
```

本机完整验证（不需要现场硬件或真实 ControlServer）：

    .\scripts\run-local-validation.ps1

也可以直接使用Visual Studio的“发布”功能。命令行发布车载端示例：

```powershell
dotnet publish .\src\SQCD.Agv.Wpf\SQCD.Agv.Wpf.csproj `
  -c Release -r win-x64 --self-contained false
```

发布后必须把正式现场参数写入发布目录中的`appsettings.json`，并确认不再使用localhost或示例占位地址。生产配置模板见`src/SQCD.Agv.Wpf/appsettings.Production.example.json`；RuleMock不应复制到车载端生产目录。

## 本地联调

如果需要同时启动已经实现 HTTP 测试控制面的 `slots-simulator`，可以从本仓库执行：

```powershell
.\scripts\run-first-integration.ps1 -StartHmi
```

该脚本只启动 simulator 的 Modbus 数据面（`127.0.0.1:1502`）和 loopback HTTP 控制面（`127.0.0.1:58006`），不会伪造或替代 ControlServer。HTTP 只能改变测试环境中的货物、关门和故障状态，HMI 仍只能通过 Modbus 操作仓门。

1. 启动IO仿真软件并执行“安全Reset”，确认监听`127.0.0.1:1502`、Unit ID为255。
2. 启动规则MOCK：

   ```powershell
   dotnet run --project .\tools\SQCD.Agv.RuleMock\SQCD.Agv.RuleMock.csproj
   ```

3. 启动车载端：

   ```powershell
   dotnet run --project .\src\SQCD.Agv.Wpf\SQCD.Agv.Wpf.csproj
   ```

4. 此时RuleMock与车载端已经连接，但车辆仍为“在途”，车载端应显示“等待到站”。在RuleMock控制台输入：

   ```text
   arrive
   ```

   车载端收到`visit.started`后才进入“可扫码”。输入`depart`可发送`visit.ended`并重新进入在途状态，`status`可查看MOCK状态。

5. 默认RuleMock为8个仓位分别提供装料和卸料规则：

   - `LOAD-001`～`LOAD-008`：分别向1～8号仓装料。
   - `UNLOAD-001`～`UNLOAD-008`：分别从1～8号仓卸料。

SUBLOT后三位编号与物理仓位号保持一致。例如，扫描`LOAD-003`向3号仓装料，扫描`UNLOAD-003`从3号仓卸料。

车载端冷启动时要求DO全0、仓门全锁且仓位全空。测试卸料时，应先完成对应仓位的装料流程，或在IO仿真软件中为对应仓位放入货物，然后扫描相同编号的`UNLOAD-xxx`。

## 配置文件

- 车载端：[appsettings.json](src/SQCD.Agv.Wpf/appsettings.json)
- 规则MOCK：[rulemock.settings.json](tools/SQCD.Agv.RuleMock/rulemock.settings.json)

生产部署前至少确认并修改：

- AGV编号和车载实例编号。
- WIRE_TO_GATE 运行时必须提供 `CONTROL_SERVER_OPERATOR_ID`（或配置中指定的操作员环境变量），用于 `SublotSubmitted.operator` 的会话身份。
- 规则模块IP、端口和正式协议适配器。
- IO模块IP、端口、Unit ID。
- DO/DI基地址和8仓逐点映射。
- 开锁反馈、人工操作和反馈稳定窗口超时。

## 安全底线

- 车载端只发送一次FC05 ON触发开锁，绝不把软件写OFF当作关门。
- 开锁写入结果未知或反馈超时后不自动重发脉冲。
- 同一时间只允许一个活动扫码请求和一个仓位操作。
- `operationId`在写DO前登记，重复操作不会再次开锁。
- IO离线、规则离线、反馈未知、仓门未锁或仓位货物状态不符时禁止新开锁。
- IO快照过期、任一开锁DO不为0或任一仓门未锁时禁止新开锁和发车。
- 每次开锁后必须确认硬件脉冲对应DO已经自动恢复0；操作成功前再次检查全部DO和仓门。
- 仓门由人员手动关闭；只有锁反馈和光幕同时满足预期才上报成功。
- 任一仓门未锁或存在活动操作时，`departurePermitted=false`。
- 结果未收到ACK时保留原messageId，连接恢复后自动重报；重报只发送结果，不会再次开锁。
- 严重界面异常会锁存安全故障并取消当前活动流程，普通状态更新不能覆盖该故障。
- 启动时仅因仓内有货进入故障时，可由维护人员确认DO全0、仓门全锁后执行“启动安全复核”；复核不会清空货物状态。

## 当前已知限制

- TCP JSON是正式接口冻结前的临时适配器。
- 旧 TCP JSON 规则适配器仍保留给开发环境；生产 WIRE_TO_GATE 业务权威必须使用 ControlServer 正式消息流。
- WIRE_TO_GATE 已支持服务端多货位命令的串行安全执行和 durable outbox，但真实 ControlServer 的业务派发、结果 reconciliation、异常恢复和现场车辆停稳信号仍需联调。
- Production 配置启动时会拒绝 ControlServer 回环/示例地址、占位 commit、示例身份和
  缺失的凭据/操作员环境变量；车辆安全投影不可用、身份不匹配或证据过期时固定报告
  UNKNOWN，所有危险动作保持 fail-closed。
- WIRE_TO_GATE 与车辆安全投影固定使用明文 TCP/HTTP，不提供 TLS 开关。厂内网络抓包者
  可取得并重放 credentialProof/Bearer 凭据，车辆 STOPPED 投影也可能被中间人篡改；
  ControlServer 的 health/version 端点会随 HTTP 服务暴露。这是已接受的安全与功能安全限制，
  现场升级必须保证 OnboardHmi 与 ControlServer 同时切换到明文版本。
- IS-07 本机只验收授权/持久化/新鲜物理事实的安全闸门，正式恢复 session、人工/
  机械动作、硬件恢复和 ControlServer 业务结果仍不能由本机伪造完成。
- 断线时未能确认物理安全收尾的活动货位会保留在 journal 中并保持阻塞，不能自动扩展或重新开锁。
- 未包含任务调度、路径规划、MES直连、刷卡会话和复杂异常恢复。
- 生产规则地址、IO地址和正式协议仍需与项目同事确认；当前开发配置不得直接用于现场。
