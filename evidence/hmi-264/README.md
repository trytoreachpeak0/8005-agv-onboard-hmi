# hmi#264 证据：把 hmi#254、hmi#261、hmi#250 同步进 batch-p3/v3

## 哪些证据是 v3 的，哪些不是

`evidence/hmi-254/g2-full-9349f30/` 和 `evidence/hmi-250/g2-full-6aa0389/` 是 cherry-pick 时随 fp 线的提交一起带过来的，记录的是 `w2g/fp-v2-impl` 上、协议 `protocol-v2.0.0` 下的全量结果，**不是 v3 的**。`evidence/hmi-254/` 和 `evidence/hmi-250/` 下的其他文件也一样，都属于 fp 线。

本票自己的证据只在 `evidence/hmi-264/` 下。

## 判故障窗口用例（`declaration-window-green-red.txt`）

`StationDeadlineExpiredG2Tests.AnAckArrivingAfterTheDeclarationAbortedItsProgressIsTakenAndTheResultStillGoesOut`。

用例的做法是：替身扣住重新提示的 ack，等车载端写出「已执行服务端的人工判故障」之后才放行。这句日志在执行器停下之后才写，此时那次进度发送的等待已经随中止撤掉，所以放行的 ack 一定是迟到的。用例里还断言了放行之前这个 ack 确实没有发出。第一版用的是固定 1 秒延迟，按 PR #265 审查的意见改成了现在的写法。

- 修复后：通过。
- 修复前（在本分支上临时去掉接收循环里处理迟到 ack 的那一支）：在「发件箱记为已确认」处等待超时，日志是「收到未处理的WIRE_TO_GATE消息：DurableAck」。还原后重新构建。

两次都是单跑这一条用例，没有负载。改用例之后，`StationDeadlineExpiredG2Tests` 全类重跑 71/71。

## 全量 ONBOARD_HMI_G2（v3，`g2-full-b3f7718/`，目录原样保留）

提交 `b3f77188`，协议 `3f091cb`（protocol-v3.0.0，SUPERSEDING_CANDIDATE）：PASS，脚本退出码 0；build、test、format 退出码都是 0，protocol g1 PASS。UnitTests 853/853，G2 733/733。schema 收尾 19540 行，60 种消息类型，0 处新违约，已知违约 4 处。

条数对账：基线是 hmi#255 v3 那次全量（`3328005`），UnitTests 845，G2 689；从 `3328005` 到 `b7a8726`，测试文件没有变化。按源码里的 `[Fact]`、`[InlineData]` 数，三张票新增 UnitTests 7 条、G2 29 条，算出来应是 852 和 718。差额都是 `MemberData` 展开的行：G2 是 hmi#254 新增的 `WireToGateG2Tests.ContentConflictAbandon.cs` 里三个数据源驱动的用例，分别展开 7、4、4 行，共 15 条；UnitTests 是 hmi#254 往 `JournalSqliteFailureTests` 的数据源里加的一行 `MarkOutgoingAbandonedAsync`，1 条。补上后正好是 853 和 733。

这一轮之后，`0568251` 只改了测试（判故障窗口用例和替身的 `DelayDurableAck`），没有重跑全量；完整 G2 由转 ready 后的那一轮 CI 覆盖。

## CI 真装置（`ci-real-rig-37361302642/`）

control-server 的 `l2.yml`，run `37361302642`，`rig=real`，场景 `real-onboard-compensate-then-reconnect`，consecutive 模式跑 1 次。结论 success，场景 PASS，用时 84 秒，判据 L2-CR-00 到 L2-CR-08 共 9 条全部 PASS。

三个提交（见 `commits.json`）：control-server `392ca349`（`batch-p3/v3` 的顶，已含 cs#481 的 v3 修复，所以这一场测的是两张票合在一起的状态），onboard-hmi `0568251`，slots-simulator `fb5f7c59`。

只入库 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`、`commits.json`、内存采样 `commit-samples.csv` 和车载端应用日志 `onboard-app-log.txt`。服务端与模拟器的日志、数据库快照（附件合计约 2.4 MB）不入库，和 hmi#254 的做法一致。
