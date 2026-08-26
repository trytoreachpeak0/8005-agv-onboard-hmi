# 本机异常恢复安全矩阵

本机实现已经把异常恢复的物理安全边界集中到
WireToGateRecoverySafetyPolicy。普通 UI 或未授权的服务端恢复消息不能直接
触发开锁、机械强制恢复或回充放行。

| 条件 | 本机结果 |
| --- | --- |
| 未建立并授权 exception recovery session | 拒绝，RECOVERY_AUTHORIZATION_REQUIRED |
| journal 没有持久化恢复检查点 | 拒绝，RECOVERY_STATE_NOT_PERSISTED |
| 停稳信号缺失、未知或过期 | 拒绝，VEHICLE_STATE_UNKNOWN |
| 车辆移动 | 拒绝，ACTION_NOT_ALLOWED_IN_STATE |
| 目标仓位未知 | 拒绝，SLOT_STATE_UNKNOWN |
| 仓门未锁 | 拒绝，LOCK_NOT_CLOSED |
| 开锁输出未复位 | 拒绝，UNLOCK_OUTPUT_NOT_RESET |
| 全部条件满足 | 仅策略层允许；仍需正式协议恢复命令、持久化和对应现场硬件适配 |

当前本机没有伪造 ControlServer 的恢复授权、MES 事实、人工/机械操作或车辆
信号。因此 IS-07 的服务端业务消息和现场物理结果仍是外部门禁；本机验收标准是
危险条件全部 fail-closed、无普通 UI 旁路、journal 状态可恢复。
