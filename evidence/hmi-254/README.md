# hmi#254 证据（第一部分：发件箱放弃）

本目录是 PR #258 定稿版本 `9349f3024204f0075abba2b23e1912e458c03790` 的两项证据。

## 1. 本机全量 ONBOARD_HMI_G2：`g2-full-9349f30/`

- 命令：`pwsh -File scripts/run-w2g-g2.ps1 -ProtocolRoot <protocol-v2.0.0 的普通克隆>`，没有传 `-Slice`，跑的是整个解决方案。
- 结论：PASS，退出码 0。
- `logs/dotnet-test-release.log`：
  - `SQCD.Agv.UnitTests`：`Passed!  - Failed:     0, Passed:   704, Skipped:     0, Total:   704`
  - `SQCD.Agv.WireToGateG2Tests`：`Passed!  - Failed:     0, Passed:   650, Skipped:     0, Total:   650`
- `summary.json`：`status=PASS`，`hmi.commit` 是上面的定稿提交；协议是 `protocol-v2.0.0`（`86575456`，`APPROVED_RELEASE`），`g1Status=PASS`；build、test、format 的退出码都是 0。
- 整个目录原样保留，没有删减，合计约 26 KB。

## 2. CI 真装置：`ci-real-rig-37297701239/`

- Run：https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/37297701239 ，`l2.yml`，`rig=real`，只跑一场 `real-onboard-compensate-then-reconnect`。结论 success。
- `commits.json`：三个提交与派发时一致。
  - control-server：`ca32779932afb3f1f9bd76624af4114db815d77a`
  - onboard-hmi：`9349f3024204f0075abba2b23e1912e458c03790`
  - slots-simulator：`fb5f7c593742bf98bc3957b8729a38aad5321f28`
- `SUMMARY.md`、`assertions.json`：结论 PASS；判据 `L2-CR-00` 到 `L2-CR-08` 共 9 条，全部 PASS。
- `timeline.jsonl`：场景时间线。
- `onboard-app-log.txt`：车载端应用日志，整份 71 行。这一场要看的是恢复入口在几次重连之间怎么判（「判恢复入口」那几行），车载端界面改动要跑这一场就是为此。日志里没有 `DURABLE_MESSAGE_ABANDONED`、「已放弃」「拒收」：正常流程里没有任何一行被误放弃。
- `commit-samples.csv`：vm01 已提交内存的采样，57 次，在 4.44 到 7.62 GiB 之间（上限 16 GiB），这一轮没有负载压力。

### 没有入库的

- artifact 里的其余内容：服务端、假 RIoT、假 MesIngest、模拟器、协议故障代理的日志，以及数据库快照。合计约 2.4 MB，与本票的改动无关；需要时按 run id 从 GitHub 下载。
- 更早两版的全量 G2（`8e9ddf70`、`03ab4a30`）：都是 PASS，但代码之后又改过，已经作废。
- 变异测试的逐轮输出：结果已经整理在 PR 正文的「变异」一节里。
