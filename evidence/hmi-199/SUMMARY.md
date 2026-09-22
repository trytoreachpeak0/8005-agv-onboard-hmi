# hmi#199 证据：本站结束或装货命令答复后撤销录入请求

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/199
PR：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/pull/200

本文件随证据提交更新。第四次真装置运行用的是 `02268bb0`；之后增量审查 F1／F2 只改了 `src/SQCD.Agv.Wpf/WireToGateBusinessService.cs` 里的 `HandleSublotRejected`（`WORKLIST_REVISION_STALE` 拒收不保留请求），加上测试。`real-onboard-compensate-then-reconnect` 全程没有服务端拒收，走不到这个函数，经调度同意不重跑真装置，下面的记录对最终 head 仍然成立。

## CI 真装置（最终 head：第二轮审查 L1／L2／L4 之后）

run [35754913023](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35754913023)，结论 success。

| 场景 | 结果 | 秒 | 起跑时已提交内存 |
| --- | --- | --- | --- |
| `real-onboard-compensate-then-reconnect-01` | PASS | 71 | 5.35 GiB（步骤内峰值 8.33 GiB） |

四行核对（读自 `Run real-onboard L2 scenarios` 那一步）：

```
control-server @ a98ae9be8433b3e170705a265a3c4fb6060fb6e7
8005-agv-onboard-hmi @ 02268bb05af29800a5a774c625a3213585e400ed
slots-simulator @ fb5f7c593742bf98bc3957b8729a38aad5321f28
```

`RIG_COMMIT_GUARD|RIG_DESKTOP_LOCK|RIG_DEADLINE`：全日志命中 1 行，带 `^[[36;1m`（源码回显）；不带前缀 0 行。

第二轮审查改了扫码前取消入口的判断（`FindLoadCancellationBeforeSublot`），这个场景有录入请求时每次界面刷新都会经过它，所以重跑。

## CI 真装置（第二轮审查前，`21dd5586`，已被上面取代）

run [35749679293](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35749679293)，结论 success。

| 场景 | 结果 | 秒 | 起跑时已提交内存 |
| --- | --- | --- | --- |
| `real-onboard-compensate-then-reconnect-01` | PASS | 67 | 4.05 GiB（步骤内峰值 7.36 GiB） |

四行核对（读自 `Run real-onboard L2 scenarios` 那一步）：

```
control-server @ a98ae9be8433b3e170705a265a3c4fb6060fb6e7
8005-agv-onboard-hmi @ 21dd5586673b65c49b5b84b55837164c1d49b3b0
slots-simulator @ fb5f7c593742bf98bc3957b8729a38aad5321f28
```

`RIG_COMMIT_GUARD|RIG_DESKTOP_LOCK|RIG_DEADLINE`：全日志命中 1 行，带 `^[[36;1m`（源码回显）；不带前缀 0 行。

## CI 真装置（审查修正后、merge hmi#197 前，`19e2757e`，已被上面取代）

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

见 PR 正文。增量审查另有红提交 `f8bd74e`、修复 `7f8e40e`：变异「只要有请求就保留」→ F1、F2 两条都红；变异「去掉过期原因码那一项」→ 只红 F2。第二轮审查另有红提交 `8bd3291`、修复 `02268bb0`，三组变异（拒收保留、取消入口、扫码提交各恢复「修订号相等」）各只红预期用例。审查修正另有红提交 `febb20d`，变异：恢复「修订号必须相等」→ 两条端到端用例红在 `the control server to receive SublotSubmitted`；去掉「子批须在最新清单」→ 三条负面断言红。

以下为审查前的记录：红提交 `1490c28`：修复前 6 条新用例红、反例 2 支绿。修复提交 `23b4755` 之后：

- 三个产品文件退回红提交版本 → 6 条新用例全红；
- 撤请求后不发 `SUBLOT_ENTRY_WITHDRAWN` → 两条撤销用例红（视图层断言有判别力）；
- 判据改成「修订号更高就撤」→ 只红反例中服务端真实顺序那一支；
- 装货命令不撤请求 → 只红第 3 项那条。
