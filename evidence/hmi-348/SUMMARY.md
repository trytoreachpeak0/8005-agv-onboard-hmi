# hmi#348 证据：待送点取货停靠隐藏「扫码前取消」，装货中取消改说「撤销本次开仓」

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/348
需求：CP-0012 修订项四（`REQ-0211`），基线 v1.12.0；追踪 program#184。
协议：`4.0.0` 候选 `4d67655c`（`SUPERSEDING_CANDIDATE`），不改协议。证据所在提交：红 `e268f35`、绿 `45d5cd5`。

原始输出精简后入库，只留判定行。完整 G2 证据目录在本机
`F:/8005-scratch/g2-hmi348/protocol-v4.0.0/20261015T071246601Z-45d5cd536cc0`（未入库，体积大）。

| 文件 | 内容 | 结论 |
| --- | --- | --- |
| `01-red-at-e268f35.txt` | 实现之前跑本票新用例 | 单元 1 红；G2 3 红（待送点不提供扫码前取消的业务层与界面各一条、待送点装货中取消的确认框说法）。机台回归与护栏用例在红阶段就是绿的，由变异证明有用 |
| `02-green-unit-tests.txt` | `45d5cd5` 单元测试项目全量 | 1212 通过 / 0 失败 |
| `03-green-g2-targeted.txt` | `45d5cd5` G2 定向：本票新用例、扫码前取消全类、出发全类、晚到按键、屏幕状态与架构测试 | 153 通过 / 0 失败 |
| `04-mutations.txt` | 5 轮变异，每轮只改一个文件，跑完还原并核对工作树干净 | 全红，存活 0：M1 删待送点判定红 2；M2 去掉「已发出那次例外」红 1；M3 按键绑定恒非待送点红 1；M4 按钮文字恒按机台红 1；M5 按钮文字恒按待送点红 4 |
| `05-format-verify.txt` | 改动文件 `dotnet format --verify-no-changes` | exit 0 |
| `06-full-g2-*` | `scripts/run-w2g-g2.ps1` 全量（G1 + 单元 + G2 + 线上报文 schema 核对） | `PASS`：单元 1212/0，G2 969/0，schema 核对 25262 行 0 违规（已知违规 4 行，与 hmi#349 相同） |
| `06-full-g2-first-attempt-g1-env.txt` | 全量第一次的 G1 日志摘录 | 环境红，不是车载端红：pnpm v11 不认 `npm_config_store_dir`，把 `.pnpm-store` 建进协议克隆，`g1-validate` 报 `EISDIR`。删掉并加设 `pnpm_config_store_dir` 后重跑即上一行 |
| `07-review-tests-green.txt` | 审查第二阶段要求补的两条用例（`b4d6a46`）在 head 上 | 2/0；连同扫码前取消全类、本票其余 G2 用例与 XAML 覆盖测试 39/0 |
| `08-review-tests-mutations.txt` | 两轮变异：R1 例外条件额外要求「已发出那条需求仍在清单里」；R2 删掉 `LoadCancellationCaption` 的变更通知 | 各红 1，分别是对应的新用例 |
| `09-format-verify-review-tests.txt` | 两个改动测试文件 `dotnet format --verify-no-changes` | exit 0 |

| `10-review-l1-notices.txt` | 审查 L1：待送点上叫操作员再按的三处提示改用「撤销本次开仓」（机台不变） | `dotnet format` exit 0；恢复标记架构测试 9/0。文案经用户确认（「文案可以」，C11 转达） |

真装置：CI 真装置 `real-onboard-compensate-then-reconnect` 一轮 PASS（83 秒），run `38721655537`（control-server `l2.yml`，`rig=real`，`cs-desktop`），车载端 `ef13809`、服务端 `e2320398`、模拟器 `fb5f7c59`；摘录见 `11-ci-real-rig-38721655537.txt`。随 hmi#353 的 PR 补入库（原件在 v2 工作区 `field-prep/departure-rules-2026-10-14/`）。
