# 车载端缺口：强制机械恢复与充电后返回服务没有操作员入口

发现：2026-09-14，为控制服务端 G3 `FP-IS-07` 设计真车载端场景时的界面走查
本仓被观测版本：`w2g/b3-on-v2@8c83265`（产品代码同 `3fb8a6e`）
协议：`protocol-v1.0.0@9f22db8`

## 结论先说

`FP-IS-07` 的六条向量里，有两条在车载端界面上没有任何入口：

- `CV-FORCED-MECHANICAL-RECOVERY`：业务层早有 `CanRequestForcedMechanicalRecovery` / `RequestForcedMechanicalRecoveryAsync`（票 21 补的命令与结果一半，G2 有测试），
  但 `MainWindow.xaml` 没有按钮，`MainViewModel` 与 `App.xaml.cs` 也没有接线，操作员发不起这条向量。
- `CV-MANUAL-CHARGING-RETURN`：只有会话客户端 `RequestManualChargingReturnToServiceAsync` 能发请求，业务层没有方法，界面没有按钮。

规格 8.3 要求批次 2 轨 A 的 `FP-IS-07` 过 G3，G3 驱动的是真车载端界面，所以这两条向量在 G3 上根本到不了。
2026-09-14 用户裁定：补车载端入口，六条向量全走界面。

## 修复

- `WireToGateBusinessService.ManualChargingReturn.cs`（新）：
  - `CanRequestManualChargingReturnToService`：与恢复入口同一道门——恢复入口开关打开、会话在线且为 `READY` 或 `RECOVERY_REQUIRED`、
    操作员号与管理员凭据都已配置（`REQUIRE_VERIFIED_ADMINISTRATOR` 的车载端一半）。
  - `RequestManualChargingReturnToServiceAsync`：以配置的管理员身份与角色发 `ManualChargingReturnToServiceRequested`（凭据只核对已配置，报文没有凭据字段），
    服务端受理发 `MANUAL_CHARGING_RETURN_ACCEPTED` 记录，拒绝或失败发 `RECOVERY_BLOCKED` 记录并返回 `false`。
    **从不改动本地的手动充电保持**（`NEVER_CLEAR_HOLD_LOCALLY`）：保持只随服务端下一份车辆业务状态快照变化。
- 界面：`MainWindow.xaml` 在「故障交接」之后加「强制机械恢复」「充电后返回服务」两个按钮，显隐与可用绑定各自的 `CanRequest*`；
  `MainWindow.xaml.cs` 各加一个确认对话框（标题与按钮同名）和失败提示；`MainViewModel` 与 `App.xaml.cs` 按「故障交接」的写法接线，
  设备故障时两个入口一并收起；`MANUAL_CHARGING_RETURN_ACCEPTED` 记为成功级日志。

## 证据

| 项 | 结果 |
| --- | --- |
| 新增 G2 测试 `TheManualChargingReturnEntrySendsTheAdministratorAndLeavesTheHoldToTheServer`（`FP-IS-07` / `CV-MANUAL-CHARGING-RETURN`，受理与拒绝两例） | 修复前红：业务层没有 `CanRequestManualChargingReturnToService` / `RequestManualChargingReturnToServiceAsync`，编译失败（CS1061 ×4）；修复后绿：请求带管理员号、角色、原因与电量，消息号即请求号；两例的本地保持都仍为 `true` |
| 新增 G2 测试 `TheManualChargingReturnEntryIsNotOfferedWithoutAVerifiedAdministrator` | 同上修复前编译失败；修复后绿：未配置管理员凭据时入口不可用、按下也不发请求 |
| `FakeControlServer.ManualChargingHoldInSnapshots` | 新开关：车辆业务状态快照带手动充电保持 |
| 界面按钮与接线 | 无单元测试可覆盖 XAML；由控制服务端 G3 `FP-IS-07` 在真车载端上用 UI Automation 点击驱动 |
| 控制服务端 L2 `g3-forced-mechanical-recovery`（真车载端 `f14f8af`；调试运行 `forced-001`，证据未入库） | 5/5 PASS：「强制机械恢复」按钮出现并可点，确认后发出 `RecoveryActionSubmitted(FORCED_MECHANICAL_RECOVERY)`，收到命令后报 `MECHANICALLY_ISOLATED`、代数与命令一致、两项证明均为 `false`，未开锁 |
| 控制服务端 L2 `g3-manual-charging-return`（真车载端 `f14f8af`；调试运行 `manual-005`，证据未入库） | 4/4 PASS：「充电后返回服务」按钮出现并可点；会话需恢复时申请被拒（`SESSION_RECOVERY_REQUIRED`），就绪后申请被受理（`RETURNED_TO_ELIGIBILITY_EVALUATION`）；两次请求都带 `L2-OPERATOR` 与 `MAINTENANCE_ADMINISTRATOR`；无任何业务或物理副作用。此前 `manual-001`～`004` 的失败都是场景脚本的写法问题，产品侧四次判定均正确 |
| 全量 | `SQCD.Agv.UnitTests` 200 passed，`SQCD.Agv.WireToGateG2Tests` 54 passed，`dotnet format --verify-no-changes` 通过 |
