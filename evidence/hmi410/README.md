# hmi#410 证据

## original/：首轮实现（`a176ddb`…`74e8c0b`）

- `red-a/run.txt`：测试提交 `a176ddb`，结果交服务端的未知装货没有指向「补偿清空」，`Timed out after 5s waiting for: the screen to name the way out.`
- `red-b/`：`524e1a5` 桩上「不装了」不出现（`run-on-stub.txt`）；`1259091` 上续行提示仍写取消（`prompt.txt`）。
- `red-c/run.txt`：`970c455` 上，结果交出后确认的取消只叫人联系维护人员，不提「补偿清空」。
- `green-a`、`green-b`、`green-c`：对应实现提交上的定向 G2 与单元测试。`green-a/rerun4.txt` 是四条并发重负载下超时的用例单独重跑全过。
- `mutation/`：「不装了」的给出条件去掉空仓判断（`cargo-guard.txt`）、去掉「只在续行」（`resume-guard.txt`），各有一条用例变红。
- `final/unit.txt`：首轮的单元测试整体，含审查 M1 指出的那条红（`TheLexerNamesEveryMemberOfTheClass`）。首轮正文没有提它。

## rework/：PR #414 审查返工

- 合并 `w2g/fp-v2-impl`（`8d9b05d`），`giveUpCaption` 合成两条规则：续行可放弃时叫「不装了」，否则只在取消入口对这次装货亮着时叫取消按钮名，否则不提。
- `s2-prompt.txt`：两张票的提示用例（`LoadGiveUpGuidanceTextTests`、`LoadCancellationEmptyScopeG2Tests`、`DoorShutOverOppositeOccupancyG2Tests`、`RemainingBasketsSkipG2Tests`、`ReopenNotReleasedCancellationG2Tests`、`StationDeadlineExpiredG2Tests`、`WireToGateStationDeadlineTextTests`、`MultiDemandJourneyG2Tests`），341 条里 340 条通过。失败的那条是 `StationDeadlineExpiredG2Tests.AResultRefusedAcrossAReconnectIsDeliveredOnTheNextSessionAndSettledOnce(moment: DuringHandshake)`，测的是断线重连，与提示文案无关；单独重跑 6 次全过（`s2-rerun-flaky.txt` 是第一次）。
- `red-s1.txt`：测试提交 `20b988a`（只改期望、新增确认框用例），7 条里 5 条红；`green-s1.txt`：实现提交 `c3f9227` 上 7 条全过。
- `format-verify.txt`：`dotnet format ./SQCD_8005AGV.sln --verify-no-changes --no-restore`，退出码 0。
- `unit-tests.txt`：`SQCD.Agv.UnitTests` 整体 1335 条全过。
- `mutation/`（`mutate.sh`，在已提交的树上每次只改一个文件、跑完 `git checkout` 还原；`*.diff` 是改动，`*.txt` 是输出）：
  - M1 把文字类放回分部类文件末尾：`TheLexerNamesEveryMemberOfTheClass` 红。
  - S2a 去掉「续行可放弃时叫不装了」：`AResumedLoadOverAnEmptySlotIsGivenUpByPressAndThenCompensated` 红在续行提示。
  - S2b 去掉「取消入口亮着才给按钮名」：305 条全过，**存活**。#413 只用纯文本单测盯 `null` 的写法，调用点上的条件没有 G2 用例；见 PR 正文的剩余风险。
  - S1 退回旧授权说法：4 条用例红在句子断言。
