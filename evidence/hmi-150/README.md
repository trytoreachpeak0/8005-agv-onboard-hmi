# hmi#150 + hmi#254 第二部分 证据索引

| 文件 | 内容 |
| --- | --- |
| `red/red-28545a8.txt` | 红：w2g/fp-v2-impl@28545a8 上 9 格新用例全红（提交 263566d，只改测试） |
| `green/green-03d7618.txt` | 绿（现行）：03d7618 上单测全量 713 通过、恢复相关定向 G2 438 通过，两者退出码 0 |
| `green/green-8d14d09.txt` | 绿（已被取代）：8d14d09；定向 G2 第一遍有一条计时用例超时，失败原文附在文末 |
| `green/green-31eb5e7.txt`、`green/green-fb85e88.txt` | 绿（已被取代） |
| `green/schema-conformance-*.txt` | 对应各次定向 G2 收尾的 schema 一致性结论原文 |
| `mutations/mutations.txt` | 变异汇总行与退出码，七轮；最终全部取红 |

全量 ONBOARD_HMI_G2 与 CI 真装置 real-onboard-compensate-then-reconnect 待调度放行时段后在最终 head 上跑，届时补到本目录。
