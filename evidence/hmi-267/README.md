# hmi#267 证据（`w2g/fp-v2-impl` 线）

本票起点是 `4bbbbfbe`（hmi#150 合入之后的集成分支顶端）。下面每份文件都是 `dotnet test` 的输出精简版，只保留了失败用例名、报错前几行和汇总行。

## 1. 红：`red/`

- `base-4bbbbfbe-g2-four-cases.txt`：在 `4bbbbfbe` 的产品代码上跑本分支新增的四格 G2，四格全红：
  - `ACommandOverADoorNotProvenShutIsRefusedUntilTheDoorIsShut`
  - `AVectorResultAcknowledgedAfterANewOperationClearedItChangesNothing`
  - `ALateVectorAcknowledgementLeavesAnotherAttemptOnFile`
  - `ACommandOverAVectorWithNoResultYetIsRefused`

  其中"迟到确认"那一格在基线上红在缺少"先收尾"日志，比较弱。真正的误结过程见第三份文件。
- `base-4bbbbfbe-door-open-observation.txt`：同一场景下旧代码的实际行为。3 号门开着时，新命令照样执行，结果 `UNKNOWN`（5 号仓 `NOT_STARTED`、`LOCK_NOT_CLOSED`）；日志簿 `activeUnlockSlots=[]`，3 号门的记录丢了；拒收 0 次。
- `door-check-only-05bea38-late-ack-observation.txt`：只加了门检查、向量处理与基线相同的那一版（`05bea38`）上的误结过程。
  - 新操作写入后，日志簿是 `vector=LOAD_COMPENSATION unsettled=9b9b…（新作业）`。
  - 补偿结果的迟到确认到达后，按"已完成"收尾，`unsettled`、`ctx` 都被清空，`cp=ResultRecorded`。

## 2. 变异：`mutation/`

### 本票的检查

| 文件 | 变异 | 红的用例 |
| --- | --- | --- |
| `m1-no-check.txt` | 去掉门检查 | `ADoorInDoubtThatReadsOpen…`、`AStaleReading…` |
| `m2-targets-only.txt` | 门检查只核命令的目标仓 | 上面两格，另加 4 格原有执行器用例 |
| `m3-stale-as-shut.txt` | 过期读数当作关好 | `AStaleReadingProvesNoDoorShutAndRefusesTheNewOperation` |
| `mb1-no-settle.txt` | 只去掉"先收尾"（第一次写入不去掉向量） | `AVectorResultAcknowledgedAfterANewOperationClearedItChangesNothing` |
| `mb2-no-refusal.txt` | 只去掉"向量没有结果行就拒收" | `ACommandOverAVectorWithNoResultYetIsRefused` |
| `mc-no-second-line.txt` | 只去掉第二道防线 | `ALateVectorAcknowledgementLeavesAnotherAttemptOnFile` |

### 改写后的多需求显示护栏（hmi#146、#152、#156）

范围都是整个 `MultiDemandJourneyG2Tests`（88 格）。P1 到 P5 来自 PR #155、#160 的"先红后绿"与注入表，P6、P7 来自 PR #151。

- P6、P7 在 `00b4f34` 的用例上跑。
- P1 到 P5 在改写版第一稿上跑。第一稿与 `00b4f34` 只差 `TheRunningDemandsUnknownResultIsShownBeforeTheQueuedCommandTakesOver` 一格，而 P1 到 P5 守的都不是这一格。

| 文件 | 变异 | 红的用例 |
| --- | --- | --- |
| `guard-p1-152-snapshot-unconditional.txt` | 恢复投影不管谁占着显示都带快照（撤 hmi#152） | 5 格，含 `ARestoredRecoveryProjection…`、`AnOverdueIsStillRaised…` |
| `guard-p2-156-debt-not-cleared.txt` | 兑现后不清欠条（#160 注入 1） | `AShownRecoveryEntryIsNotShownAgain…` |
| `guard-p3-156-no-release-hook.txt` | 释放时不兑现欠条（#160 注入 2） | `AShownRecoveryEntry…`、`AWithheldRecoveryEntryReaches…` |
| `guard-p4-156-same-dedup-key.txt` | 欠条用回原去重键（#160 注入 3） | 同上两格 |
| `guard-p5-156-superseded-paid.txt` | 被取代的欠条照样兑现（#160 审查 M2） | `AWithheldRecoveryEntryDoesNotTakeTheScreenOff…` |
| `guard-p6-146-no-display-gate.txt` | 排队命令不等显示闸就宣布"准备执行"（撤 hmi#146） | 4 格，含 `TheRunningDemandsUnknownResult…` |
| `guard-p7-146-result-event-unconditional.txt` | 已确认结果的事件不管谁占着显示都带快照 | `TheRunningDemandsUnknownResultIsShownBeforeTheQueuedCommandTakesOver` |

- `guard-p7-original-tests.txt`：P7 在改写前的用例上（门检查临时关掉）也只红这一格。改写版第一稿把 A 的确认改成丢弃，P7 就没人杀了；改成"扣住 A 的结果，等 B 接手屏幕再放行"之后，P7 重新被这一格杀死。
- `guard-door-check-off-before-rewrite.txt`：用例改写前，只去掉门检查，多需求组 88 格全绿。
- `guard-door-check-on-before-rewrite.txt`：用例改写前，加上门检查，多需求组红 6 格。这是 6 格要改写的归因证据。

