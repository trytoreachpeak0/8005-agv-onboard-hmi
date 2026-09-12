# `ONBOARD_HMI_G2`：`FP-IS-14`／`FP-IS-15`（`ad0e507`，协议 `16e2567`）

**它取代以下两份，作为这两片车载端这一半的现行证据：**

- `FP-IS-14`：`../20260910-fp-is-14-15-v2-message-plane/FP-IS-14/protocol-v1.0.0/FP-IS-14/20260910T042025069Z-d9ac1a18837a/`
- `FP-IS-15`：`../20260910-fp-is-14-15-v2-message-plane/FP-IS-15/protocol-v1.0.0/FP-IS-15/20260910T042141809Z-d9ac1a18837a/`

那两份原样保留、一字未改，它们对 `d9ac1a1` 与协议 `f6ee75d` 仍然成立。

## 为什么要重跑

产品负责人 2026-09-12 批准了一次重跑，原因有三：

1. **协议候选换了。**`fp/v2-candidate` 补上了单人签名规则，协议仓提交为 `16e2567`。content manifest 从 `84f984ea…`
   变为 `25fd6689…`。车载端在 `e30d421` 跟上了这个身份。
2. **`FP-IS-15` 的车载端代码改了。**`a98679f` 把车载告警接到真实来源。在那之前，真车报上去的告警快照永远是空的。
   它还加了会话中途发布：告警和服务端手上那份不一致时，报一份新的全量快照。
3. **门禁脚本修了一个缺陷。**见下面「被纠正的那两份」。修复提交是 `ad0e507`，只改 `scripts/run-w2g-g2.ps1`，
   不改产品代码。

## 结论

| 片 | `status` | `selectedTestCount` | build／test／format 退出码 | `protocol.g1Status` | 向量 |
| --- | --- | --- | --- | --- | --- |
| `FP-IS-14` | **`PASS`** | 2 | 0／0／0 | **`PASS`** | `CV-SLOT-CONFIGURATION-ACTIVATION` |
| `FP-IS-15` | **`PASS`** | 3 | 0／0／0 | **`PASS`** | `CV-ONBOARD-ALARM-SNAPSHOT` |

两份身份逐字段一致：`implementationCommit` 为 `ad0e50705cb80a45ff6f1d05dba9250228364c7c`（分支 `w2g/b3-on-v2`）；
`protocolRepositoryCommit` 为 `16e2567a7033883f00fc999f7fa08f954dd13a26`；`protocolManifestSha256` 为
`25fd6689e8234b7d481874b408109cd27eb0f02fbb023225385d6642e9bfd3d0`；`protocolSchemaBundleSha256` 为 `225a8334…`；
`protocolApprovalStatus` 为 `SUPERSEDING_CANDIDATE`。

**这是 v2 线上第一次由 `ONBOARD_HMI_G2` 自己跑出协议 `G1` 的 `PASS`。**上一份两片的 `g1Status` 都是 `SKIPPED`，
因为协议仓当时是 linked worktree。这次 `-ProtocolRoot` 指向普通克隆 `C:\Users\szy\8005-b3\proto-g1-16e2567`，
脚本内部 G1 的 `candidateManifestSha256` 与服务端仓同日的协议 `G1` 证据一致。

## 与上一份的差

- `FP-IS-14`：2 条对 2 条。车载端在 `FP-IS-14` 名下没有改代码，只换了协议身份。
- `FP-IS-15`：1 条变为 3 条，原来那条保留，新增两条（`a98679f`）：
  - `AnAlarmRaisedMidSessionIsPublishedAsTheWholeCurrentSetAndTheSessionCarriesOn`：握手完成前不发。会话中途抬起的告警，
    以整份当下集合、revision 2 报上去，ack 之后记下，会话照常心跳。
  - `TheAlarmMonitorPublishesWhatItEvaluatedAndOnlyWhenTheServerDoesNotAlreadyHoldIt`：监视器只在服务端手上那份与告警板
    内容不同时才报。内容相同不重报，条件消失时报一份空快照。

## 装置

从 `hmi-b3` 本地 `git clone -b w2g/b3-on-v2` 出干净克隆 `C:\Users\szy\8005-b3\hmi-g2-ad0e507`，跑完 `git status` 仍然干净；
协议克隆也仍然干净。**PATH 前面特意加了 `C:\Program Files\nodejs`**，`pnpm` 解析到 `pnpm.ps1`。修复之前出问题的
正是这个环境，在这里跑通才能说明修复成立。两片各给一个 `-EvidenceRoot`，所以层级是
`<本目录>/<片>/protocol-v1.0.0/<片>/<运行 id>/`，与上一份相同。

## 被纠正的那两份

`../20260912-fp-is-14-15-single-owner-e30d421$s/` 下的两份都是 **`FAIL`**，绑定 `e30d421`，保留在原处未改。

**失败的只有协议 G1 前置步骤，车载端这一半当时就是好的。**两份各自的 `buildExitCode`、`testExitCode`、`formatExitCode`
都是 0，`selectedTestCount` 分别为 2 和 3。`summary.json` 里 `g1Status` 为 `NOT_RUN`，`logs/protocol-g1.log` 的原文是：

```
install: unknown option -- frozen-lockfile
[ERR_PNPM_RECURSIVE_EXEC_FIRST_FAIL] Command "install --frozen-lockfile" not found
```

意思是 pnpm 把 `install --frozen-lockfile` 当成一个命令名，转手交给了 GNU `install`。根因在
`run-w2g-g2.ps1` 的 `Invoke-ProtocolG1`：它用 `& $FilePath $数组` 把参数数组当成一个参数传出去。
PATH 上的 `pnpm` 是 corepack 的 `pnpm.ps1` 时，两个元素会被并成一个字符串。2026-09-09 没有暴露，是因为那时
pnpm 不在 PATH 上，脚本走的是 `node pnpm.cjs`，原生程序会把数组逐项展开。`ad0e507` 把 install 和 g1 两处都改为
splat。服务端仓 `run-staged-g3.ps1` 一直是 splat，不受影响。

那个目录名里的 `$s` 是同一次调用的另一处笔误：bash 调用里给路径写的反斜杠转义被工具参数先解码了一层，`$s` 成了
字面文字。这次重跑改用 pwsh 脚本文件，路径不再经过 shell 字符串转义。

## 未在本轮证明的

- `ONBOARD_HMI_G2` 只证车载端这一半。车载端报出的真实告警在服务端看板上显示成什么样，由 `G3` 证明。
- **告警条件本身没有在真车上验证。**IO 离线、锁反馈丢失、行驶中仓门未锁这些，这里证的是求值规则与发布路径，
  不是现场 IO 真的会那样变化。
- `protocol-v1.0.0` 这个 tag **尚未打**，没有任何人签过 attestation。本次绑定的是 commit。
