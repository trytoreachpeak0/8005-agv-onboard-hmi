# hmi#222 故障注入

每个注入把实现里的一处判断改坏，重新编译，跑本票的 G2 测试类（个别跑单元测试或切片架构测试），记下哪些用例变红，再还原。
脚本是 `run-mutations.py`，从 worktree 根目录跑；`--check` 只核每处替换恰好命中一次。没命中记 `NOT APPLIED`，不会把没改坏的
代码当成绿。`summary.tsv` 是原始记录，`M*.txt`／`R0*.txt` 是每个注入被替换的原文、替换成什么、失败的用例名。

## 结论

- **R0（红证据）**：拿掉入口（入口永远不出现）与会话层的发送路径，回到本票之前。本票 G2 类加出站形状测试共 45 条，39 条红；
  完整输出在 `../red/SQCD.Agv.WireToGateG2Tests.txt`。仍绿的 6 条：5 条是「入口不该出现」的反向用例
  （`TheEntryIsNotOfferedUnlessTheServerSaysTheVehicleIsCharging` 三行、`TheEntryIsNotOfferedWithoutAVerifiedMaintainer` 两行），
  它们的红由 M08、M09a、M09b 给出；1 条 `TheShapeCheckReportsThePayloadShapesVersionOneSent` 与本票无关。
  `summary.tsv` 第一行的 `R0 ... BUILD FAILED` 是第一次的注入写法撞上可空分析（CS8604），改成恒真条件后单独重跑，结果在末尾。
- **M01～M25**：27 个注入全部按事先写下的预期变红。M24 是票面要求的切片标记反向验证：去掉「充电后返回服务」第一条测试的
  `FP-IS-13` 标记，`EverySliceTraitIsExactlyTheProjectionOfTheTestsOwnVectors` 变红。
- 红出比点名更多用例的（M05 14 条、M08 8 条、M14 8 条、M17 36 条、M19 9 条），逐个看过，都是确实依赖被改坏那一点的：
  M05 让 `CHARGER_UNREACHABLE`／`CHARGER_OCCUPIED` 两个选项消失，凡是按这两种或数选项个数的都红；M17 是接线接错，整类都过不去。

## M26（手工）

把 `MainWindow.xaml` 里说明与结果两行的 `Text` 绑定对调，跑 `scripts/check-ui-layout.ps1`：`Status: FAIL`，点名
`unableToChargeEntry`。还原后 `PASS`。输出在 `M26-notice-and-status-bindings-swapped.txt`。

## 独立审查之后（N01～N09）

审查之后在 `2f61ecc` 上跑，`summary.tsv` 末尾 8 行。M01～M25 是在 `4d00e4c` 上跑的，之后没有整批重跑：审查修改只动了结算
那几个方法与入口的开关判断，各自由下表的注入覆盖。

| 注入 | 改坏了什么 | 结果 |
| --- | --- | --- |
| N01 | 审查第 2 条修复前的形状：等待路径前加 300 ms（审查员的 X1），且快照已结束的请求不再结算 | 向量测试 `TheReportCarries...` 红：操作记录里等不到结果 |
| N02 | X1 原样，修复保留 | 绿（预期） |
| N03 | 审查第 4 条，清桩入口修复前的形状加 300 ms | `AResultFollowedAtOnceByTheSnapshotThatEndsTheClearanceIsStillShownToTheOperator` 红 |
| N04 | X1 用在清桩入口，修复保留 | 绿（预期） |
| N05 | 不看 `unableToChargeEntryEnabled` | `TheEntryIsNotOfferedWhileTheSwitchIsOff` 红 |
| N06 | 出厂配置把开关写成 `true` | `ConfigurationTests.TheUnableToChargeEntryShipsSwitchedOff` 红 |
| N07 | 审查员的 X2：重提不要求确认人是同一个 | `AnotherMaintainerIsAskedAfreshInsteadOfResubmittingSomeoneElsesConfirmation` 红 |
| N08 | 断线时把这次按下说成「未发送」 | `TodaysServerEndsTheSessionAndThePressIsShownAsUnknownWithoutAReplay` 等 10 条红 |

N09：修复之后不加注入，`TheReportCarries...` 与清桩那条新用例一起连跑 30 遍，30 遍全过（`N09-thirty-runs-after-the-fix.txt`）。
审查员在修复前同样连跑 30 遍，红 2 遍。
