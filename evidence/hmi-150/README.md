# hmi#150 + hmi#254 第二部分 证据索引

| 文件 | 内容 |
| --- | --- |
| `red/red-28545a8.txt` | 红：w2g/fp-v2-impl@28545a8 上 9 格新用例全红（提交 263566d，只改测试） |
| `green/green-fb85e88.txt` | 绿（已被取代）：fb85e88 |
| `green/green-31eb5e7.txt` | 绿（已被取代）：31eb5e7 |
| `green/green-8d14d09.txt` | 绿：8d14d09 上单测全量 713 通过；定向 G2 第一遍 432/433（StationDeadline 一族计时用例超时一条），第二遍 433/433、退出码 0 |
| `green/schema-conformance-fb85e88.txt` | 定向 G2 收尾的 schema 一致性结论原文 |
| `mutations/mutations.txt` | 变异汇总行与退出码，六轮；最终全部取红（M12、M9d、N2 等按审查补用例后取红） |

全量 ONBOARD_HMI_G2 与 CI 真装置 real-onboard-compensate-then-reconnect 待调度放行时段后在最终 head 上跑，届时补到本目录。
