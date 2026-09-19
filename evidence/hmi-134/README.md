# hmi#134 证据

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/134（批次7-13，`FP-IS-08` 车载端半边）

## 红证据（`red/`）

两种取法：

- **先红后绿**（`01`～`11`）：每轮先提交测试与只够编译的桩，在该测试提交上跑出失败，再提交实现。文件名与提交对应：

| 文件 | 测试提交 | 红在 |
| --- | --- | --- |
| `01-schema.txt` | `42f466a` | 8 项清单、9 条腿判 `PROTOCOL_SCHEMA_INVALID` |
| `02-crash.txt` | `d483de8` | 只放开两处校验（`b0fbb9f`）后，两条清单项在 `MainViewModel.cs:276` 抛 `InvalidOperationException: Sequence contains more than one element`（装配照 `App.OnDispatcherUnhandledException` 转 `UNHANDLED_UI_ERROR`） |
| `03-journey-consistency.txt` | `7d1d856` | 计划两个需求恒不一致、`SingleOrDefault` 抛异常；控制器旅程门关着 |
| `04-stop-facts.txt` | `0a8281e` | 多条清单项时顶栏方向与任务类型为空、每行文字为空 |
| `05-worklist-list.txt` | `ad1ca56` | 视图模型崩在 `Sequence contains more than one element` |
| `06-plan-legs.txt` | `afb5528` | 计划腿列表与文案为空 |
| `07-loading-phase-text.txt` | `8bf1f6a` | 持货与结束原因文案为空 |
| `08-loading-phase-vm.txt` | `f1d41cf` | 视图模型不显示持货、装满与结束原因 |
| `09-sides.txt` | `0b58c25` | 标侧为空 |
| `10-sides-hint-correction.txt` | `bca3d1b` | 每行侧、取消提示与修正对象为空 |
| `11-ui-layout.txt` | `04c390a` | `check-ui-layout.ps1` 新 7 项全缺，`Status: FAIL` |

- **缺陷版本**（`12`～`20`）：测试写在实现之后的，本地临时注入一个具体缺陷（diff 写在文件开头或 `.diff` 里）、跑、`git checkout` 还原，不提交。

| 文件 | 缺陷 | 红的测试 |
| --- | --- | --- |
| `12-*` | 计划腿不按 `sequence` 排、按收到顺序显示 | `FP-IS-08` 两条具名测试，都红在顺序断言 |
| `14-*` | 视图模型不读 `loadingPhase` | `TheLoadingPhaseLinesFollowTheServersBusinessStateSnapshots` |
| `15-*` | 空旅程时不清清单与计划 | `ADroppedConnectionClearsThePlanTheWorklistAndTheCargoHoldingCountdown` |
| `16-*` | 标侧不读日志操作上下文 | `ARestartRestoresTheItemsTheNineLegPlanTheLoadingPhaseAndTheSide` |
| `17-*` | 录入不比对清单修订号 | `AWorklistRevisionArrivingBeforeTheEntryIsSubmittedRefusesItLocally` |
| `18-*` | 标侧取任意一条命令的仓位 | `AFailedOrUnknownResultForTheFirstDemandLeavesTheListIntactAndItsSideOnItsOwnRow`（FAILED、UNKNOWN） |
| `19-*` | `CLOSED` 不带原因也收下 | `AClosedLoadingPhaseWithoutAReasonIsStillRefusedAndLeavesNoHalfState` |
| `20-*` | 不给扫码前取消的不可用提示 | `TheCancellationBeforeSublotIsShownDisabledWithAHintOnlyWhenTheStopHasSeveralItems(items: 2)` |

`13-list-fp-is-08.txt`：`dotnet test --list-tests --filter "IntegrationSlice=FP-IS-08"` 选中 2 条。

## 绿（`green/`）

- `onboard-hmi-g2-<提交>/`：本机全量门禁（`run-w2g-g2.ps1 -SkipProtocolG1`，协议根 `repos/8005-agv-protocol` 在 `protocol-v2.0.0`）。
- `ui-layout-<提交>.json`：布局检查 PASS。
- `ui-render-1280x800.png`、`ui-render-1024x768.png`：主窗口带示例数据（3 条清单项、9 条腿、持货等单、多条时的取消提示）渲染的截图，由一次性测试生成，未提交该测试。

第一次全量（`c4d377e`）测试全部通过，但出站 schema 一致性检查报了 1 条违约：替身在
`AClosedLoadingPhaseWithoutAReasonIsStillRefusedAndLeavesNoHalfState` 里有意发的 `CLOSED` 不带原因。已按既有机制登记为
`deliberate`（`a9d2788`，只豁免 synthetic-peer 来源），第二次全量见 `green/`。
