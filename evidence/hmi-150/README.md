# hmi#150 + hmi#254 第二部分 证据索引

| 文件 | 内容 |
| --- | --- |
| `red/red-28545a8.txt` | 红：w2g/fp-v2-impl@28545a8 上 9 格新用例全红（提交 263566d，只改测试） |
| `green/green-03d7618.txt` | 绿（现行）：03d7618 上单测全量 713 通过、恢复相关定向 G2 438 通过，两者退出码 0 |
| `green/green-8d14d09.txt` | 绿（已被取代）：8d14d09；定向 G2 第一遍有一条计时用例超时，失败原文附在文末 |
| `green/green-31eb5e7.txt`、`green/green-fb85e88.txt` | 绿（已被取代） |
| `green/schema-conformance-*.txt` | 对应各次定向 G2 收尾的 schema 一致性结论原文 |
| `g2-full-085c98c/` | 本机全量 ONBOARD_HMI_G2：085c98c（代码 03d7618），`pwsh -File scripts/run-w2g-g2.ps1 -ProtocolRoot <protocol-v2.0.0 的普通克隆>`，不传 `-Slice`。PASS，退出码 0；UnitTests 713 通过，G2 700 通过；`summary.json` status=PASS、g1Status=PASS，build/test/format 退出码 0；schema 一致性 0 条新违规、4 条已登记的故意违规（均为既有用例）。目录原样保留，约 60 KB |
| `mutations/mutations.txt` | 变异汇总行与退出码，七轮；最终全部取红 |
| `ci-real-rig-37439910002/` | CI 真装置 `real-onboard-compensate-then-reconnect`，run 37439910002（control-server `cb8a894e`、本仓 `dbf29315`、simulator `fb5f7c59`）：PASS，94 秒，9 条判据全 PASS。按 hmi#254、hmi#255 的先例精简为 7 个文件：`run-SUMMARY.md` 是这一轮的汇总，`SUMMARY.md`、`assertions.json`、`timeline.jsonl` 是场景结论，`onboard-app-log.txt` 是车载端应用日志，`commits.json` 与 `commit-samples.csv` 是三方提交与内存采样。服务端日志与数据库快照没有入库，原件在该 run 的 artifact `real-rig-evidence` 里 |

这个场景里补偿结果在首发就拿到确认，走不到本票新增的「首发之外的确认」与「人工核对后结束」两条路径；它证明的是恢复入口与界面没有回归。新路径由上面的 G2 用例覆盖。
