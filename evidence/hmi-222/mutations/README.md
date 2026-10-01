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