## 3. 绿：`green/`

都在 `00b4f34` 上跑，`dotnet test` 退出码 0。

- `unit-00b4f34.txt`：`SQCD.Agv.UnitTests` 全部 717 格通过。
- `g2-targeted-00b4f34.txt`：`RecoveryVectorG2Tests`、`LoadCancellationBeforeSublotG2Tests`、`MultiDemandJourneyG2Tests` 三组，共 297 格通过。

- `unit-460105c.txt`、`g2-targeted-460105c.txt`：审查后的 head `460105c`。单元测试 717 格通过；上面三组 G2 共 300 格通过（新增维修原因的三格参数用例）。退出码都是 0。

本机全量 `ONBOARD_HMI_G2` 和 CI 真装置 `real-onboard-compensate-then-reconnect` 还没跑，要先向调度申请时段。

## 4. 审查后的改动：`review/`

审查对象是 `e99ee7b`，以下都在它之后。

- 合入集成分支 `81758d6`（hmi#270）之后：
  - `merge-81758d6-two-cases.txt`：改用 `HoldNextDurableAck` 的两格通过。
  - `merge-81758d6-mb1-no-settle.txt`、`merge-81758d6-mc-no-second-line.txt`：两个变异各自只红自己那一格。
- `TheRunningDemandsUnknownResultIsShownBeforeTheQueuedCommandTakesOver` 的消息超时，三组都在关门前注入 2.5 秒延迟：
  - `inject-30s.txt`：30 秒超时，3/3 通过。
  - `inject-30s-p7.txt`：再加 P7 变异，3/3 红在屏幕被拽回 A。
  - `inject-2s-control.txt`：退回 2 秒的对照，1/1 红在等不到 A 的确认，复现审查看到的误红。
- 文案与日志级别：
  - `mut-text-always-shut-door.txt`：变异"文案一律叫人关门"，三种维修原因的三格红。
  - `mut-log-always-warning.txt`：变异"每份重发都记 Warning"，门开着那一格红。

## 5. CI 真装置：`ci-real-rig-37492878389/`

- Run：https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/37492878389
  - `l2.yml`，`rig=real`，只跑一场 `real-onboard-compensate-then-reconnect`。调度在 vm01 空闲时放行。
  - 结论 success：job `real-rig` 为 success；`scenarios` 为 skipped，这是 `rig=real` 时的正常分支。
- `commits.json`：与派发时一致。control-server `1de2d2e27667305ac3aff917a425db9dbe92efeb`（派发前读到的 `fp/v2-impl` 顶端），onboard-hmi `eaa0f0358ca873841ddb04f4c69a763af54f1f83`，slots-simulator `fb5f7c593742bf98bc3957b8729a38aad5321f28`。
- `run-SUMMARY.md`：场景 PASS，用时 86 秒。
- `SUMMARY.md`、`assertions.json`：判据 `L2-CR-00` 到 `L2-CR-08` 共 9 条，全部 PASS。
- `timeline.jsonl`：场景时间线。
- `onboard-app-log.txt`：车载端应用日志，整份 73 行。
  - 本票加的拒收与收尾一条都没有出现："拒收SlotOperationCommand"、"日志簿记录的仓门未能确认已关好"、"恢复向量还没有它自己的结果"、"新仓位操作替换了日志簿"、"先收尾在案的恢复向量"、"未结作业已不是它自己的"、`RECOVERY_BLOCKED` 都是 0 次。正常流程里没有误拦。
  - "判恢复入口"那几行在几次重连之间判断正常，补偿之后 `recoveryVector=none`。
- `commit-samples.csv`：vm01 已提交内存的采样，59 次，在 4.57 到 7.7 GiB 之间（上限 16 GiB）。

## 6. 本机全量 ONBOARD_HMI_G2：`g2-full-c7bd350/`

- 命令：`pwsh -File scripts/run-w2g-g2.ps1 -ProtocolRoot <protocol-v2.0.0 的普通克隆> -EvidenceRoot <工作区外的 scratch 目录>`。没有传 `-Slice`，跑的是整个解决方案。
- 代码：审过的 `eaa0f03` 加真装置证据提交，即 `c7bd350`；两者之间证据目录以外 0 行差异。
- 结论：PASS，脚本退出码 0。
- `logs/dotnet-test-release.log`：
  - `SQCD.Agv.UnitTests`：`Passed!  - Failed:     0, Passed:   717, Skipped:     0, Total:   717`
  - `SQCD.Agv.WireToGateG2Tests`：`Passed!  - Failed:     0, Passed:   707, Skipped:     0, Total:   707`
  - 没有 `Test Assembly Cleanup Failure`。
- `test-results/schema-conformance.txt`：`0 distinct violations, 4 on file`。4 条全是已登记的有意违规；`schema-violations.json` 里没有 `onFile` 为空的项。
- `summary.json`：`status=PASS`，`hmi.commit` 是 `c7bd3509b43e4c2470f23dfa7af6f941eaed9dae`，`workingTreeStatus=[]`；协议 `protocol-v2.0.0`，`APPROVED_RELEASE`。
- 整个目录原样保留，约 60 KB。
