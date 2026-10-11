# 本机 WIRE_TO_GATE G2 证据

scripts/run-w2g-g2.ps1 为每次本机验证创建一个不可复用的证据目录。三种跑法：

    .\scripts\run-w2g-g2.ps1                     # 整个解决方案，一个结论
    .\scripts\run-w2g-g2.ps1 -Slice FP-IS-00     # 只证一个切片，另出 gate-result.json
    .\scripts\run-w2g-g2.ps1 -AllSlices          # 一次证多片，每片一份目录与 gate-result.json（见下文「一次跑多片」）

批次出口的按片证据走 CI 的手动入口，不在本机串行跑，见「在 CI 上按片跑」。

不带 `-Slice` 时输出到 evidence/g2/<Tag>/<UTC时间>-<HMI commit>/；
带 `-Slice` 时多一层切片目录：evidence/g2/<Tag>/FP-IS-NN/<UTC时间>-<HMI commit>/。
`<Tag>` 取 `$expected.Tag`，批次分支 `w2g/batch-p3/v3` 上是 `protocol-v3.0.0`（集成分支上仍是 `protocol-v2.0.0`）。
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
码是 0（2026-09-09 本仓实测，`-Slice FP-IS-13`；那时它还没建，onboard-hmi#222 翻面之后它能选中测试，今天还没建的是
`FP-IS-09`），不拒绝就会给一个没建的切片写出一份绿证据。

哪些测试属于哪一片，由测试上的 `[Trait("IntegrationSlice", ...)]` 决定，而那是每个测试自己的
`[Trait("ProtocolVector", ...)]` 按协议切片索引的投影——`IntegrationSliceTraitArchitectureTests`
钉住这条等式，投影只到本条线已实现的切片。哪些切片算已实现，以
`ProtocolVectorTestBindingArchitectureTests.SlicesThisLineImplements` 为准（`IntegrationSliceTraitArchitectureTests`
经 `ImplementedSlices()` 读它，并断言片数），当前是 `FP-IS-00`～`08`、`FP-IS-10`～`15`，共 15 片（`FP-IS-08` 于批次 7
由 onboard-hmi#134 加入，`FP-IS-12` 于批次 8 由 onboard-hmi#217 加入，`FP-IS-13` 于批次 9 由 onboard-hmi#222 加入——三张
车载端充电票的最后一张，它的四条向量在本端都有了具名测试之后才翻面）。

## 一次跑多片：`-Slice A,B,C` 与 `-AllSlices`

    .\scripts\run-w2g-g2.ps1 -Slice FP-IS-00,FP-IS-04   # 指定几片，逗号分隔
    .\scripts\run-w2g-g2.ps1 -AllSlices                 # 切片索引里所有能在本仓选中测试的片

批次出口要逐片出证。逐片调用时每片都重做一遍构建、`dotnet format` 与协议 G1，而这三样是这棵树的属性、
与切片无关：批次 8 出口 15 片本机串行 31 分钟，其中 format 629 秒、G1 100 秒都是同一个结论说了 15 遍
（onboard-hmi#295）。多片调用把它们各做一次，再逐片 `dotnet test --no-build --filter IntegrationSlice=…`。

- **每片的目录与单片调用完全相同**：`evidence/g2/<Tag>/FP-IS-NN/<UTC时间>-<HMI commit>/`，同一次调用的各片共用
  一个时间戳。片目录里照旧有 `summary.json`、`gate-result.json`、`transcript.ndjson`、`journal.ndjson`、
  `logs/`、`test-results/`，按单片读法读任何一片都不用改。
- **共享步骤的日志在片目录里是副本。** `logs/dotnet-build-release.log`、`dotnet-format-verify.log`、
  `protocol-g1.log` 是那一次构建、format、G1 的日志拷贝，不是每片重跑；原件在
  `evidence/g2/<Tag>/multi-slice/<同一时间戳>-<commit>/logs/`，同目录还有共享步骤的 `transcript.ndjson`
  与 `multi-slice-summary.json`（每片一行：状态、选中条数、片目录相对路径、失败原因；`-AllSlices` 时另列
  `slicesWithoutTests`，即脚本里 `$slicesNotImplementedHere` 表列出的本条线还没实现的片，今天是 `FP-IS-09`）。
  共享目录的 `transcript.ndjson` 与每片自己的 `transcript.ndjson` 各有一条 `run.started`：前者
  `evidenceType` 为 `ONBOARD_HMI_LOCAL_G2_MULTI_SLICE`，后者是该片的，同一次调用会出现两条，不是跑了两次。
- **`summary.json` 与 `gate-result.json` 升到 `schemaVersion 1.3.0`**，新增 `multiSliceRun`：多片调用时写
  `runId`、共享目录相对本片目录的路径、同批的片、共享的三个步骤、G1 状态与协议 HEAD commit；单片与整仓调用
  写 `null`。单片调用的其余字段与目录结构不变，只是 `dotnet format` 改在测试之前跑（transcript 里两条命令
  顺序对调）。
- **一片红只红那一片。** 每片单独判 PASS／FAIL，所有片的证据都写完之后脚本才以非 0 退出，错误信息按片列出。
  共享步骤（身份校验、G1、构建、format）失败则每片都 FAIL，因为它们是每片结论的一部分。
