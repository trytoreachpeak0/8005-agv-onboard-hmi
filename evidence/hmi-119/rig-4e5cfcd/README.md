# hmi#119 真装置时段证据（车载端 `4e5cfcd`）

2026-09-19 15:49 起的真装置时段。三个提交：服务端 `aa17b059`（`fp/v2-impl` 顶端，含 cs#187 的 `c1252932`），车载端 `4e5cfcd`（本分支 merge hmi#120 之后），模拟器 `fb5f7c5`。

| 目录 | 内容 | 结论 |
| --- | --- | --- |
| `onboard-hmi-g2/` | 全量 `ONBOARD_HMI_G2`（`scripts/run-w2g-g2.ps1 -SkipProtocolG1`）的 `summary.json`、日志与测试结果 | PASS：Release 构建、UnitTests 387、G2Tests 200、`dotnet format`、出站 schema 3282 行 0 违规 |
| `ui-layout-20260919T075433051Z.json` | `scripts/check-ui-layout.ps1` | PASS |
| `compensate-then-reconnect/` | 服务端仓 `scripts/l2/Invoke-L2Scenario.ps1 -Scenario real-onboard-compensate-then-reconnect` | PASS（8/8） |
| `pairing-cs187-001/` | 与 control-server#187 的配对验证 | P-00～P-06 PASS，P-07 FAIL（见下） |
| `pairing-scenario/` | 配对验证用的一次性场景脚本与装置配置，原样副本 | — |

## 配对验证怎么复现

服务端仓没有能让续行命令在车上被挡住的现成场景，所以写了一个一次性场景，不进服务端仓。

1. 把服务端仓的 `scripts/l2/` 与 `scripts/DesktopLock.psm1` 复制到任意目录 `<rig>/scripts/`，把 `pairing-scenario/` 里的两个文件放进 `<rig>/scripts/l2/scenarios/`。
2. 三个克隆按上面三个提交 detach 检出，保持干净。
3. 运行：

   ```
   pwsh -File <rig>/scripts/l2/Invoke-L2Scenario.ps1 -Scenario hmi119-resume-rejected-pairing -EvidenceRoot <新目录> -Repository <服务端克隆> -OnboardRepository <车载端克隆> -SimulatorRepository <模拟器克隆> -BatchId batch-6
   ```

   `-Repository` 等三个参数让产品代码仍取自三个克隆，副本只提供场景脚本。端口锁与桌面锁照常获取。

场景做的事：

- 前半段照服务端仓的 `g3-exception-resume`：装载以 UNKNOWN 结束，两次重启。第二次重启的恢复状态报告带着服务端授权 `RESUME_AFTER_REPAIR` 要的已证实断点。
- 检查四个管理员恢复入口（P-00）。
- 经模拟器 `PUT /slots/{n}/lock-feedback-override {mode: FIXED_0}`，把装载仓的锁反馈钉成 0（未锁）。车载端门禁因此以 `LOCK_NOT_CLOSED` 挡住续行命令。仓门与货物的物理事实不变。
- 按「申请恢复」，判 P-01～P-05。
- 把覆盖改回 `AUTO`，按「补偿清空」，判 P-06、P-07。

## P-07 为什么红

按「补偿清空」之后，车一条消息都没发。车载端日志（`pairing-cs187-001/logs/onboard-app/agv-20260919.log`）原文：

```
恢复向量LOAD_COMPENSATION未执行：reason=RECOVERY_SESSION_STATE_PENDING。
```

原因：车日志里的 `ExceptionRecoverySessionId` 只在结果记下时才清。会话「没有结果就 CLOSED」这条路要到 cs#187 之后才走得到，于是已关闭会话的 id 一直留着，每个恢复入口都在本地拒绝。修复与测试见本分支 `fix(w2g): 拒绝续行后清掉那次恢复会话与动作的记录，保留未结算操作`，红绿证据是 `../red/10-*`、`../green/10-*`。修复后的重跑证据另起目录。
