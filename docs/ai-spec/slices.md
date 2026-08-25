# W2G-IS-00～07 车载切片

| Slice | 权威输入 | 最小持久/接口边界 | 失败/恢复 | 验收 | 禁止副作用 |
| --- | --- | --- | --- | --- | --- |
| 00 | Session/Capability/Safety/Recovery schemas | TLS pin、VehicleCredential、session generation、journal 与待补报结果 | 身份错配/旧代次拒绝；恢复中再断线重入五步对账 | READY 前无业务动作 | TCP 在线即 READY；只比 ProtocolVersion |
| 01 | 服务端唯一 Demand/旅程投影 | 只整体采用带 revision 的业务投影 | 旧/冲突 revision 拒绝或请求快照 | 只读显示已承诺旅程 | 读 MesIngest；本地选单/改站 |
| 02 | Sublot/SlotOperation schemas | journal PREPARED 后才通过 ISlotIoProvider 开锁；完整有序仓位集 | 相反占用重做同目标；UNKNOWN 恢复；取消/纠错保留身份 | 多仓 OCCUPIED/锁闭/复位后一个可靠结果 | 手填篮数/换仓；部分成功 |
| 03 | PreDepartureSafetyCheck | 每次实时读取 8 仓并返回带版本结果 | 状态变化可靠撤销；UNKNOWN 不安全 | 新鲜 SAFE 结果 | 调 RIoT；复用旧安全结果 |
| 04 | 服务端完整 UNLOAD | 无二次输入；逐仓 EMPTY/锁闭/复位并可靠补报 | 单仓问题不回滚已清空证据；UNKNOWN 恢复 | 全部目标闭环 | 扫 Sublot/工号；等 PDA/MES |
| 05 | 连接丢失向量 | journal 记录已发送/未知模块组；不扩张 ActiveUnlockSet | 仅安全收尾后暂停，重连等显式授权 | 各断点唯一收敛 | 断线开未发组；自动取消 |
| 06 | delivery/dedup 向量 | command/content hash、首结果、待 ACK MessageId 持久化 | 同内容重放原状态/结果；异内容冲突 | drop/delay/duplicate 只一次 IO | 内存去重；重试开门 |
| 07 | recovery/exception 向量 | 每个 ProvenRecoveryCheckpoint 与 ForcedRecoveryGeneration 持久化 | 重启重读实时 IO；唯一继续，否则 RECOVERY_REQUIRED | 无重复开锁/结果，四路径可追溯 | 用旧 IO 覆盖实时；从头执行 |
