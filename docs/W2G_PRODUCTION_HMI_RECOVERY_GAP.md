# WIRE_TO_GATE 生产 HMI 状态与恢复边界（2026-08-30）

## 已在车载端修复

- WIRE_TO_GATE 模式的主横幅和“任务系统”状态改由正式上层会话驱动，不再依赖恒为离线的
  `DisabledRuleGateway`。
- `SublotEntryRequested`、子批提交、仓位操作阶段、结果确认、恢复阻断和可靠重放均投影到操作记录。
- 批量仓位操作会在八仓卡片上同时标识全部目标仓位；失败后保持“需要管理员恢复”，不会退回误导性的
  “连接中”。
- 重复 `SlotOperationCommand` 继续只重放首个 `OperationResult`，不会执行第二次仓门 IO。
- `SlotOperationResumeCommand` 只在正式服务端恢复授权、精确匹配的 journal attempt/checkpoint、车辆停稳、
  信号新鲜、目标仓位已知且上锁、输出复位时通过本地安全求值。

## 当前不能由车载端单独完成的恢复收敛

当前正式协议的 `SlotOperationResumeCommand` 复用原 `slotOperationAttemptId`，且没有独立的
`SlotOperationResumeResult`。ControlServer 当前又以
`(slotOperationAttemptId, forcedRecoveryGeneration)` 唯一接受 `OperationResult`：

1. 原失败或 UNKNOWN 的 `OperationResult` 已被持久接受并令站点操作进入 `RecoveryRequired`；
2. `RESUME_AFTER_REPAIR` 不推进 `forcedRecoveryGeneration`；
3. 修复物理状态后若车载端发送同 attempt 的新结果，服务端会判为不同内容冲突；
4. 若重放原结果，服务端按 replay 忽略，恢复 workflow 也不会收敛。

因此在双方确定新的结果身份或服务端允许“已授权恢复结果替代”之前，车载端必须保持 fail-closed，不能
因为收到恢复命令就再次开门。当前 HMI 会明确显示该门禁，而不是静默丢弃。

## 需要双方确认的一项接口决定

以下方案必须选定一个，之后车载端才能实现真实续作：

1. `SlotOperationResumeCommand` 提供新的 operation/result identity；或
2. ControlServer 在活动的 `recoveryActionId` 下允许同 attempt 的授权替代结果；或
3. 协议增加独立的 resume result，并明确它如何关闭原 `OperationResult`。

决定后还需新增带 demand 的联合回归：原操作超时 → 打开异常恢复会话 → 管理员授权 → 新命令持久化 →
车载端安全检查 → 恢复动作 → 新结果被唯一接受 → journey 离开 `Blocked`。证据必须使用新数据库和新
journal，不能继承 2026-08-30 的红运行。
