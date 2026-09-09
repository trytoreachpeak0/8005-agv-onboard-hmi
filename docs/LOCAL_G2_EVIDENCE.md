# 本机 WIRE_TO_GATE G2 证据

scripts/run-w2g-g2.ps1 为每次本机验证创建一个不可复用的证据目录。两种跑法：

    .\scripts\run-w2g-g2.ps1                     # 整个解决方案，一个结论
    .\scripts\run-w2g-g2.ps1 -Slice FP-IS-00     # 只证一个切片，另出 gate-result.json

不带 `-Slice` 时输出到 evidence/g2/protocol-v1.0.0/<UTC时间>-<HMI commit>/；
带 `-Slice` 时多一层切片目录：evidence/g2/protocol-v1.0.0/FP-IS-NN/<UTC时间>-<HMI commit>/。
两种都包含：

- summary.json：精确协议身份、HMI commit、切片向量、命令退出码和外部门禁；
- transcript.ndjson：运行事件顺序；
- journal.ndjson：身份绑定和本机验证结果摘要；
- logs/：protocol G1、Release build、test、format 的原始输出；
- test-results/：dotnet test 结果目录。

## `-Slice` 做什么

测试按 `--filter "IntegrationSlice=FP-IS-NN"` 过滤，`summary.json` 的 `slices` 只剩这一片。
**构建与 `dotnet format` 仍是全仓的**——它们是这棵树的属性，不是这一片的属性，按片收窄会让每份
证据只覆盖那片碰巧改到的文件。

带 `-Slice` 的运行**另写一份 gate-result.json**，与控制端 `test-wire-to-gate.ps1` 的
`schemaVersion 1.1.0` 对齐（`gate` 为 `ONBOARD_HMI_G2`），这样一条切片的两端证据能被同一个读法
读。**是超集，不是同一份字段表**：本端多出 `implementationBranch`（控制端跑在一条分支上，
本端跑在 `w2g/*`）、`recordedTestCount`（控制端只有一个测试工程，不会因文件名相撞丢 trx）、
以及 `buildExitCode`／`formatExitCode`（控制端那个脚本两样都不跑，本端两样都算进结论）。
按控制端 `1.1.0` 写的读法能读本端；要求字段集完全相等的读法不能。

`summary.json` 仍然保留：transcript、G1 结果与身份校验这三样 gate-result 里放不下。它自己的
`schemaVersion` 也一并升到 `1.1.0`——**两种跑法都升**，多出 `integrationSliceId`、
`selectedTestCount`、`recordedTestCount`、`integrationSliceIndexSha256` 四个字段（不带 `-Slice`
时都是 `null`）。让不带 `-Slice` 的那条留在 `1.0.0` 反而更糟：字段已经多了，版本号却说没多。

**选不中任何测试的切片会被拒绝，且是在建目录之前拒绝。** `dotnet test --filter` 选中 0 条时退出
码是 0（2026-09-09 本仓实测，`-Slice FP-IS-13`），不拒绝就会给一个没建的切片写出一份绿证据。

哪些测试属于哪一片，由测试上的 `[Trait("IntegrationSlice", ...)]` 决定，而那是每个测试自己的
`[Trait("ProtocolVector", ...)]` 按协议切片索引的投影——`IntegrationSliceTraitArchitectureTests`
钉住这条等式，投影只到本批次实现的八片（`FP-IS-00`～`07`）。

## 身份校验

脚本会校验 `protocol-v1.0.0` 这个 tag：**它至今没有打出**，所以查的是「存在则必须指向候选
commit」而不是「必须存在」，真正绑定的是候选 commit
f6ee75defe6e2d18f63f4082bee445dbb678ab1b。同时确认当前 protocol HEAD 是该候选的后继提交，再执行
protocol G1。这样协议仓库可以在候选之后继续增加不改变身份的 CI/文档提交；release manifest、
Schema bundle 和 vectors 哈希仍会逐项校验。工作区路径包含 # 时，脚本临时映射一个盘符运行 G1；
协议仓库本身不会被修改。

`approvalStatus` 是 `SUPERSEDING_CANDIDATE`，不是 `APPROVED_RELEASE`——规格 6.6 第 6 条要两名产品
负责人 attestation ＋ 注释 tag，两件都还没发生。证据里写明了这一点，不要当成已批准发布。

该证据只代表 OnboardHmi 本机 G2。summary.json 会明确保留
controlServerG2=PENDING_EXTERNAL 和 g3=PENDING_JOINT，不能把本机结果当作
ControlServer 或现场联合验收。
