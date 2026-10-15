# hmi#354 证据：开锁后门没关的「请关门」横幅

协议：`4.0.0` 候选 `3fb214e5`（`SUPERSEDING_CANDIDATE`），不改协议、不刷新 vendored 索引。
证据所在提交：取红 `4da1f31`、实现 `0f553ee`、布局断言收紧 `04708b5`、按用户决定改 2 分钟 `e3e48b3`。01～02 在改 2 分钟之前，03～07 都在 `e3e48b3` 上。

原始输出精简后入库，只留判定行。完整 G2 证据目录在本机
`F:/8005-scratch/g2-hmi354/protocol-v4.0.0/20261015T165302460Z-e3e48b353a63`（未入库，体积大）。

| 文件 | 内容 | 结论 |
| --- | --- | --- |
| `01-red-at-4da1f31.txt` | 空壳实现下跑新测试 | 9 条里 7 条按预期失败；另 2 条是「不该出现横幅」的用例，空壳下本应通过 |
| `02-render-*` | 把真的 `MainWindow.xaml` 按 1004×660（车载屏 1024×768 最大化后的客户区）渲染：门锁着、黄底、红底亮相位、红底浅相位，以及各行实测高度 | 无横幅时布局不变（仓位区 329）；有横幅时横幅 99、仓位区 230、操作记录 110；大字一行放得下最长的两仓文案。渲染时升级门槛还是 60 秒，红底两张取在第 134、135 秒，改成 120 秒后同样是红底档 |
| `03-mutations.txt` | 11 个变异，每个只改一个文件、只跑 `DoorOpenPrompt` 两个测试类 | 11 个全部被断言抓到。M5、M6 第一版因编译错误（CS8602）「被抓」，作废，换成能编译、运行期取值的假条件重跑 |
| `04-format-verify.txt` | `dotnet format --verify-no-changes` | exit 0 |
| `05-architecture.txt` | 两个测试项目的 `ArchitectureTests` | 单元 203/0，G2 项目 12/0 |
| `06-full-g2-*` | `scripts/run-w2g-g2.ps1` 全量（G1、单元、G2） | `PASS`：单元 1261/0，G2 991/0 |
| `07-check-ui-layout.json` | `scripts/check-ui-layout.ps1`（新增 `visibleDoorOpenPrompt`） | `PASS` |
| `08-ci-real-rig-38773775218.txt` | CI 真装置 `real-onboard-compensate-then-reconnect` ×1（`l2.yml`，`rig=real`；cs `ef992e98`、onboard `457dcac2`、sim `fb5f7c59`） | `PASS`，104 秒 |

## 审查第一阶段之后（M1～M3 返工）

提交：字段前置 `9e31534`、取红 `4ccf215`、实现 `982de1a`、M3 布局用例 `4a7a32d`。09 在 `4ccf215` 上，10～14 在 `4a7a32d` 上。

| 文件 | 内容 | 结论 |
| --- | --- | --- |
| `09-review-red-at-4ccf215.txt` | M1、M2 新用例，在排除与按仓计时还没加时跑 | 视图模型 3 条、G2 端到端 1 条按预期失败（恢复向量下横幅照出；读数断后计时清零，120 秒仍是黄色）。快照字段先单独提交，所以红的是断言而不是编译 |
| `10-crowded-*` | 最挤的提示区（红色横幅＋取消装货＋期待动作超时＋离站倒计时），1004×660 与 989×640 | 都没有被裁；操作记录 110；仓位区 148／128。989×640 下仓位卡片只露出标题与门锁那一行，「货物」那行要滚动 |
| `11-review-mutations.txt` | 14 个变异（含 R1 去掉恢复向量排除、R1b 业务服务不打标记、R2 去掉 `IsKnown: true`、R2b 读数未知清零计时） | 14 个全部被断言抓到，没有编译错误 |
| `12-review-format-verify.txt` | `dotnet format --verify-no-changes` | exit 0 |
| `13-review-architecture.txt` | 两个测试项目的 `ArchitectureTests` | 单元 203/0，G2 项目 12/0 |
| `14-review-full-g2-*` | `run-w2g-g2.ps1` 全量 | `PASS`：单元 1261/0，G2 997/0 |

CI 真装置 08 跑在审查之前的 `457dcac2` 上；返工在 `MainWindow.xaml` 里只改了注释；改动的是横幅什么时候出现（恢复向量不出、读数未知时收起），是否需要重跑由调度定。

## 并入 hmi#353 之后

`git merge origin/w2g/fp-v2-impl`（`7bcd5b7b`，含 hmi#353），没有冲突，合并提交 `4a8d5df`。15～17 都在 `4a8d5df` 上。

| 文件 | 内容 | 结论 |
| --- | --- | --- |
| `15-merge-format-verify.txt` | `dotnet format --verify-no-changes` | exit 0 |
| `16-merge-targeted.txt` | 相关 G2 定向：本票测试类、`WorklistScrollingLayoutTests`、`MultiDemandJourneyG2Tests` 整类、`HmiScreenState*` | 227/0 |
| `17-merge-full-g2-*` | `run-w2g-g2.ps1` 全量 | `PASS`：单元 1261/0，G2 1011/0 |
