# hmi#215 证据

精简后入库，范围由调度定。四轮真装置都是 CI `l2.yml`（`rig=real`）在 vm01 交互式 runner 上跑的，原始证据是各 run 的 artifact；本地下载的副本没有入库。模拟器都是 `fb5f7c593742bf98bc3957b8729a38aad5321f28`。每个目录只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`；`logs/`、`snapshots/` 删掉了。

## `real-rig-pass/`：新场景 PASS

- run 37103989833，场景 `real-onboard-slot-fault-declaration` ×1。
- 服务端 `30e80f73492636cbddfa3e62b6d829ffd149b4ab`（`test/hmi215-rsfd-scenario-fix`，修了场景脚本在 StrictMode 下读不存在属性的问题），车载端 `b9cd47f74e456a75093c9aa97083db0e7795005e`（本 PR）。
- 结果：PASS，L2-RSFD-01～07 全部通过。03：`OperationResult` UNKNOWN，被判仓 UNKNOWN 带 `SLOT_FAULT_DECLARED`；04：旅程 Blocked / `LOAD_RESULT_REQUIRES_RECOVERY`；05：仓位操作 RecoveryRequired；07：判定后空关门，10 秒后门仍关着（`CLOSED/EMPTY/1/0`），UNLOCKING 进度条数与判定前相同（1）。

## `real-rig-red/`：红证据，车载端缺陷版本

- run 37104193491，同一场景、同一服务端提交。
- 车载端 `2c1e07a20344fe687fdbcb4dd76e6c4b89ade0ff`：分支 `w2g/hmi215-red-applied-only`，在 `b9cd47f7` 上只改一处——收到判定只回 APPLIED，不碰执行器、不写日志、不出 `OperationResult`（即照搬取消接手的「中止即沉默」之后的样子）。从未合入。
- 结果：FAIL，红的位置与事先报给调度的预期一致：01、02、06 PASS；03 FAIL `(no OperationResult)`；04 FAIL `AwaitingLoadResult`；05 FAIL `Prepared`；07 FAIL `OPEN/EMPTY/0/0 / UNLOCKING 2`（空关的门被重开）。

## `real-rig-compensate-then-reconnect/`：恢复入口回归

- run 37102766873，场景 `real-onboard-compensate-then-reconnect` ×1，服务端 `3cf62fecd28c402fe9fc54a144b20a85583edd4b`（`batch-p3/v3`），车载端 `b9cd47f7`。
- 结果：PASS。

## `real-rig-first-run-script-error/`：第一轮新场景，脚本自身异常

- 同一个 run 37102766873，`real-onboard-slot-fault-declaration`，服务端 `3cf62fec`，车载端 `b9cd47f7`。
- 结果：FAIL，失败原因「在此对象上找不到属性“PayloadJson”」——场景脚本在 L2-RSFD-03 拼实际值时读了 `Get-L2OperationResults` 返回对象上没有的属性，StrictMode 下抛出，03～07 没有记上。不是产品判据红；01、02 已 PASS。脚本修复在服务端 `test/hmi215-rsfd-scenario-fix`。留 `SUMMARY.md` 与 `assertions.json` 作记录。

## `g2-full/`：车载端全量 G2

`run-w2g-g2.ps1`（protocol-v3.0.0 候选，`-ProtocolRoot` 指向 `8005-agv-protocol@3f091cb2` 的普通克隆）对 `b9cd47f7`：PASS，UnitTests 796/796，WireToGateG2Tests 662/662，0 失败。只留 `summary.json`。候选身份，`UNRELEASED_CANDIDATE`，不计入批次出口。
