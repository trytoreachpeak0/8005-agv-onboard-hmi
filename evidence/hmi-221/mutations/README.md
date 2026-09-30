# hmi#221 故障注入

每个注入把实现里的一处判断改坏，重新编译，跑 `ManualStationClearanceG2Tests`（个别跑
`WireToGateStationClearanceTests`），记下哪些用例变红，再还原。脚本是 `run-mutations.py`，从 worktree 根目录跑；
每个替换都校验恰好命中一处，没命中记 `NOT APPLIED`，不会把没改坏的代码当成绿。

`summary.tsv` 是原始记录，`M*.txt` 是每个注入被替换的原文、替换成什么、失败的用例名。

## 结论

22 个脚本注入全部变红，红的用例与事先写下的预期一致；另有 1 个手工注入（M20）。

**M03 第一次没有红，`summary.tsv` 里两行都留着。** 去掉「第一次应答前不许第二次按下」的闸门之后，第二次按下被另一道判断
挡住了：此时已有未应答的请求，当前提示是「重新提交」，和操作员手里那份对不上，照样拒绝、照样不发。实现上确有两层，
但当时的用例分不清是哪一层在起作用。用例改成再按一次、带上「此刻本来会给出的那份提示」，只有闸门能挡住它，并断言拒绝
的原话；改后重跑 M03 变红。`M03-*.txt` 是重跑后的结果。

几个注入红出了比点名更多的用例（M04、M08、M14、M17、M19），逐个核过，都是确实依赖被改坏那一点的用例：
M04／M08 是确认之后还要继续用入口的用例，M14／M19 是走过「超时」那一步的用例，M17 是接线接错、整类都过不去。

## M20（手工）

把 `MainWindow.xaml` 里说明与结果两行的 `Text` 绑定对调，跑 `scripts/check-ui-layout.ps1`：`Status: FAIL`，点名
`stationClearanceEntry`。还原后 `PASS`。输出在 `M20-notice-and-status-bindings-swapped.txt`。

## 独立审查之后（S1、S3、S4）

审查要求补的用例同样各做了一次注入，都在审查修改的提交上跑，全部变红，红的只有对应的用例：

| 注入 | 改坏了什么 | 红 |
| --- | --- | --- |
| R1 | 与请求关联的 `ProtocolProblem` 不单独认，当成普通失败 | `AProtocolProblemToTheRequestEndsItsIdAndEachLaterPressIsANewConfirmation` |
| R2 | 按下只接原来列表里的四种异常 | `ASessionThatFailsWithAnUnexpectedExceptionStillEndsThePressAsUnknown` |
| R3 | 不拒收自相矛盾的应答 | `AResultThatContradictsItselfIsRefusedAndThePressEndsAsUnknown`（4 个取值） |
| R4 | 畸形应答不在类型化读取之前拒收，`JsonException` 原样冒出 | `AMalformedResultLeavesTheVehicleUnlatchedAndTheConfirmationResubmittable` |

M01～M19 是在 `d4c9644` 上跑的，审查修改之后没有整批重跑；它们针对的判断与用例在这次修改里没有变，变的是等待辅助方法
与假服务端的配置。
