# hmi#255 证据（`w2g/batch-p3/v3` 线，PR #257）

本目录是 PR #257 定稿版本 `33280059fdbf83a96c8ebdae73953d287bfc5373` 的证据。这条线的产品改动与 PR #256（`w2g/fp-v2-impl` 线）逐行相同。

## 1. 本机全量 ONBOARD_HMI_G2：`g2-full-3328005/`

- 命令：`pwsh -File scripts/run-w2g-g2.ps1 -ProtocolRoot <8005-agv-protocol 普通克隆，检出 3f091cb2>`。没有传 `-Slice`，跑的是整个解决方案。
- 结论：PASS，脚本退出码 0。
- `logs/dotnet-test-release.log`：
  - `SQCD.Agv.UnitTests`：`Passed!  - Failed:     0, Passed:   845, Skipped:     0, Total:   845`
  - `SQCD.Agv.WireToGateG2Tests`：`Passed!  - Failed:     0, Passed:   689, Skipped:     0, Total:   689`
  - 没有 `Test Assembly Cleanup Failure`。
- `test-results/schema-conformance.txt`：`0 distinct violations, 4 on file`。4 条全是已登记的有意违规；`schema-violations.json` 里没有 `onFile` 为空的项。
- `summary.json`：`status=PASS`，`hmi.commit` 就是上面的定稿提交，`workingTreeStatus=[]`；协议是 `protocol-v3.0.0` 候选（`3f091cb2`，`SUPERSEDING_CANDIDATE`）。
- 整个目录原样保留，合计约 60 KB。

## 2. CI 真装置

这条线不另跑。两条线的产品改动逐行相同，所以只在 `w2g/fp-v2-impl` 线上跑了一场：run 37331952427，`real-onboard-compensate-then-reconnect`，结论 success，判据 9 条全部 PASS。证据在 PR #256 分支的 `evidence/hmi-255/ci-real-rig-37331952427/`。

## 3. 红与绿：`red/`、`green/`

- `red/`：产品代码换回本票起点 `b5a44d6`，在上面跑本分支的用例。
  - 第一轮：执行器 5 条、G2 5 条全红。
  - 第二轮：执行器 7 条、G2 10 条全红。
- `green/green-round3.txt`：第三轮的定向运行。每一步都记 `dotnet test` 退出码和程序集收尾的 schema 一致性结论。
- `green/` 里文件头标注了"已由 green-round3.txt 取代"的那份，只记了汇总行，仅作留底。
- 变异只在 fp 线上跑，见 PR #256 的 `evidence/hmi-255/green/`。

## 没有入库的

- `48ff0201` 那轮全量被中途停掉，没有结论。
