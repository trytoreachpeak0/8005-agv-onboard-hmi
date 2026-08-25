# 8005 AGV 车载端 HMI

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
- `OnboardHmi_MVP` 已绑定未批准协议候选 `72ddde5` / manifest `e878d8…35f2e`，并建立正式 `ISlotIoProvider` 与原型 A 映射入口；旧协议和单仓路径尚未完成迁移。

当前分支还包含候选五步恢复客户端、SQLite `OnboardExecutionJournal`、多仓物理闭环执行器、独立 HTTP IO Simulator、Fake ControlServer/Conformance 工具，以及按获选原型 A 重建的 WPF“左旅程—中央唯一动作—右固定八仓”生产窗口。候选身份固定为 protocol commit `72ddde595165468520d9f3a46b25e4aa4eec0c3f`、manifest SHA-256 `e878d89e820535fe1eb64b85681b9c2994fb98646309e6ba768219c5c8735f2e`，状态仍为 `CANDIDATE_UNAPPROVED`。

独立 IO Simulator 默认监听 `127.0.0.1:58006`，初始 8 仓均为 `online + EMPTY + LOCKED + RESET + CLEAR`：

```powershell
dotnet run --project .\tools\SQCD.Agv.IoSimulator -c Release
```

它提供 `/api/v1/slots`、`/api/v1/unlock`、逐仓状态注入及 `Offline / ResponseTimeout / Unknown / LockFeedbackAbnormal / OutputStuck / RequestLost / ResponseLost / Crashed` 故障模式。模拟器只证明软件闭环，不代表真实 IO 协议、接线、锁、光幕或硬件资格。

Fake ControlServer 与 Conformance 是显式本地自测入口；凭据只从 `CONTROL_SERVER_ONBOARD_CREDENTIAL` 读取：

```powershell
$env:CONTROL_SERVER_ONBOARD_CREDENTIAL = '<local-test-secret>'
dotnet run --project .\tools\SQCD.Agv.FakeControlServer -c Release -- 58015
dotnet run --project .\tools\SQCD.Agv.Conformance -c Release -- 127.0.0.1 58015 http://127.0.0.1:58006 <new-journal-path>
```

生产启动保留既有 `OnboardController` 安全锁存，并通过 `SlotIoModuleClientAdapter` 只消费正式业务状态 Provider；同时会使用配置的 ControlServer 地址和环境变量凭据建立候选五步恢复会话。只有既有执行链可操作、候选会话在线且候选业务状态为 `READY` 时才允许扫码；候选心跳丢失会立即锁存故障并禁止新操作。软件急停请求和目标车辆部署仍须在目标适配/部署阶段完成。未完成真实 ControlServer 命令分发、RIoT/车辆/IO 集成、Golden WPF 用户预览、G3 或协议批准，不能据此宣称整个 MVP 或真实现场闭环完成。

当前`SQCD.Agv.Contracts`和TCP JSON消息属于早期联调协议，只用于保留现有可运行能力，不代表双方最终接口。后续接口定义、消息示例、版本和兼容规则统一以[`8005-agv-protocol`](https://github.com/trytoreachpeak0/8005-agv-protocol)仓库为准，并按该仓库逐步发布的协议增量开发。

正式规则接口、现场IP、最终IO映射和反馈超时仍应在现场联调后冻结。所有暂定值集中在配置文件中。

## 工程结构

```text
src/
├─ SQCD.Agv.Core            领域模型、安全规则和端口接口
├─ SQCD.Agv.Contracts       临时TCP JSON协议DTO
├─ SQCD.Agv.Application     单仓作业状态机和业务编排
├─ SQCD.Agv.Infrastructure  Modbus、TCP、配置和文件日志
└─ SQCD.Agv.Wpf             车载操作界面和组合根
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
dotnet test .\tests\SQCD.Agv.UnitTests\SQCD.Agv.UnitTests.csproj -c Release
```

可复现构建要求 `global.json` 指定的 SDK `8.0.424`；不得使用 9.x SDK 冒充冻结版本。若 SDK 未加入 `PATH`，可把 `WIRE_TO_GATE_DOTNET_EXE` 指向该版本的 `dotnet.exe` 后运行脚本。薄实施入口见 [`docs/ai-spec/README.md`](docs/ai-spec/README.md)。

也可以直接使用Visual Studio的“发布”功能。命令行发布车载端示例：

```powershell
dotnet publish .\src\SQCD.Agv.Wpf\SQCD.Agv.Wpf.csproj `
  -c Release -r win-x64 --self-contained false
```

发布后必须把正式现场参数写入发布目录中的`appsettings.json`，并确认不再使用localhost或示例占位地址。生产配置模板见`src/SQCD.Agv.Wpf/appsettings.Production.example.json`；RuleMock不应复制到车载端生产目录。

## 本地联调

1. 启动正式 HTTP IO Simulator，确认监听 `127.0.0.1:58006`；初始状态已是八仓安全 Reset。
2. 如需回归旧单仓流程，再启动兼容 RuleMock（它不代表候选 ControlServer）：

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

- 旧 `SQCD.Agv.Contracts` / RuleMock 仍只用于回归旧单仓流程；候选会话、Fake ControlServer 与 Conformance 是另一条明确隔离的入口。
- 生产启动已接入候选五步恢复与活动心跳，并以候选 `READY` 作为扫码门禁；`WireToGateSlotExecutor` 已支持完整目标集合、持久去重和重启恢复。真实 ControlServer 的 Demand/站点/批量仓位命令分发尚未替换旧规则执行链，必须在双仓联合集成中完成后才能称为候选协议端到端。
- 新原型 A 窗口已成为生产启动窗口；尚未运行需用户单独授权的 Golden WPF tier 2/3，也未取得目标 1024×768 车载硬件的视觉批准。
- Modbus Provider 保留给真实硬件适配；本轮默认 HTTP Simulator 不证明现场模块、接线或极性资格。
- 未包含任务调度、路径规划、MES直连、刷卡会话或未经协议批准的异常动作。
