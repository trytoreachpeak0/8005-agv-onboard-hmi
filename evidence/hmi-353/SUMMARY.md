# hmi#353 证据：待送点清单显示全，每条一个「取消」（彻底取消）

票：https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/353
需求：CP-0014 修订项二（`REQ-0211`），基线 v1.13.0；追踪 program#184。服务端对应 cs#726。
协议：`4.0.0` 候选 `4d67655c`（`SUPERSEDING_CANDIDATE`），不改协议、不刷新 vendored 索引。证据所在提交：红 `ca98b87`、实现 `c4ae8e6`、滚动测试 `a5a3582`、补用例 `c6c349e`；审查第一阶段之后：M2 用例 `2ad9b4a`、修复 `d68bdf7`、P2 断言 `277d047`。全量 G2（06）跑在 `c6c349e` 上，审查之后的改动由 08～10 覆盖。

原始输出精简后入库，只留判定行。完整 G2 证据目录在本机
`F:/8005-scratch/g2-hmi353/protocol-v4.0.0/20261015T125028737Z-c6c349ef41e5`（未入库，体积大）。

| 文件 | 内容 | 结论 |
| --- | --- | --- |
| `01-red-at-ca98b87.txt` | 实现之前跑本票新用例（只有最小桩） | 待送点 6 条全红；机台回归 2 条是护栏，红阶段就是绿的。第一轮变异（04）没有一轮让它们变红，审查第一阶段 M1 指出后，第二阶段 P1、P2 才证明它们有用（08） |
| `02-format-verify.txt` | 本票改动的 `.cs` 文件 `dotnet format --verify-no-changes` | exit 0 |
| `03-check-ui-layout.txt` | `scripts/check-ui-layout.ps1`，新增 `worklistRowCancellation` 一项 | PASS |
| `04-mutations.txt` | 9 轮变异，每轮只改一个文件，跑完只还原那个文件并核对工作树干净；假条件取运行期的值 | 8 轮被抓住（M1 红 6、M2 红 2、M3 红 1、M4 红 1、M5 红 3、M6 红 1、M8 红 2、M9 红 1）；**M7 存活**，见下 |
| `05-unit-full.txt` | 单元测试项目全量 | 1212 通过 / 0 失败 |
| `06-full-g2-*` | `scripts/run-w2g-g2.ps1` 全量（G1、单元、G2、线上报文 schema 核对），head `c6c349e` | `PASS`：单元 1212/0，G2 983/0，schema 核对 23537 行 0 违规（已知违规 4 行，与 hmi#348 相同） |
| `07-*-6rows-*` | 1024×768（agv02 实读分辨率）下待送点 6 条需求的截图与量测，改前、改后 | 改前每条 51 像素、完整露出 2 条，清单自带滚动区吞掉滚轮与拖动；改后每条 35 像素、完整露出 4 条，左栏整栏可滚 |
| `08-phase2-mutations.txt` | 审查第一阶段之后的 4 轮变异 | P1 视图模型去掉待送点判断：机台用例红 2；P2 业务层去掉待送点判断：先存活，补断言（`277d047`）后红 2；P3 车上已装的排除失效：M2 新用例红 1；P4 L1 分支失效：L1 新用例红 1 |
| `09-phase2-targeted.txt` | 第二阶段定向：`MultiDemandJourneyG2Tests` 整类、`LoadCancellationBeforeSublotG2Tests`、屏幕状态与滚动测试，head `277d047` | 235 通过 / 0 失败（第一遍用了 `--no-build`、跑的是变异版二进制，作废） |
| `10-phase2-unit-full.txt` | 第二阶段单元测试项目全量 | 1212 通过 / 0 失败 |

**M7 为什么存活**：M7 让「业务状态变了就重算每行按钮」这一路失效。补了 `ARowCancellationBecomesPressableWhenTheEntryRequestArrivesAfterTheWorklist` 之后仍然绿。原因是录入请求到达等业务变化都会发操作员事件，产品（`App.xaml.cs`）和 G2 夹具在事件后都刷新日志状态，清单随之整张重建，行上的按钮状态就跟着更新了。所以在现有流程里，这一路是一层冗余保险，不是唯一入口。留给审查判断要不要补一个只走这一路的用例。

**截图的一点出入**：改前那张截图没有带 App 的全局按钮样式，按钮外观与实际略有不同；量出来的行高与可见行数不受影响。改后那张带了样式。

**第一轮变异作废**：第一次跑 M1～M7 时，过滤条件是按文件名写的，没有选中本票用例，所以「全部存活」不作数。改成按用例名过滤后重跑，上表是重跑的结果。

真装置：车载端界面改了，要跑 CI 真装置 `real-onboard-compensate-then-reconnect`，按调度顺序在推送、独立审查之后申请，结果补在这里。
