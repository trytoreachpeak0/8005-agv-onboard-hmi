# `ONBOARD_HMI_G2`：`FP-IS-00`～`07` 在协议 v2 下重证（`360a405`）

票 17 的三分之一。**这是 v2 下的重新证明，不是沿用任何既有结论。**
`OnboardHmi_MVP` 那条线钉在已发布的 `protocol-v0.3.0`（`ProtocolVersion 3`／
`WIRE_TO_GATE_MVP`），与本线不是同一条协议；本线此前也从未产出过按切片分列的
`ONBOARD_HMI_G2`。本目录不引用、不继承、不折算任何更早的结论。

## 运行类型

纯本机 G2。**不动车、不建单、不使用任何现场凭据。**
2026-09-09 在 `LAB-WIN-01` 上运行，`.NET SDK 8.0.425`（`global.json` 钉死，`rollForward: disable`）。
八次运行时工作树都是干净的（每份 `summary.json` 的 `hmi.workingTreeStatus` 均为空数组）。

## 结论

| 项 | 结果 |
| --- | --- |
| Release 构建 | 0 warning / 0 error（八次各一遍，全仓，不按切片收窄） |
| `dotnet format --verify-no-changes` | 干净（八次各一遍） |
| 八片 `ONBOARD_HMI_G2` | **八份 `gate-result.json` 全部 `PASS`** |
| 绑定 | `w2g/fp-v2-impl` ＝ `360a4059c9daa44a90f6b504cb25366d264b532a` ＋ `protocol-v1.0.0@f6ee75defe6e2d18f63f4082bee445dbb678ab1b` |

八份逐片，`selectedTestCount` 与 `recordedTestCount` 逐片相等：

| 片 | 运行目录 | selected | recorded | build/test/format |
| --- | --- | --- | --- | --- |
| `FP-IS-00` | `20260909T060819673Z-360a4059c9da` | 11 | 11 | 0/0/0 |
| `FP-IS-01` | `20260909T060900359Z-360a4059c9da` | 5 | 5 | 0/0/0 |
| `FP-IS-02` | `20260909T060939288Z-360a4059c9da` | 5 | 5 | 0/0/0 |
| `FP-IS-03` | `20260909T061018991Z-360a4059c9da` | 9 | 9 | 0/0/0 |
| `FP-IS-04` | `20260909T061103739Z-360a4059c9da` | 2 | 2 | 0/0/0 |
| `FP-IS-05` | `20260909T061142015Z-360a4059c9da` | 5 | 5 | 0/0/0 |
| `FP-IS-06` | `20260909T061214425Z-360a4059c9da` | 8 | 8 | 0/0/0 |
| `FP-IS-07` | `20260909T061246892Z-360a4059c9da` | 19 | 19 | 0/0/0 |

`selected` 是 `--filter "IntegrationSlice=FP-IS-NN"` 在测试列举里选中的条数，`recorded` 是
落盘 `.trx` 里 `<UnitTestResult` 的条数。**两者不等时脚本判 FAIL**——证据不允许声称覆盖了它没
记下的测试。

哪些测试属于哪一片，由 `[Trait("IntegrationSlice", ...)]` 决定，而那是每个测试自己的
`[Trait("ProtocolVector", ...)]` 按协议切片索引的投影，`IntegrationSliceTraitArchitectureTests`
钉住这条等式（票 22）。

## 身份绑定，八份逐字段一致

`schemaVersion` `1.1.0`；`gate` `ONBOARD_HMI_G2`；`implementationBranch` `w2g/fp-v2-impl`；
`protocolTag` `protocol-v1.0.0`；`protocolRepositoryCommit`
`f6ee75defe6e2d18f63f4082bee445dbb678ab1b`；`protocolManifestSha256`
`84f984eabf17106e92666c415b63100d404e9ec69a9a710dfddf17683cc42788`；`integrationSliceIndexSha256`
`71e0a63d49d1973653e1f70addc19c334faff5e53e8597733c1423a7307bd82f`。

**`integrationSliceIndexSha256` 与控制端同一轮 `CONTROL_SERVER_G2` 的那份逐字节相同**——两端读
的是同一张切片表。

`tagExists` 为 `false`，`candidateIsAncestorOfHead` 为 `true`：`protocol-v1.0.0` 至今没打，绑的
是候选 commit 本身，且协议仓 HEAD 确实是它的后继。`approvalStatus` 是 `SUPERSEDING_CANDIDATE`，
不是 `APPROVED_RELEASE`——本目录不构成、也不支持任何发布主张。

## ⚠️ 两件本轮没做到的事，写在这里而不是藏起来

1. **协议 G1 没有运行**（八份 `summary.json` 的 `protocol.g1Status` 均为 `SKIPPED`）。
   本机跑不了：协议仓没有 `node_modules`，`tools/g1-validate.mjs` 起手就缺 `ajv`；
   而 `pnpm` 不在 PATH 上，codex runtime 里那份 `pnpm.cjs` 自己也会去调 `pnpm install` 而失败。
   06:05 那次带 G1 的尝试因此中止，**没有产出任何文件**（运行 id
   `20260909T060552149Z-360a4059c9da`，目录为空，随后删除；此处如实记下）。

   **G1 不是票 17 要的三道门禁之一**，而八份证据里协议身份的九个哈希字段仍由本脚本逐项比对
   过。要补 G1，需要先让这台机器能装协议仓的依赖。

2. **`G3` 本轮没有运行。** `run-staged-g3.ps1` 从 GitHub 克隆车载端并断言远端 tip 等于绑定
   commit，而本线的提交按用户 2026-09-09 的裁定尚未推送（远端停在 `9ec5b29`，本地
   `360a405`）。**八个切片因此尚未通过**——一片要四道门禁齐全才算过。

## 复现

```powershell
foreach ($n in '00','01','02','03','04','05','06','07') {
    .\scripts\run-w2g-g2.ps1 -Slice "FP-IS-$n" -SkipProtocolG1 -EvidenceRoot "evidence\g2\<新目录>"
}
```

去掉 `-SkipProtocolG1` 即会尝试跑协议 G1；在能装依赖的机器上应当这么跑。
