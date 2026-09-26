# hmi#208 证据：握手没带上的安全变化以旧内容、更大的号写上服务端

车载端基线 `w2g/fp-v2-impl@b789c3d4`（含 hmi#204、hmi#206）。服务端只读 `fp/v2-impl@a407ec61`。

| 文件 | 内容 |
| --- | --- |
| `00-step1-conclusion.md` | 第一步三问的结论，与报给调度的一致 |
| `01-red-on-test-commit.txt` | 测试提交 `6a249f5` 上修复前红：两格都红在同一条断言（第 166 行，服务端存下了出发安全） |
| `02-reverse-verification.txt` | 修复提交 `02d8dae` 上的反向验证，四个变异各连跑 3 次，脚本 `hmi208-mutations.sh` |
| `03-probe-final-state-before-fix.txt` | 探针：修复前的产品代码、关掉两条主断言，其余断言全绿 |

## 修复前是什么样

两格序列相同（`01`）：

```
SafetyStateSnapshot v3 departureSafe=False | SafetyStateChanged v4 departureSafe=True | SafetyStateChanged v5 departureSafe=False; SessionReadiness sent on it: RECOVERY_REQUIRED, READY, RECOVERY_REQUIRED
```

握手快照 v3 写的是此刻的不安全；就绪后，断线前记下的 v4（安全）按号更大盖了上去，假服务端按真服务端的就绪规则据此宣布了一次 READY；随后业务服务发现读数与刚发的不同，补发 v5 改回不安全。

所以缺陷是**窗口**，不是最终状态：`03` 证明修复前「最终状态是此刻读数、一个号一份内容、发件箱不留旧行」都已成立。新用例的主断言因此断的是新一代里服务端**每一次**写下的安全状态，不是最后一刻。

## 反向验证（`02`）

| 状态 | 结果 | 红在哪 |
| --- | --- | --- |
| 修复原样 | 3/3 全绿（8 条：新 2 条 + hmi#204 的 6 条） | — |
| M1 放弃分支永不进入 | 两格都红，3/3 | 第 166 行，报错与修复前一字不差 |
| M2 不跳号 | 只有 `LandedAfterTheOutboxRead` 红，3/3 | 第 207 行，发件箱里 v4 有两份安全状态 |
| M3 不标记旧行 | 只有 `LandedAfterTheOutboxRead` 红，3/3 | 第 213 行，发件箱留着未确认的 `SafetyStateChanged` |
| M4 不比内容，跨代一律放弃 | hmi#204 的两条同 messageId 重发用例红，3/3 | 等同一条报告重发超时 |

M2、M3 下 `NeverOnFile` 仍绿，是对的：那一格旧变化从没写进发件箱，没有旧行可留，也没有旧行与新内容共用一个号。

M2 为什么要断在发件箱而不是服务端：被放弃的 v4 从没送到服务端，任何一代都没有，所以服务端那一层按构造看不见「号被给了别的内容」。车载端发件箱是它发出过哪些号的记录，同号两份内容的旧行离下一次握手补发只差一步。

每个状态之后产品文件按 SHA-256 核对还原（`restored: ... OK`）。
