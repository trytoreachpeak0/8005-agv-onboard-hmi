# hmi#282 证据：fp（0e1c15ba）同步进 batch-p3/v3

| 目录 | 内容 | 提交 |
| --- | --- | --- |
| `g2-targeted/` | 定向 `dotnet test --filter`（Release，`--no-build`，按类串行）。`exit-codes.txt` 是每次运行的退出码，其余是各次完整输出 | `5778847`（合并）；`MultiDemandJourneyG2Tests-ce3368d-run1/2` 在 `ce3368d` |
| `g2-slice/protocol-v3.0.0/FP-IS-07`、`FP-IS-02`、`FP-IS-03` | `scripts/run-w2g-g2.ps1 -Slice`，协议为 v3 候选 `3f091cb2`（`scratch/` 下的普通克隆），三片都是 PASS | `ce3368d` |

说明：

- `g2-targeted/g2-ArchitectureTests.txt` 不是一次通过：G2 项目里没有名为 `ArchitectureTests` 的类，过滤匹配到 0 条，`dotnet test` 照样退出码 0。架构测试在 UnitTests 项目里，已包含在 `unit-all.txt`（881/881）中。
- `g2-MultiDemandJourneyG2Tests.txt`（`5778847`）的 3 条红：两条是 hmi#209 的强制取出用例未带 v3 要求的交接记录（`ce3368d` 修）；一条是 `AHandoffThatOpenedNothingLeavesTheLoadsAckToSettleIt` 在测试线程枚举 `ViewModel.Logs` 时撞上界面线程写入（`Collection was modified`），测试代码与 fp 相同，已交调度另开票。
- 切片第一次运行（`-EvidenceRoot` 传了相对路径）在跑测试之前就因日志路径解析错误失败，不是测试结果，那次的目录已删除。
- 审查 S1、S2 之后（`47d5c0a`，只改测试与替身）：`g2-MultiDemandJourneyG2Tests-47d5c0a.txt` 123/123、`g2-RecoveryVectorG2Tests-47d5c0a.txt` 220/220，这两类是用到改动后替身（`RecoverySessionScopeByDemand`、`ModelOneOpenRecoverySession`）的全部测试类。
