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

CI 真装置 real-onboard-compensate-then-reconnect 待调度放行后在最终 head 上跑，届时补到本目录。
