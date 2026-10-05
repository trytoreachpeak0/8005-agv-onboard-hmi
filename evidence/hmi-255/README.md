# hmi#255 证据（`w2g/fp-v2-impl` 线，PR #256）

本目录是 PR #256 定稿版本 `ded23c0c1bd3206d74d42ead34e7fa7a2c3097c9` 的证据。这个版本已合入 `w2g/fp-v2-impl` 的 `0d857753`（含 hmi#254 的 PR #258）。

## 1. 本机全量 ONBOARD_HMI_G2：`g2-full-ded23c0/`

- 命令：`pwsh -File scripts/run-w2g-g2.ps1 -ProtocolRoot <protocol-v2.0.0 的普通克隆>`。没有传 `-Slice`，跑的是整个解决方案。
- 结论：PASS，脚本退出码 0。
- `logs/dotnet-test-release.log`：
  - `SQCD.Agv.UnitTests`：`Passed!  - Failed:     0, Passed:   712, Skipped:     0, Total:   712`
  - `SQCD.Agv.WireToGateG2Tests`：`Passed!  - Failed:     0, Passed:   660, Skipped:     0, Total:   660`
  - 没有 `Test Assembly Cleanup Failure`。
- `test-results/schema-conformance.txt`：`0 distinct violations, 4 on file`。4 条全是已登记的有意违规；`schema-violations.json` 里没有 `onFile` 为空的项。
- `summary.json`：`status=PASS`，`hmi.commit` 就是上面的定稿提交，`workingTreeStatus=[]`；协议是 `protocol-v2.0.0`（`86575456`，`APPROVED_RELEASE`）。
- 整个目录原样保留，合计约 60 KB。

## 2. CI 真装置：`ci-real-rig-37331952427/`

- Run：https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/37331952427
  - `l2.yml`，`rig=real`，只跑一场 `real-onboard-compensate-then-reconnect`。
  - 结论 success：job `real-rig` 为 success；`scenarios` 为 skipped，这是 `rig=real` 时的正常分支。
- `commits.json`：三个提交与派发时一致。
  - control-server：`ca32779932afb3f1f9bd76624af4114db815d77a`
  - onboard-hmi：`ded23c0c1bd3206d74d42ead34e7fa7a2c3097c9`
  - slots-simulator：`fb5f7c593742bf98bc3957b8729a38aad5321f28`
- `run-SUMMARY.md`：整次运行的摘要，场景 PASS，用时 84 秒。
- `SUMMARY.md`、`assertions.json`：场景结论 PASS；判据 `L2-CR-00` 到 `L2-CR-08` 共 9 条，全部 PASS。
- `timeline.jsonl`：场景时间线。
- `onboard-app-log.txt`：车载端应用日志，整份 77 行。
  - 车载端界面和恢复入口一改，就要跑这一场，看的是恢复入口在几次重连之间怎么判（"判恢复入口"那几行）。
  - 补偿向量正常进入 `EXECUTING`。
  - 日志里没有"未能确认已关好""门可能还开着""IO 读数过期""开锁输出没有复位"，也没有 `RECOVERY_BLOCKED`：本票新加的按键门禁在正常流程里没有误拦。
- `commit-samples.csv`：vm01 已提交内存的采样，56 次，在 4.47 到 7.48 GiB 之间（上限 16 GiB），这一轮没有负载压力。

## 3. 红、绿与变异：`red/`、`green/`

- `red/`：产品代码换回本票起点 `0eccfd8`，在上面跑本分支的用例。
  - 第一轮：执行器 5 条、G2 5 条全红。
  - 第二轮：执行器 7 条、G2 10 条全红。
- `green/green-round3.txt`：第三轮的定向运行。每一步都记 `dotnet test` 退出码和程序集收尾的 schema 一致性结论。
- `green/green-merge-0d85775.txt`：合入 `0d857753` 后的定向运行，退出码 0，schema 0 条违规。
- `green/mutations-fp-round2.txt`：19 个变异杀了 18 个。存活的审查 B1 是等价变异，组合变异的记录见 `green/mutation-r-b1-combined.txt`。
- `green/mutations-fp-round3-exit-text.txt`：出口文案变异 F1 到 F3 全部被杀，每条都记了退出码。
- `green/` 里文件头标注了"已由 green-round3.txt 取代"的几份，只记了汇总行，没有记退出码，仅作留底。

## 没有入库的

- artifact 里的其余内容：服务端、假 RIoT、假 MesIngest、模拟器、协议故障代理、打包导入的日志，以及数据库快照。合计约 2.4 MB，与本票的改动无关；需要时按 run id 从 GitHub 下载。
- 作废的几轮全量 G2：
  - `73f3fd9a` 那轮是 PASS，但之后改了产品代码。
  - `134c38a6` 那轮的用例全过，但程序集收尾报了一条替身报文缺 `correlationId`，已在 `75417181` 修掉。
  - `48ff0201`（v3）那轮被中途停掉，没有结论。
