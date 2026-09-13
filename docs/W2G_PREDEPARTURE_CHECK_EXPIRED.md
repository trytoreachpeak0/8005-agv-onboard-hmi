# 车载端缺陷：询问旧安全版本的出发前检查照常作答，从不回 PREDEPARTURE_CHECK_EXPIRED

发现：2026-09-13，为控制服务端 G3 `FP-IS-03` 设计场景时对照协议向量 `CV-PREDEPARTURE-SAFETY-EXPIRES` 的代码走查
本仓被观测版本：`w2g/b3-on-v2@26a9a96`（产品代码同 `8a2fed9`）
对端：控制服务端 `fp/b2-close@6c252816`（同日服务端一半，缺陷单 `docs/defects/20260913-expired-predeparture-check-never-asked-again.md`）
协议：`protocol-v1.0.0@9f22db8`

## 结论先说

向量 `CV-PREDEPARTURE-SAFETY-EXPIRES` 的第四条消息是 `ProtocolProblem`，稳定错误码 `PREDEPARTURE_CHECK_EXPIRED`。
两端都没有任何代码发它：本仓只在「允许收到的原因码」白名单里列了它，服务端收到 `ProtocolProblem` 只记日志。
协议也没写由哪一端在什么时候发。

2026-09-13 用户裁定两端按向量补齐。车载端这一半：**收到的检查询问的安全版本，已经低于本端被服务端接受的版本时，
不作答，回 `ProtocolProblem(PREDEPARTURE_CHECK_EXPIRED)`，会话不断。**服务端同日改为检查失效后作废并以新身份、按当前版本重新发起。

## 为什么是车载端发

- `PreDepartureSafetyCheck` 携带 `expectedSafetyStateVersion`；车载端知道自己最新被接受的安全版本（`SafetyStateChanged` 收到 `DurableAck` 后前进）。
  两者一比就知道这张检查是否已被安全状态的变化取代。
- 由服务端事后发不通：车载端的检查结果等的是 `DurableAck`，服务端收到即确认；之后再来一条关联它的 `ProtocolProblem`，
  本仓找不到等待者，走「未处理消息」路径断开会话。
- 服务端每一轮会重放尚未作废的旧检查。车载端安全版本已前进时，正是这次重放引出向量顺序里的
  `Check → Result → SafetyStateChanged → ProtocolProblem`。

## 修复

- `WireToGateBusinessService.HandlePreDepartureSafetyCheckAsync`：`ExpectedSafetyStateVersion < Current.SafetyStateVersion` 时记一条警告，
  `RejectServerCommandAsync(command, "PREDEPARTURE_CHECK_EXPIRED")`，返回，不评估安全、不发结果。
- `WireToGateSessionClient.RejectServerCommandAsync` / `WireToGateSessionService.RejectServerCommandAsync`：新增。发一条关联到该命令的
  `ProtocolProblem` 并保持会话。原先的解析期拒收（`SendProtocolProblemAsync` 之后抛出、断开）不变，两者共用 `CreateProtocolProblem`。
- 询问版本不低于本端版本的检查照旧作答。

## 证据

| 项 | 结果 |
| --- | --- |
| 新增 G2 测试 `ACheckAskedAboutAnOlderSafetyStateIsRefusedAsExpiredWithoutEndingTheSession`（`FP-IS-03` / `CV-PREDEPARTURE-SAFETY-EXPIRES`，两例） | 修复前：询问版本 0 的一例红（等不到 `ProtocolProblem`），询问版本 1000000 的对照例绿；修复后两例绿，前者回 `PREDEPARTURE_CHECK_EXPIRED`、不发结果、会话仍连着 |
| `FakeControlServer.PreDepartureSafetyCheckExpectedVersionAfterRecovery` | 新开关：握手后发一条询问指定版本的检查 |
| 全量 | `SQCD.Agv.UnitTests` 200 passed，`SQCD.Agv.WireToGateG2Tests` 50 passed |

真车载端上的完整顺序由控制服务端 G3 `FP-IS-03` 核对。
