# hmi#199 证据：本站结束或装货命令答复后撤销录入请求

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/199
PR：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/pull/200

本提交只加这一个文件；代码树与真装置运行所用的 `d5d445f8` 相同（`git diff d5d445f8 HEAD -- src tests` 为空）。

## CI 真装置

run [35736388968](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35736388968)，结论 success。

| 场景 | 结果 | 秒 | 起跑时已提交内存 |
| --- | --- | --- | --- |
| `real-onboard-compensate-then-reconnect-01` | PASS | 68 | 3.97 GiB（步骤内峰值 7.25 GiB） |

四行核对（读自 `Run real-onboard L2 scenarios` 那一步，不是 checkout 段）：

```
control-server @ a98ae9be8433b3e170705a265a3c4fb6060fb6e7
8005-agv-onboard-hmi @ d5d445f8dde960c1058bc36aa96b31ff41d9fd0c
slots-simulator @ fb5f7c593742bf98bc3957b8729a38aad5321f28
```

`RIG_COMMIT_GUARD|RIG_DESKTOP_LOCK|RIG_DEADLINE`：全日志命中 1 行，该行带 `^[[36;1m`（脚本源码回显）；不带该前缀的命中 0 行，即三个守卫都没有触发。

## 红证据与反向验证

见 PR 正文。红提交 `1490c28`：修复前 6 条新用例红、反例 2 支绿。修复提交 `23b4755` 之后：

- 三个产品文件退回红提交版本 → 6 条新用例全红；
- 撤请求后不发 `SUBLOT_ENTRY_WITHDRAWN` → 两条撤销用例红（视图层断言有判别力）；
- 判据改成「修订号更高就撤」→ 只红反例中服务端真实顺序那一支；
- 装货命令不撤请求 → 只红第 3 项那条。
