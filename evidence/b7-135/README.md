# hmi#135 证据

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/135（批次7-14，多需求下的操作员入口）

PR：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/pull/158

## 红证据（`red/`）

### 先红后绿

| 文件 | 测试提交 | 红在 |
| --- | --- | --- |
| `01-selection-entry-unavailable.txt` | `cd6a207` | 批次7-13 合入后的基线上，三条判据红在 `the cancellation-before-sublot entry to be offered` 超时——多条清单项时扫码前取消的入口根本不出现，取消发不出去。第四条（一条清单项）本来就绿，它钉的是「单条行为逐字不变」 |
| `03-correction-and-fallback-target.txt` | 纠错与回落那一轮 | 四条红：`只能修正本站最后一次装货` 的新文案没有（实际仍是 `修正对象：…`）、回落目标那一行不显示 |

### 注入验证（不提交，跑完按备份还原）

还原后都与注入前 md5 一致。产品文件的备份是同目录的 `*.bak`、`*.bak2`。

`02-ui-layout-injections.txt`——`scripts/check-ui-layout.ps1` 改动之后仍有判别力。这条检查原本钉着批次7-13 的「暂不可用」占位按钮，本票把它替换掉了，**而拆掉或放宽一个检查不会让任何东西变红**：

| 注入 | 预期红 | 实际 |
| --- | --- | --- |
| 01 删掉「请先选择」那句提示 | `loadCancellationSelectionHint` | 一致 |
| 02 去掉清单的 `SelectedItem` 绑定 | `selectableWorklistItems` | 一致 |
| 03 按钮 `IsEnabled` 改回旧的入口开关 | `loadCancellationSelectionHint` | 一致 |
| 04 把「暂不可用」的 `AutomationId` 换回来 | — | 红两条（同时移除新 id、引入旧 id，可解释；孤立验证见 10） |
| 05 `Visibility` 也绑成 `CanPressLoadCancellation` | `loadCancellationSelectionHint` | 一致。**这一条是审查发现的净损失**：旧检查钉着「入口不可用时原位仍有一个禁用按钮」，新检查一开始只钉 `IsEnabled`，改掉 `Visibility` 按钮就整个消失而脚本仍 PASS |
| 06 清单 `ListBox` 的 `AutomationId` 去掉 | `selectableWorklistItems` | 红两条（该 id 是两条检查共用，可解释） |
| 07 回落目标那一行的 `AutomationId` 去掉 | `recoveryFallbackTarget` | 一致 |
| 08 回落目标那一行的 `Text` 绑定改错 | `recoveryFallbackTarget` | 一致 |
| 09 回落目标那一行的显隐绑定去掉 | `recoveryFallbackTarget` | 一致 |
| 10 只把旧的「暂不可用」提示额外加回来、不动新提示 | `noLoadCancellationUnavailableHint` | 一致（04 的孤立版本） |

`04-guard-injections.txt`——自查「本票新立的每个约束，改掉有没有测试会响」：

| 注入 | 预期红 | 实际 |
| --- | --- | --- |
| 原选择不在清单时回落到当下选择／第一条 | 1 条 | 只红 `ASentCancellationWhoseDemandLeftTheStop…` |
| 已发出的取消改按当下选择重新推断 | 1 条 | 红 2 条（多的那条依赖同一个分支，可解释） |
| 回落目标不再判断主体是否回落 | 1 条 | **第一版一条都不红**——见下 |
| 未选中／所选不在清单都回落到第一条 | 2 条 | 红 3 条（多的那条读的是同一个信号，可解释） |

**第三次注入的第一版是假绿。** 那条测试用真实命令把车开到「有在途操作」再断言回落目标为 `null`，它通过了——但把那层判断整个拆掉之后照样通过：走到那一刻缓存里的 `LastCompletedLoadOperationContext` 已经不在了，正确读法与错误读法都返回 `null`，断言是对的而被测的那层判断从未参与。两种读法只有在「在途操作与已结算装货同时在状态里」时才分得开，那正是「装完一条又接了一条命令」之后重启恢复出来的形状。改成 seed 那个形状的 Theory 之后，拆掉判断就只红 `armed: True` 那一格。

## 绿证据

本机只跑改动相关的测试类（2026-09-20 用户决定：车载端全量 G2 只走 CI）：

| 项 | 结果 |
| --- | --- |
| `MultiDemandJourneyG2Tests` + `MultiDemandViewModelTests` + `LoadCancellationBeforeSublotG2Tests` + `RecoveryVectorG2Tests` | 158 passed |
| `IntegrationSliceTraitArchitectureTests` + `ProtocolVectorTestBindingArchitectureTests` | 绿 |
| `scripts/check-ui-layout.ps1` | PASS |
| `dotnet format --verify-no-changes` | 两个项目均无改动 |

CI 与真装置的 run 号记在 PR 正文。
