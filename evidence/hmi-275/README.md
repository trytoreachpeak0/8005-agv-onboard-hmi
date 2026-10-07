# hmi#275 证据：把 hmi#150、hmi#270、hmi#267、hmi#263 同步进 batch-p3/v3

PR：trytoreachpeak0/8005-agv-onboard-hmi#277。

| 目录或文件 | 内容 | 跑在哪个提交上 |
| --- | --- | --- |
| `ours-merge-audit.txt` | `-s ours` 记为已合并的对照：逐票（patch-id、父提交、`--cc`）、逐文件（`merge-tree` 模拟正常合并）、差异行是否都在 merge-base 上 | fp `28545a8` 对 v3 `22a98199` |
| `g2-targeted/` | G2 定向，六个类按类串行；`summary.txt` 末尾写了实际跑了几次 | `96f2a46`，`RecoveryVectorG2Tests` 另在 `d5a5b51`、`b823f9c` 上 |
| `s2-repair-release-pin/` | 独立审查 S2：新用例原样绿、删去 `WriteCheckpointAsync` 保留 `RepairRelease` 那一行后红、还原后绿 | `aee3418` |
| `g2-full-916c94a/` | 全量 `ONBOARD_HMI_G2`，PASS，UnitTests 859/859，G2 776/776，schema 0 处新违约 | `916c94a`，协议 v3 候选 `3f091cb2` |
| `ci-real-rig-37607047677/` | 与 control-server 联合的 CI 真装置，两个场景都 PASS（9/9、7/7 条判据） | control-server `e53d1dfc`、onboard `f45c3b38`、simulator `fb5f7c59` |

CI 真装置每个场景只留了 `SUMMARY.md`、`assertions.json`、`timeline.jsonl` 和车载端日志；完整证据包是 run `37607047677` 的 artifact `real-rig-evidence`。
