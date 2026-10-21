# hmi#391 审查返工证据（PR #405 审查 M1、M2、L2、L3 与文案）

- `red-on-4ccfe0f.txt`：返工测试提交 `4ccfe0f`（产品代码仍是审查时的 `488b3c6`）。16 条里 10 条红：
  另一原因的拒绝仍说未开放（探针 1）、会话里动作被拒仍说未开放（探针 2）、动作被拒后再以
  `RECOVERY_SESSION_STATE_PENDING` 被拒标记仍在（探针 3）、未结作业换了标记仍在（L2）、重放旧 `CLOSED` 重新点亮（L3）、
  三处新文案、成功后关闭的两条（只红在文案）。
- `targeted-green.txt`：实现提交 `8be3a0e` 上，本票用例、关闭原因旧用例、屏幕快照与 G2 架构守卫 59 条全绿，退出码 0。
- `unit-tests.txt`：单元测试项目整体 1312 条全绿。
- `wider-g2.txt`：`RecoveryVectorG2Tests`、`MultiDemandJourneyG2Tests`、`DoorShutOverOppositeOccupancyG2Tests`、
  `*ViewModelTests` 563 条全绿，退出码 0。
- `format-verify.txt`：`dotnet format ./SQCD_8005AGV.sln --verify-no-changes --no-restore` 退出码 0，无输出。

成功后关闭的时序：服务端确认结果、车收尾完再关会话时，操作记录稳定是「补偿清空已完成并收到服务端确认」在前、
「本次恢复已结束……」在后（`ACloseForTheAuthorizationAfterTheCompensationCompletedSaysItCompletedFirst`）。
关会话紧跟在结果确认之后、车还没收尾时，两条都会出现，但先后不定：返工中有一轮是「已结束」在前、几毫秒后「已完成」
（`ACloseForTheAuthorizationRightBehindTheResultStillSaysTheCompensationCompleted` 只断言两条都在）。
