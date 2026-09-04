# 架构与开发约定

## 1. 分层边界

```text
WPF组合根 ───────→ Application ───────→ Core
    │                                      ↑
    └────────────→ Infrastructure ─────────┘
                         │
                         └──────────────→ Contracts

RuleMock ───────────────────────────────→ Contracts
```

- `Core`不得引用UI、网络、文件系统或具体设备库。
- `Application`只依赖`IIoModuleClient`、`IRuleGateway`和领域模型。
- `Infrastructure`负责把Modbus原始位和TCP JSON转换为领域语义。
- `Wpf`是组合根；ViewModel不能出现Modbus地址、TCP报文字段或硬件极性判断。
- `RuleMock`是独立进程，车载端项目不得引用它。

## 2. 单仓操作时序

```text
扫码
  ↓
规则核验并返回 operationId / slotIndex / Load或Unload
  ↓
检查规则连接、IO连接、仓门锁反馈和当前货物状态
  ↓
先登记 operationId 已开始
  ↓
FC05向目标DO写ON一次（硬件脉冲自动复位）
  ↓
等待锁反馈DI从1变0并保持稳定
  ↓
等待目标DO自动恢复0并保持稳定
  ↓
提示操作员装/卸货并手动关门
  ↓
等待锁反馈DI=1，且光幕符合最终货物预期
  ↓
再次确认8个DO全0、8个仓门全锁
  ↓
上报结果并等待ACK
```

DO自动恢复0只代表开锁输出脉冲结束，不代表仓门已经关闭。

## 3. 状态和并发

全局状态为：

```text
Starting → Connecting → WaitingArrival → ReadyToScan
                                      ↓
                Faulted ← Reporting ← Operating ← Verifying
```

- `SemaphoreSlim`保证本地同一时刻只有一个扫码编排。
- `operationId`集合保证同一业务操作在进程内只触发一次物理开锁。
- Modbus传输使用独占锁，避免轮询和写操作交叉破坏请求/响应顺序。
- TCP JSON发送使用独占锁；响应通过`correlationId`匹配未决请求。
- 网络断开会使所有未决请求失败，并由后台连接循环重连。

## 4. IO语义

| 信号 | 原始值 | 领域语义 |
| --- | ---: | --- |
| 开锁DO | 1 | 继电器闭合，触发硬件开锁脉冲 |
| 开锁DO | 0 | 继电器断开 |
| 锁反馈DI | 1 | 已锁 |
| 锁反馈DI | 0 | 未锁/已开 |
| 光幕DI | 1 | 无货 |
| 光幕DI | 0 | 有货 |

所有地址、通道映射和轮询参数均从配置加载。业务代码只使用`IsLocked`和`HasCargo`。

## 5. 异常策略

- 开锁FC05失败或超时：结果视为未知，禁止自动重发。
- 开锁后锁反馈未变0：上报失败并进入`Faulted`。
- 锁反馈已变0但开锁DO未按时自动恢复0：上报失败并进入`Faulted`，禁止继续操作和发车。
- 人工操作超时或关门/货物不符合预期：上报失败并进入`Faulted`。
- 物理操作完成但结果ACK丢失：保留完整结果和原`messageId`；规则连接恢复后自动重报，也允许操作员手动重报，绝不重新开锁。
- 严重界面异常会锁存为不可被普通状态覆盖的安全故障，取消当前活动流程并禁止后续开锁。
- 程序冷启动快照不满足“DO全0、仓门全锁、仓位全空”：不覆盖现场状态，进入`Faulted`。
- 若冷启动仅因现场已有货物不满足“全空”，维护人员可在确认DO全0、仓门全锁、反馈新鲜后执行启动安全复核；保留货物事实，不清空IO。
- 规则或IO断线：空闲状态禁止扫码；活动操作保留安全引导和最终状态。
- RuleMock默认以“在途”启动，`arrive`发送`visit.started`，`depart`发送`visit.ended`，用于验证未到站禁止扫码。

## 6. 编码规范

- 目标框架为.NET 8，启用可空引用类型和隐式using。
- Release和Debug均将警告视为错误，并启用推荐级代码分析。
- 公共异步API必须接受`CancellationToken`或明确说明生命周期。
- 不使用`.Result`或`.Wait()`处理正常业务异步流程；WPF退出清理是唯一同步边界。
- 时间戳使用`DateTimeOffset`，网络协议使用ISO 8601带时区格式。
- 配置启动时集中校验，禁止在ViewModel中硬编码现场参数。
- 8个仓位的DO必须互不重复，16个锁反馈/光幕DI必须互不重复。
- 日志应包含`visitId`、`operationId`、仓位和错误码等关联信息。
- 修改安全规则、协议映射或IO解释时必须同步增加自动测试。

## 7. 合入前检查

```powershell
dotnet build .\SQCD_8005AGV.sln -c Release
dotnet test .\tests\SQCD.Agv.UnitTests\SQCD.Agv.UnitTests.csproj -c Release --no-build
```

不得通过关闭可空检查、静态分析或`TreatWarningsAsErrors`来绕过问题。
