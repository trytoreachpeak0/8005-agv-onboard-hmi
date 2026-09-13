# 车载端缺陷：装载记完结果后「修正装货」入口不出现，要等一次无关的会话事件

发现：2026-09-13，G3 `FP-IS-02` 真车载端场景 `g3-pickup-load-and-correction` 的调试运行（控制服务端仓库 `fp/b2-close@21b8d041`）
本仓被观测版本：`w2g/b3-on-v2@c86bac5`
对端：控制服务端 `fp/b2-close`（离站等待 20 秒），slots-simulator `fb5f7c5`
协议：`protocol-v1.0.0@9f22db8`

## 结论先说

装载跑完、结果被服务端确认之后，「修正装货」按钮不会立刻出现，要等到下一次与修正无关的会话事件。
控制服务端按 `REQ-0237` / ADR-cross-0054 在装载提交后让车在取货点等一个离站期限，修正只能在这段时间里开始；
而这段时间里通常没有别的会话事件，**下一次事件就是离站本身**。结果是按钮总在窗口关掉之后才出现，
`CV-LOAD-CORRECTION` 在现场不可达。

## 观测

| 时刻 | 事实 |
| --- | --- |
| 21:29:33 | 第 2 仓关门，`OperationResult` COMPLETED |
| 21:29:34 | 服务端判提交，旅程进入 `AwaitingStationDeparture` |
| 21:29:35 起 | UIA 每 0.5 秒读一次「修正装货」：不可用 |
| 21:29:55 | 离站期限到，服务端建出去关卡的单 |
| 21:29:57 | 「修正装货」变为可用；此时服务端已按离站后规则拒绝修正 |

## 机制

- `CanRequestLoadCorrection` 读 `_lastRecoveryState`（`WireToGateBusinessService.RecoveryVectors.cs`），只看其中的
  `LastCompletedLoadOperationContext`。
- 这份缓存只在两处被刷新：会话状态变化（`OnSessionStateChanged` → `RestorePendingRecoveryOperationProjectionAsync`），
  以及恢复请求自己读写日志的时候。
- 装载成功后，`MarkResultRecordedAsync` 把 `LastCompletedLoadOperationContext` 写进日志，但**没有刷新缓存**。

在 `c86bac5` 之前的服务端上这个问题看不出来：服务端在提交的同一轮就发车，发车带来的会话事件一两秒后就刷新了缓存
（同一场景 20:19:17 关门、20:19:19 按钮出现）。服务端补上离站等待之后，它才变成挡住修正的那一环。

## 修复

`HandleSlotOperationAsync`（正式装载）与恢复续作两处，在 `MarkResultRecordedAsync` 之后立刻
`ReadRecoveryStateCachedAsync`。紧随其后发布的操作员事件会让界面重算各个 `CanRequest*`。

## 为什么 G2 没抓到

G2 的假 IO（`FakeIoModuleClient`）不支持执行仓位操作，`WaitForLockerAsync` 直接抛异常，所以 G2 里没有任何测试把一次装载真跑到
「结果被记录」，也就没有人在那之后读过修正入口。

## 证据

| 项 | 结果 |
| --- | --- |
| 新增 G2 测试 `ACompletedLoadOffersTheCorrectionEntryAsSoonAsItsResultIsRecorded`（`FP-IS-02` / `CV-LOAD-CORRECTION`） | 修复前红：装载完成（开锁 1 次、`OPERATION_COMPLETED` 已发布），修正入口 2 秒内不出现；修复后绿 |
| `FakeIoModuleClient.SimulateOperatorLoad` | 新开关，默认关闭，关闭时照旧拒绝仓位操作；打开时按单元测试 `SimulationIo` 的方式模拟操作员放货关门 |
| 全量 | `SQCD.Agv.UnitTests` 200 passed，`SQCD.Agv.WireToGateG2Tests` 48 passed |

真车载端上的修正全流程在控制服务端 G3 `FP-IS-02` 的 `G3-02-07`～`G3-02-13` 里核对。
