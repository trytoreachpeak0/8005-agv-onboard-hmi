# hmi#278 证据

- `reachability-probe.txt`：修前（`w2g/fp-v2-impl` 45faf677，只加了探针用例）两种到达顺序的日志簿实读，两格全红。探针用例当时的名字与定稿相同，定稿去掉了写临时文件的探针行、加了出口链。
- `mutation.txt`：在 `0ffd173` 上逐个变异，每轮 `--no-incremental` 重编、跑本票 4 格 G2 与执行器级 4 格单测，整份写回原文件还原。`PRE` 是三处产品判断全部退回（只留字段），即修前行为：交接两格与补偿写标记格红，补偿护栏格绿。
- `targeted-g2.txt`：`StationDeadlineExpiredG2Tests`、`MultiDemandJourneyG2Tests`、`RecoveryVectorG2Tests`、`ArchitectureTests` 逐类运行与 `dotnet format --verify-no-changes`，全部退出码 0。
