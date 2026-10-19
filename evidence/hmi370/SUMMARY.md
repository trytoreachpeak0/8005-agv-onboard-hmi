# hmi#370 证据：agv03 2026-10-19 装货结果 UNKNOWN 后车上无出口

## 坐实（只读取自 agv03）

原件 `C:/Users/HT/AppData/Local/SQCD/8005AGV/logs/agv-20261019.log` 与日志簿
`C:/Users/HT/AppData/Local/SQCD/8005AGV/onboard-journal.db`，2026-10-19 15:30 用 `scp` 只读复制；车载程序未起停，配置未改。

- 车载端文本日志不记 `OperationResult` 本身，所以票里两条「推断」改从日志簿读：
  `agv03-journal-ff73d54f.json` 里 `operation-result:ff73d54f-…` 一行，`CreatedAt=06:37:09.786Z`（14:37:09.786 CST），
  `Acknowledged=1`，载荷 `overallOutcome=UNKNOWN`；5 号仓 `outcome=UNKNOWN`、`finalPhysicalState=EMPTY`、
  `lockState=LOCKED`、`reasonCodes=["ACTION_NOT_ALLOWED_IN_STATE"]`；6 号仓 `NOT_STARTED`。`journalCheckpoint=SAFE_FINISH_REACHED`。
- `ACTION_NOT_ALLOWED_IN_STATE` 是执行器把 `TimeoutException` 映射出来的码（`WireToGateSlotOperationExecutor.cs` `MapFailureReason`）。
  第三次开锁的 `UNLOCKING` 进度 14:37:06.571，`SAFE_FINISH` 进度 14:37:09.703，相隔约 3.13 秒，与 `unlockFeedbackTimeoutMs = 3000`
  吻合：「开锁反馈超时 → UNKNOWN」由推断变为读到（超时这一环仍是由码与时间对上的，日志没有单独一行写「超时」）。
- 恢复状态（15:30 读）：`unsettledSlotOperationAttemptId=ff73d54f-…`，`pendingResults` 仍有这条 `OperationResult`，
  `recoveryVector=null`。正是它让 `CancellableLoadInFlight` 返回 null，14:37:17 那次确认时对象变成 none。
- 15:21:44 起原因码换成 `DEPARTURE_UNSAFE`（`agv03-agv-20261019-1437-excerpt.log` 末两行），待答结果不变。

## 测试

- 红（提交 `224e135`）：`red-g2.txt`。
- 绿（修复后）：`green.txt`；另 `dotnet format SQCD_8005AGV.sln --verify-no-changes` exit=0。
