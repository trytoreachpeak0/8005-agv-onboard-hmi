# onboard-hmi#295 证据：车载端按片 G2 一次跑多片、挪到 CI

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/295

本目录只留判读所需的文件（`gate-result.json`、`summary.json`、汇总、transcript、控制台输出、对照结果），
trx 与构建日志没有入库，原件在跑的那台机器上或 CI artifact 里。协议一律用 `protocol-v3.0.0`（`3f091cb2`）
的普通克隆。

| 目录 | 是什么 | 结论 |
| --- | --- | --- |
| `compare-g2-slice-modes.ps1` | 对照脚本，先于实现提交（`fbc0752`） | — |
| `red/before-impl-fbc0752.*` | 在只提交了对照脚本的 `fbc0752` 上跑：多片调用被参数校验拒绝 | 红，`Cannot validate argument on parameter 'Slice'` |
| `green/compare-3-slices/` | 实现提交 `50a186f`：`-Slice FP-IS-04,FP-IS-01,FP-IS-10` 一次，对比同一提交上三片各调一次 | PASS，逐片一致；多片 487.9 秒，逐片合计 942.9 秒 |
| `green/baseline-c60013ae/` | 改动前的脚本（集成分支 `c60013ae`）三片各调一次，与上面的多片结果比对 | PASS，逐片一致（含每条测试名与目录文件清单），字段只多了 `multiSliceRun`；三片合计 684.3 秒 |
| `red/injected-fp-is-01/` | 在 `50a186f` 的本地副本上把 FP-IS-01 的一条断言改反（提交 `806e2834`，未推送），多片调用同样三片 | 只有 FP-IS-01 为 FAIL（4 过 1 败），FP-IS-04、FP-IS-10 为 PASS，退出码 1 |
| `green/ci-38101599940-all-slices/` | CI `workflow_dispatch` 全片一轮，提交 `cf47d9df`，run 38101599940 | 15 片全 PASS，FP-IS-09 列为本仓无测试；协议 G1 在 CI 上 PASS；作业 19 分钟 |

三片选的是：`FP-IS-04`（零报文片，只在 `SQCD.Agv.UnitTests` 里，`schemaConformance` 为 `null`）、
`FP-IS-01`（两个测试工程都有）、`FP-IS-10`（只在 `WireToGateG2Tests` 里）。

比对的字段：`gate-result.json` 与 `summary.json` 的 `status`、选中条数、记录条数、trx 里每种结果的条数、
`schemaConformance`（`linesChecked`、`linesInViolation`、`linesInKnownViolation`）、build／test／format 退出码、
G1 状态；与基线比对时另加每条测试名与片目录里的文件清单（trx 文件名带时间戳，不比）。

耗时差异的说明：本机三轮跑在不同时刻，机器负载不同，同一片的逐片耗时在两轮之间差到 1.5 倍
（FP-IS-10：341 秒对 163 秒）。能稳定读出的是多片一次约等于逐片调用中的一次多一点，
因为构建、format、G1 只付一次，而每片测试本身（含出站 schema 编译）不变。

## 审查 M1 的修复（`review-m1/`）

审查发现：`-AllSlices` 把「已实现切片的测试全丢了」当成「还没建」跳过，其余各片照写 PASS、退出码 0。
修复（`9d5ebe1`）：脚本写死 `$slicesNotImplementedHere = @('FP-IS-09')`，只跳过表里的片；
新架构测试 `G2ScriptSliceTableArchitectureTests` 先于修复提交（`eb23d22`）。

| 文件 | 是什么 | 结论 |
| --- | --- | --- |
| `red/table-test-before-fix.txt` | 在 `eb23d22`（只有测试）上跑新架构测试 | 2 条都红：脚本还没有这张表 |
| `red/allslices-fp-is-04-trait-mistyped/` | 修后脚本，本地副本把 FP-IS-04 的 trait 名写错（提交 `7960c58`，未推送），跑 `-AllSlices` | 预检拒绝，退出码 1，没建任何证据目录。审查在修复前的脚本上实测同一变异：14 片 PASS、退出码 0 |
| `red/allslices-fp-is-10-trait-mistyped/` | 同上，改写错 FP-IS-10（`833c4a5`） | 在 FP-IS-10 拒绝，退出码 1。能走到这里，说明 FP-IS-00～08 过了预检、FP-IS-09 按表跳过 |
| `red/table-widened/` | 表里多塞已实现的 FP-IS-04（本地提交，未推送） | 架构测试红（期望 `FP-IS-09`，实际多出 `FP-IS-04`）；`-AllSlices` 在 FP-IS-04 拒绝（表里的片选中了 7 条测试） |
| `green/architecture-tests-9d5ebe1.txt` | 修后的五个相关架构测试类 | 43/43 通过 |
