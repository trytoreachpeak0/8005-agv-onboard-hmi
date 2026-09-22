# hmi#199 证据：本站结束或装货命令答复后撤销录入请求

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/199
PR：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/pull/200

本文件随证据提交更新；代码树与第二次真装置运行所用的 `19e2757e` 相同（`git diff 19e2757e HEAD -- src tests` 为空）。

## CI 真装置（最终 head，审查修正后）

run [35746075526](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35746075526)，结论 success。

| 场景 | 结果 | 秒 | 起跑时已提交内存 |
| --- | --- | --- | --- |
| `real-onboard-compensate-then-reconnect-01` | PASS | 67 | 3.99 GiB（步骤内峰值 7.26 GiB） |

四行核对（读自 `Run real-onboard L2 scenarios` 那一步）：

```
control-server @ a98ae9be8433b3e170705a265a3c4fb6060fb6e7
8005-agv-onboard-hmi @ 19e2757effca7d645297a47a8fccc134211b635d
slots-simulator @ fb5f7c593742bf98bc3957b8729a38aad5321f28
```

`RIG_COMMIT_GUARD|RIG_DESKTOP_LOCK|RIG_DEADLINE`：全日志命中 1 行，带 `^[[36;1m`（源码回显）；不带前缀 0 行。

审查修正改到了这个场景会走的扫码提交检查与录入请求接收，所以在新 head 上重跑；上一次（审查前，`d5d445f8`）的记录保留在下面。

## CI 真装置（审查前，`d5d445f8`，已被上面取代）

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

见 PR 正文。审查修正另有红提交 `febb20d`，变异：恢复「修订号必须相等」→ 两条端到端用例红在 `the control server to receive SublotSubmitted`；去掉「子批须在最新清单」→ 三条负面断言红。

以下为审查前的记录：红提交 `1490c28`：修复前 6 条新用例红、反例 2 支绿。修复提交 `23b4755` 之后：

- 三个产品文件退回红提交版本 → 6 条新用例全红；
- 撤请求后不发 `SUBLOT_ENTRY_WITHDRAWN` → 两条撤销用例红（视图层断言有判别力）；
- 判据改成「修订号更高就撤」→ 只红反例中服务端真实顺序那一支；
- 装货命令不撤请求 → 只红第 3 项那条。
