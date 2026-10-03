# hmi#242 证据

精简后入库，范围由调度定。原始全量在控制端工作区 `evidence/hmi242/`，合入后删除。

## `g3-real-onboard/`：cs#410 的 G3 场景，真车载端

- 场景：`g3-unable-to-charge-field-confirmation` ×1，2026-10-02，调度放行的本机真装置时段。
- 三端：服务端 `424e149516cbed5c255c09e3ada5590dd2dca613`（cs#410 分支，detached worktree），车载端 `8694d8fce1a54b42d35064470a0eeb3cd571f292`（本 PR），模拟器 `fb5f7c593742bf98bc3957b8729a38aad5321f28`，`rig=RealOnboard`（见 `SUMMARY.md`）。
- 结果：PASS，G3-13-21～27 共 7 条全部通过；G3-13-27（界面结果一行 `UnableToChargeStatus`）读到 `CONFIRMED`。
- 留下的文件：`SUMMARY.md`、`assertions.json`、`timeline.jsonl`；`onboard-log-excerpt.txt` 是车载端日志里提交与收到 Confirmed 的两行；`timeline-excerpt-result-and-clearing.jsonl` 是时间线里结果、界面读数与清桩中业务状态被确认那三行。
- 两端日志里都没有记录清桩中业务状态的行，它只出现在 `timeline.jsonl`（`clearing-state-acknowledged`）。
- 删掉的：`logs/`（除上面两行）、`snapshots/`、`field-operator-roles.json`。

**限度。** G3-13-27 读到一次 `CONFIRMED` 即通过。时间线是：09:50:26.605Z 车载端记下收到 Confirmed；09:50:27.27Z 探针读到 `CONFIRMED`；09:50:27.39Z 之前清桩中状态已被确认。清桩中状态的生成时刻只精确到秒，所以单凭这一次，定不死「读到时清桩中已经到了」。持续不消失由 G2 用例 `AConfirmedResultStaysOnTheLineThroughTheClearingItLeadsTo` 断言。场景改为「确认之后持续断言」由 cs#410 跟进。

## `g2-full/`：车载端全量 G2

`run-w2g-g2.ps1`（protocol-v2.0.0，经 `Invoke-HeavyLocal.ps1`）对 `8694d8fc`：PASS，1287 通过（610 + 677），0 失败，0 跳过。只留 `summary.json`。
