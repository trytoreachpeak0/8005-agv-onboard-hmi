# ONBOARD_HMI_G2：15 片，protocol-v3.0.0 发布身份（control-server#393 出口第 5 步）

| 项 | 值 |
| --- | --- |
| 车载端提交 | `b9e67a538ba4cdf1916d201a08af40dd28270d14`（`w2g/fp-v2-impl`，onboard-hmi PR #286 合并提交，树同 `w2g/batch-p3/v3@33f26018`） |
| 协议 | `(AGV_FULL_PRODUCT, 4)`，`protocol-v3.0.0` → `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c`，manifest `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e`，`APPROVED_RELEASE` |
| 协议检出 | `C:/w2g/p3`：短路径的普通克隆，检出在注释 tag `protocol-v3.0.0` 上（协议仓的 G1 不能在 git worktree 里跑） |
| 入口 | `pwsh -File scripts/run-w2g-g2.ps1 -Slice <id> -ProtocolRoot C:/w2g/p3 -EvidenceRoot <本目录绝对路径>/<id>`，经 `Invoke-HeavyLocal.ps1 -Ticket cs#393`，本机封锁时段 |
| 时间 | 2026-10-09 23:47 起，逐片依次运行 |

| 切片 | 结论 | 选中／记录的测试 |
| --- | --- | --- |
| `FP-IS-00` | PASS | 40 / 40 |
| `FP-IS-01` | PASS | 5 / 5 |
| `FP-IS-02` | PASS | 126 / 126 |
| `FP-IS-03` | PASS | 90 / 90 |
| `FP-IS-04` | PASS | 7 / 7 |
| `FP-IS-05` | PASS | 30 / 30 |
| `FP-IS-06` | PASS | 62 / 62 |
| `FP-IS-07` | PASS | 410 / 410 |
| `FP-IS-08` | PASS | 2 / 2 |
| `FP-IS-10` | PASS | 1 / 1 |
| `FP-IS-11` | PASS | 1 / 1 |
| `FP-IS-12` | PASS | 6 / 6 |
| `FP-IS-13` | PASS | 18 / 18 |
| `FP-IS-14` | PASS | 9 / 9 |
| `FP-IS-15` | PASS | 5 / 5 |

每片的 `gate-result.json` 都逐份核过 `implementationCommit`、`protocolTag`、`protocolApprovalStatus`、`protocolManifestSha256`，值都与上表一致。每片都在发布态重跑了一次协议 G1，日志在 `logs/protocol-g1.log`。

同一夜有一遍因为传了相对路径而无效的运行，见同级目录 `20261009-protocol-v3.0.0-b9e67a53-relpath-inconclusive/README.md`。
