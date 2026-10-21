# hmi#391 证据

- `red/console.txt`：测试提交 `849b608`（实现之前，只有够编译的桩）。6 条新用例全红：被拒时屏上是兜底句
  「操作被拒绝（RECOVERY_AUTHENTICATION_FAILED）。请核对后重试……」，会话被以该码关闭时是裸码
  「服务端恢复会话已关闭（RECOVERY_AUTHENTICATION_FAILED）。」；同一理论的三行旧数据照旧通过。
- `green/console.txt`：实现提交 `1bfddb3` 上，新用例、关闭原因三条旧用例、屏幕快照与 G2 架构守卫，53 条全绿，退出码 0
  （收尾的出站 schema 检查也过）。
- `green/unit-tests.txt`：单元测试项目整体 1312 条全绿。
- `green/wider-g2.txt`：`RecoveryVectorG2Tests`、`MultiDemandJourneyG2Tests`、`DoorShutOverOppositeOccupancyG2Tests` 与
  `*ViewModelTests` 557 条全绿，退出码 0。
- `green/format-verify.txt`：`dotnet format ./SQCD_8005AGV.sln --verify-no-changes --no-restore`，退出码 0，无输出。
- `mutation/`：在 `1bfddb3` 上逐个改坏一处、跑对应用例、还原（`mutate.sh`，每次只改一个文件）：
  - M2 被拒时记一次按键：`AResumeRefused…` 红在按键计数（期望 0，实为 1）。
  - M3 开了会话不撤提示：`OnceAuthorized…` 红在 `IsRecoveryNotOpen`（期望 False）。
  - M4 补偿被拒不换文案：`ACompensationRefused…` 红在句子断言。这条用例写在实现之后，这就是它的红证据。