- **预检照旧，且整批一起拒绝。** `-Slice` 里点名的任何一片选不中测试，整次调用在建目录之前就被拒绝，
  与单独点它时一样。`-AllSlices` 只跳过脚本里 `$slicesNotImplementedHere` 表列出的片，并记进
  `slicesWithoutTests`；不在表里却选不中测试的片同样整批拒绝。跳过与否不能看「选不中测试」来推：一片已实现切片的
  测试全丢了（trait 写错、测试工程退出解决方案）也选不中，推出来的结论会是「没建」，给它和其余各片写出一批绿证据
  （onboard-hmi#295 审查 M1，与 `docs/defects/20260908-empty-slice-filter-mints-a-green-g2.md` 同一个洞）。
  `IntegrationSliceTraitArchitectureTests` 替代不了这道检查：它只在 push／PR 那一轮跑，按片那一轮不跑，所在的测试
  工程退出解决方案时更是哪一轮都不跑。表是 `ProtocolVectorTestBindingArchitectureTests.SlicesThisLineImplements` 之外的
  第二份副本，由 `G2ScriptSliceTableArchitectureTests` 逐项比对切片索引减去已实现的那组；表里的片若选中了测试，
  `-AllSlices` 也会拒绝，提示把它从表里删掉。

### 在 CI 上按片跑（批次出口用这条，不占本机）

`.github/workflows/test.yml` 有手动入口：

    gh workflow run test.yml -R trytoreachpeak0/8005-agv-onboard-hmi --ref <分支> [-f slices=FP-IS-00,FP-IS-04] [-f protocol_ref=<tag 或完整 SHA>]

- `slices` 默认 `all`（即 `-AllSlices`），也可以给逗号分隔的片号；`protocol_ref` 留空就按
  `WireToGateProtocol.cs` 绑定的那个检出（已发布按 `Tag`，候选按 `Commit`）。给了 `protocol_ref` 时要注意：
  脚本只核绑定的 commit 是它的祖先、tag 若存在指向绑定的 commit，**切片清单读的是检出的那份协议**。指向一个
  切片索引已经改过的后继提交，证出来的片与 `vectorIds` 就不是本仓绑定版本的那一份，`gate-result.json` 里的
  `integrationSliceIndexSha256` 会与绑定版本不同。批次出口留空即可。
- 这一轮**跑协议 G1**：win11-01 上全机装着 node 与 pnpm（`C:\Program Files\nodejs`，协议仓自己的 `g1.yml`
  用的就是它们，两个 runner 是同一个 `NETWORK SERVICE` 账号）。push 与 PR 触发的那一轮不变：整个解决方案一遍、
  `-SkipProtocolG1`。
- 证据上传为 artifact `g2-slice-evidence`，结构就是上面的 `protocol-v*/FP-IS-NN/…` 与 `protocol-v*/multi-slice/…`：
  `gh run download <run> -R trytoreachpeak0/8005-agv-onboard-hmi -n g2-slice-evidence -D <目录>`。
- 读结论的顺序：**先看 run 的结论（`gh run view <run> --exit-status`）和 `multi-slice-summary.json` 在不在**，
  再读各片 `gate-result.json`。每片跑完就写自己的证据，汇总在最后才写；进程中途被杀（超时、runner 掉线）时，
  已跑完的片目录里留着 PASS，没跑到的片根本没有目录，只读片目录会把半轮当成整轮。某片红时
  `multi-slice-summary.json` 的 `slices[].failures` 写着原因。车载端 G2 在 CI 下有已知不稳的用例（例如 onboard-hmi#299 迟到 ack、
  onboard-hmi#281），某片红先对照这些再归因。

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

脚本逐字段核对 `$expected` 身份。批次分支 `w2g/batch-p3/v3` 上绑定的是协议 `v3.0.0` 候选：commit
3f091cb2eae7c58cec54a95dd9389c9180bc7b4c（协议仓分支 `batch-p3/protocol-v3.0.0-candidate`，身份表由
`8005-agv-program#151` 关闭评论公布，`8005-agv-onboard-hmi#214`），`(profileId AGV_FULL_PRODUCT, protocolVersion 4)`，
`releaseVersion 3.0.0`。

`Tag` 写的 `protocol-v3.0.0` **尚未打出**，由 `8005-agv-program#152` 在同一个 commit 上创建，
所以脚本查的是「存在则必须指向候选 commit」而不是「必须存在」；`approvalStatus` 声称
`APPROVED_RELEASE` 时才要求 tag 必须存在。同时确认当前 protocol HEAD 是该候选的后继提交，再执行
protocol G1。release manifest、Schema bundle 和 vectors 哈希仍会逐项校验。工作区路径包含 # 时，脚本临时
映射一个盘符运行 G1；协议仓库本身不会被修改。CI（`.github/workflows/test.yml`）按同一对常量选检出：
`ApprovalStatus` 为 `APPROVED_RELEASE` 时按 `Tag` 检出，否则按 `Commit` 检出。

`approvalStatus` 是 `SUPERSEDING_CANDIDATE`，不是 `APPROVED_RELEASE`：发布需要一份批准
attestation 加注释 tag，两件都还没发生。在这个身份上跑出的 G2 一律标 `UNRELEASED_CANDIDATE`，只是开发态，
不计入批次出口。`protocolVersion` 只在同一 `profileId` 内单调递增，读证据时连 `profileId` 一起读。

该证据只代表 OnboardHmi 本机 G2。summary.json 会明确保留
controlServerG2=PENDING_EXTERNAL 和 g3=PENDING_JOINT，不能把本机结果当作
ControlServer 或现场联合验收。
