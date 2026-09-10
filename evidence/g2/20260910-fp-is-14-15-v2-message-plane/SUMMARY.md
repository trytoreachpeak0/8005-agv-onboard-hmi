# `ONBOARD_HMI_G2`：`FP-IS-14`／`FP-IS-15`（`d9ac1a1`）

#27／#28 搬到协议 v2 线、并把消息 7／8／9 补上线之后，这两个切片第一次具备被 G2 证明的条件。
**这是第一次证明，不是沿用任何既有结论。**

## 结论

| 片 | `status` | `selectedTestCount` | `testExitCode` | 向量 |
| --- | --- | --- | --- | --- |
| `FP-IS-14` | **`PASS`** | 2 | 0 | `CV-SLOT-CONFIGURATION-ACTIVATION` |
| `FP-IS-15` | **`PASS`** | 1 | 0 | `CV-ONBOARD-ALARM-SNAPSHOT` |

绑定 `d9ac1a18837a2684ba67c3aeab55258d291d596b`（分支 `w2g/b3-on-v2`）
＋ `protocol-v1.0.0@f6ee75defe6e2d18f63f4082bee445dbb678ab1b`。

## 协议 `G1` 没有运行，且这一点必须读到

两份 `summary.json` 的 `protocol.g1Status` 都是 `SKIPPED`。**`gate-result.json` 里没有这个字段**，
所以只看 `gate-result.json` 会以为 G1 跑过。读结论的人请一并读 `summary.json`。

原因不是选择性跳过。协议仓这次是以一个 **linked worktree** 提供的
（`C:\Users\szy\8005-b3\proto-v2c`，detached 在 `fp/v2-candidate` 顶 `f6ee75d`），
而 `g1-validate.mjs` 的排除规则写作 `p.startsWith(".git/")`，假定 `.git` 是目录；linked worktree 的
`.git` 是一个文件，排不掉，G1 会因为多出恰好一个条目而报 `manifest file count` FAIL。
那是**装置形状造成的假失败，不是协议内容的问题**，`run-w2g-g2.ps1` 自己认出这个形状并给了两条出路
（换一份普通克隆，或 `-SkipProtocolG1` 并在证据里说明）。本目录走的是第二条。

本轮范围本就只有两道 G2（用户 2026-09-10 的决定），G1 不在其中。

## 目录层级与上一次不同

上一次（`20260909-fp-is-00-07-v2-recertification`）是
`<日期>/protocol-v1.0.0/<片>/<运行 id>/`；本目录是
`<日期>/<片>/protocol-v1.0.0/<片>/<运行 id>/`，多一层、片名出现两次。原因是两片分别给了
`-EvidenceRoot`，而 `run-w2g-g2.ps1` 会在 `-EvidenceRoot` 之下自己再拼 `protocol-v1.0.0/<片>/<运行 id>`。

**没有为版式重跑。**证据目录只增不改，为一个纯粹的层级差异再跑两次门禁，换来的是两份内容相同的
运行记录和一份更难读的历史。下一次给一个共同的 `-EvidenceRoot` 即可回到上一次的形状。

## 被纠正的那一份

`FP-IS-14/protocol-v1.0.0/FP-IS-14/20260910T041719210Z-d9ac1a18837a/` 是 **`FAIL`**，保留在原处未改。

它失败的**只有**上面那条前置守卫：那一份自己的 `selectedTestCount` 是 2、`testExitCode` 是 0，
两条车载端测试当时就是通过的。纠正的那一份是同目录下的
`20260910T042025069Z-d9ac1a18837a/`，除 `-SkipProtocolG1` 外命令一字未改。

红的证据不被绿的重跑覆盖：`run-w2g-g2.ps1` 每次写一个新的时间戳目录，两份并存。

## 未在本轮证明的

- **`G1`、`G3` 均未运行。**
- 两端指纹算法一致这件事，本仓这一侧只能钉一个固定值
  （`SlotConfigurationActivationTests.TheFingerprintMatchesTheValueTheControlServerComputes`），
  控制服务端那一侧钉着同一个字面量。两个仓互相看不见，真正的两端一致要等 `G3`。
- `protocol-v1.0.0` 这个 tag **尚未打**（`summary.json` 的 `protocol.tagExists: false`）。
  这两份 G2 绑的是 commit，不是 tag。
