# hmi#208 证据：握手没带上的安全变化以旧内容、更大的号写上服务端

车载端基线 `w2g/fp-v2-impl@b789c3d4`（含 hmi#204、hmi#206）。服务端只读 `fp/v2-impl@a407ec61`。

提交顺序：`6a249f5` 测试 → `02d8dae` 第一版修复 → `d596950` 第一轮证据 → 审查 → `db94c01` 审查后测试 → `53321d5` 第二版修复 → `9f574ee` 用例竞态改正 → 本轮证据。

| 文件 | 内容 |
| --- | --- |
| `00-step1-conclusion.md` | 第一步三问的结论，与报给调度的一致 |
| `01-red-on-test-commit.txt` | 测试提交 `6a249f5` 上修复前红：两格都红在主断言（服务端存下了出发安全） |
| `02-reverse-verification.txt` | 第一版修复 `02d8dae` 上的反向验证，四个变异各 3 次，脚本 `hmi208-mutations.sh` |
| `03-probe-final-state-before-fix.txt` | 探针（审查后重做）：修复前的产品代码、两条主断言**反过来**，三格都绿，并打印每格服务端写下的序列；脚本 `hmi208-probe-before-fix.sh` |
| `04-red-on-review-test-commit.txt` | 审查后测试提交 `db94c01` 上、第一版修复之下的红：代号撞号那一格红在主断言；标记写盘失败那条红在日志措辞 |
| `05-reverse-verification-after-review-first-run.txt` | 第二版修复 `53321d5` 上第一次跑六个变异；M6 第 2 轮多红的一条暴露了用例自己的竞态，由 `9f574ee` 改正 |
| `06-stability-20-runs.txt` | `9f574ee` 上本票用例连跑 20 次，退出码 20 次为 0 |
| `07-reverse-verification-final-head.txt` | 最终头部 `9f574ee` 上的反向验证，六个变异各 3 次，脚本 `hmi208-mutations-2.sh` |
| `08-real-rig-red-36257155944-diagnosis.md` | 新头部第一次 CI 真装置红（run 36257155944）的机理：接收循环在同步写盘时被卡住、心跳超时断线；读到与推的分开；不指向本 PR 代码的依据；断线后装货结果会怎样 |
| `09-real-rig-rerun-idle.txt` | vm01 空闲时同 head 重跑（run 36258605363）PASS 56s 的四行核对与对照数；只算空闲条件下的对照 |

## 修复前是什么样

三格序列相同（`01`、`03`、`04`）：

```
SafetyStateSnapshot v3 departureSafe=False | SafetyStateChanged v4 departureSafe=True | SafetyStateChanged v5 departureSafe=False; SessionReadiness sent on it: RECOVERY_REQUIRED, READY, RECOVERY_REQUIRED
```

握手快照 v3 写的是此刻的不安全；就绪后，断线前记下的 v4（安全）按号更大盖了上去，假服务端按真服务端的就绪规则据此宣布了一次 READY；随后业务服务发现读数与刚发的不同，补发 v5 改回不安全。

缺陷是**窗口**，不是最终状态：`03` 在修复前的代码上把主断言反过来仍然全绿，说明「最终状态是此刻读数、一个号一份内容、发件箱不留旧行」修复前都已成立。主断言因此断的是新一代里服务端**每一次**写下的安全状态。

## 反向验证（`07`，最终头部）

| 状态 | 结果（3 轮一致） | 红在哪 |
| --- | --- | --- |
| 修复原样 | 全绿（10 条：本票 4 条 + hmi#204 的 6 条） | — |
| M1 放弃分支永不进入 | 主用例三格 + 标记失败那条 | 主用例第 202 行（服务端存下出发安全）；标记失败那条第 358 行（第二代存下出发安全） |
| M2 不跳号 | 只 `LandedAfterTheOutboxRead` | 第 241 行，发件箱 v4 有两份内容 |
| M3 不标记旧行 | `LandedAfterTheOutboxRead` + 标记失败那条 | 第 247 行，发件箱留着未确认的旧行；标记失败那条等不到写盘失败（放弃分支没去标记） |
| M4 不比内容，一律放弃 | hmi#204 的三条同 messageId 重发用例 | 等重发超时 |
| M5 第一版的代号条件加回去 | 只 `NeverOnFileUnderARepeatedGenerationNumber` | 第 202 行，报错与修复前一字不差 |
| M6 放弃后不清签名 | 只 `NeverOnFileUnderARepeatedGenerationNumber` | 第 170 行，等不到快照之后那份此刻读数 |

M2、M3 下 `NeverOnFile` 两格仍绿，是对的：它们的旧变化从没写进发件箱，没有旧行可留，也没有旧行与新内容共用一个号。M2 为什么要断在发件箱而不是服务端：被放弃的 v4 从没送到服务端，服务端那一层按构造看不见。

每个状态之后产品文件按 SHA-256 核对还原（`restored: ... OK`），每一轮都记了退出码。
