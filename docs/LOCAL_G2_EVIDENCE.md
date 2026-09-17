# 本机 WIRE_TO_GATE G2 证据

scripts/run-w2g-g2.ps1 为每次本机验证创建一个不可复用的证据目录。两种跑法：

    .\scripts\run-w2g-g2.ps1                     # 整个解决方案，一个结论
    .\scripts\run-w2g-g2.ps1 -Slice FP-IS-00     # 只证一个切片，另出 gate-result.json

不带 `-Slice` 时输出到 evidence/g2/<Tag>/<UTC时间>-<HMI commit>/；
带 `-Slice` 时多一层切片目录：evidence/g2/<Tag>/FP-IS-NN/<UTC时间>-<HMI commit>/。
`<Tag>` 取 `$expected.Tag`，当前是 `protocol-v2.0.0`。
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
钉住这条等式，投影只到本条线已实现的切片（`FP-IS-00`～`07`、`FP-IS-14`、`FP-IS-15`）。

## 出站报文 schema 校验（`schemaConformance`）

`WireToGateG2Tests` 在测试进程结束时，把本次经 `WireToGateProtocolSerializer.Create`／
`RebindSessionGeneration` 发出的每一条协议报文——车载端产品发的，与 `FakeControlServer` 发的——交给
独立进程 `tools/SQCD.Agv.SchemaConformance`，按本仓 `vendor/8005-agv-protocol` 的 schema 逐条校验
（8005-agv-onboard-hmi#74）。脚本把 `WIRE_TO_GATE_SCHEMA_REPORT_DIR` 指向本次的 `test-results/`，
`schema-coverage.json`、`schema-conformance.txt` 与（违约时）`schema-violations.json` 落在那里。

**一律按 `dotnet test` 的退出码判。** 违约以 test assembly cleanup failure 让退出码非 0，控制台摘要
却仍写 `Failed: 0`。

- 整仓那一趟一定跑到 `WireToGateG2Tests`，没有产出 `schema-coverage.json` 就判失败：删掉 fixture 不能让
  这道门禁无声消失。
- `-Slice` 那一趟选中了 `WireToGateG2Tests` 的测试时同样要求覆盖文件在；一条都没选中时
  `schemaConformance` 为 `null`，违约仍由退出码判。
- `summary.json` 与 `gate-result.json` 因此升到 `schemaVersion 1.2.0`，多出 `schemaConformance`
  （`linesChecked`、`linesInViolation`、`linesInKnownViolation`、`schemaCompilationMilliseconds`、
  覆盖文件相对路径）。
- 已知违约登记在 `tests/SQCD.Agv.WireToGateG2Tests/schema-known-violations.json`：每条要么指向已开的
  缺陷 issue，要么点名故意发它的测试（`deliberate`，只豁免替身的行，永远不豁免产品的行）。
- 只验出站，入站不验；messageType 覆盖只报告不判死——没被任何测试发出的消息，它的发送方法缺字段
  这道门禁看不见。
- 成本：整份 G2 多出约 45～75 秒 schema 编译（2026-09-17 本机实测四次：45、61、62、75 秒，随机器负载波动；校验本身约
  4 秒），测试本身约 8 秒。

## 身份校验

脚本逐字段核对 `$expected` 身份。当前绑定的是协议 `v2.0.0` 候选：commit
86575456c847041515b7b75e8851a00e0d939804（`fp/v2-candidate` 顶端，由 `8005-agv-program#96`
公布），`(profileId AGV_FULL_PRODUCT, protocolVersion 3)`，`releaseVersion 2.0.0`。

`Tag` 写的 `protocol-v2.0.0` **至今没有打出**，由 `8005-agv-program#97` 在同一个 commit 上创建，
所以脚本查的是「存在则必须指向候选 commit」而不是「必须存在」；`approvalStatus` 声称
`APPROVED_RELEASE` 时才要求 tag 必须存在。同时确认当前 protocol HEAD 是该候选的后继提交，再执行
protocol G1。这样协议仓库可以在候选之后继续增加不改变身份的 CI/文档提交；release manifest、
Schema bundle 和 vectors 哈希仍会逐项校验。工作区路径包含 # 时，脚本临时映射一个盘符运行 G1；
协议仓库本身不会被修改。

`approvalStatus` 是 `SUPERSEDING_CANDIDATE`，不是 `APPROVED_RELEASE`：发布需要一份批准
attestation 加注释 tag，两件都还没发生。在这个身份上跑出的 G2 只是开发态，不是正式证据，
不要当成已批准发布。`protocolVersion 3` 在 MVP 线 `WIRE_TO_GATE_MVP 0.3.0` 上也出现过，
读证据时连 `profileId` 一起读。

该证据只代表 OnboardHmi 本机 G2。summary.json 会明确保留
controlServerG2=PENDING_EXTERNAL 和 g3=PENDING_JOINT，不能把本机结果当作
ControlServer 或现场联合验收。
