# hmi#278 证据

- `reachability-probe.txt`：修前（`w2g/fp-v2-impl` 45faf677，只加了探针用例）两种到达顺序的日志簿实读，两格全红。探针用例当时的名字与定稿相同，定稿去掉了写临时文件的探针行、加了出口链。
- `mutation.txt`：在 `0ffd173` 上逐个变异，每轮 `--no-incremental` 重编、跑本票 4 格 G2 与执行器级 4 格单测，整份写回原文件还原。`PRE` 是三处产品判断全部退回（只留字段），即修前行为：交接两格与补偿写标记格红，补偿护栏格绿。
- `targeted-g2.txt`：`StationDeadlineExpiredG2Tests`、`MultiDemandJourneyG2Tests`、`RecoveryVectorG2Tests`、`ArchitectureTests` 逐类运行与 `dotnet format --verify-no-changes`，全部退出码 0。

## 独立审查之后（864f756 起）

- `mutation-review.txt`：审查后代码上的 13 轮变异，每轮改了什么写在该轮开头一行；脚本原文 `mutate2.py`、`mutate2.sh`。全部被杀。
- `targeted-g2-review.txt`：`StationDeadlineExpiredG2Tests` 62、`MultiDemandJourneyG2Tests` 100、`RecoveryVectorG2Tests` 193、`WireToGateTakenOverMarkerTests` 6、`ArchitectureTests` 4，`SQCD.Agv.UnitTests` 722，`dotnet format --verify-no-changes`，全部退出码 0。

## 本机全量 ONBOARD_HMI_G2（`g2-full-1dd3353/`）

在 `1dd3353` 上，`pwsh -File scripts/run-w2g-g2.ps1 -ProtocolRoot <protocol-v2.0.0 的普通克隆>`，不传 `-Slice`。`Status: PASS`，脚本退出码 0；build/test/format 退出码都是 0；协议 `protocol-v2.0.0`、`APPROVED_RELEASE`、G1 PASS。`SQCD.Agv.UnitTests` 722/722，`SQCD.Agv.WireToGateG2Tests` 725/725；schema 一致性 `0 distinct violations, 4 on file`；没有 `Test Assembly Cleanup Failure`。
