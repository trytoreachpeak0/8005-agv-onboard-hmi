## 第一步结论：甲——同一个 `safetyStateVersion` 不能对应两份不同的安全状态，编号由车载端负责；不改协议

实现者认领本票后的第一步读协议与读库结果。行号：车载端 `w2g/fp-v2-impl@1184bb07`，服务端 `fp/v2-impl@384b9b69`（含 cs#331、cs#340），协议 `protocol-v2.0.0`，需求仓 `8005-agv-program@de33a902`。

### 协议本意读到的原文

| 出处 | 原文（节选） | 读到/推的 |
| --- | --- | --- |
| 协议 `schemas/messages/SafetyStateChanged.schema.json`、`SafetyStateSnapshot.schema.json` | `safetyStateVersion` 都只引 `common/types.schema.json#/$defs/Revision`（`integer`，`minimum: 0`），没有说明文字。两者 payload 结构不同：变化是 `safetyStateVersion`、`observedAt`、`safety`、`affectedSlots`；快照是 `safetyStateVersion`、`observedAt`、`safety`、`slotStates`（8 个仓的完整状态） | 读到 |
| 协议 `vectors/CV-SNAPSHOT-SAME-REVISION-CONFLICT` | 快照、确认、快照、`ProtocolProblem`；`controlServer: REJECT_SAME_REVISION_DIFFERENT_CONTENT`、`onboardHmi: NEVER_APPLY_CONFLICTING_SAME_REVISION`，`stableErrorCode: SNAPSHOT_REVISION_CONTENT_CONFLICT`。属于 `FP-IS-00` | 读到 |
| 需求仓 `CONTEXT.md:380-381` | `SafetyStateSnapshot`：「车载端为恢复或版本缺口对账提供的当前完整抽象安全状态及 safetyStateVersion」 | 读到 |
| 需求仓 `CONTEXT.md:384-385` | `SafetyStateUnknown`：「服务端发现 safetyStateVersion 未知、回退或不连续后进入的保护状态」 | 读到 |
| 需求仓 `.scratch/8005-full-product/issues/06-answer.md:369-370`（v2 协议设计答复） | 「`SNAPSHOT_REVISION_CONTENT_CONFLICT` 这个码存在的意义就是『同一 revision 必须同一内容』」 | 读到 |
| 服务端 `OnboardMessageProcessor.cs:343-347`（`SafetyStateSnapshot` 分支注释） | 会话中途的快照是车在「next safetyStateVersion」上作答，「a safety change like SafetyStateChanged」，走同一条路，版本规则不放宽 | 读到。服务端本来就把变化与快照当作**同一条版本序列** |
| 车载端 `WireToGateSessionClient.cs:1505-1508`（会话中途快照 `PublishSafetyStateSnapshotAsync` 的注释） | 「版本号由调用方给，必须比已被接受的大。快照内容每次都不同（observedAt、读数），同一个版本号换内容就是冲突，服务端会拒。调用方与 `SafetyStateChanged` 共用同一个版本序列」 | 读到。车载端自己也是这么约定的，只有握手快照 `:757` 例外：它直接取「已接受版本」 |

### 判断

1. **版本号是安全状态的编号，编号的生产者是车载端。**协议要「同一 revision 必须同一内容」；两端代码都把变化通知与快照当成同一条序列。所以「同一个号不能对应两份不同的安全状态」由发号的一方保证，也就是车载端。（推的，依据是上表）
2. **车载端今天确实给两份不同的状态发了同一个号**，这不是服务端比较方式造成的假象。bisect-cs323 库（`C:\w2g\bisect-cs323\hmi-7cf1dcba\stage-root\controlserver.db` 与 wal，拷到临时目录只读查询）的 `ProtocolInbox`：（读到的）
   - 第 2 代：`SessionHello` → 补发的 `SafetyStateChanged` v5，`departureSafe=false`、`unknownPresent=true`，`observedAt 16:20:38.966`（生成于第 1 代）→ `CapabilitySnapshot`，之后再没有这一代的报文。
   - 第 3 代：`SessionHello` → `CapabilitySnapshot` → `SafetyStateSnapshot` **v5，`departureSafe=true`**、`unknownPresent=false` → 告警快照 → 恢复报告 → `SafetyStateChanged` v6（`departureSafe=true`）。
   - 所以 v5 在这次运行里对应了两份不同的安全摘要。跨代没被发现，只因为服务端每代清空（`WireToGateStore.cs:157-158`）。
3. **丙（服务端改成比安全内容）解决不了本票，也不该在 v2 里做。**（推的）
   - 就算服务端不比整行、改比 payload，变化和快照的 payload 结构本来就不同（`affectedSlots` 对 `slotStates`），`observedAt` 也必然不同（一个是变化发生时，一个是握手时）。要让「变化 vN」与「快照 vN」被判为同内容，就得新定义一个跨消息类型的等价关系（例如只比 `safety`）。协议里没有这条定义，加它就是改协议语义，属于 v3 批。
   - 上面第 2 条那种「号相同、摘要真的不同」，服务端按协议**应当**拒。丙修不掉它，只有车载端换号能修掉。
   - 丙指出的那一半是真的：服务端比整行哈希，比协议要求的「内容」更严。但在车载端每份快照都换新号之后，同一代里不会再出现同号的两条安全报文，这个更严的比较就碰不到了（推的）。它不是本票的触发原因，我不在本票改，记为剩余风险。
4. **不需要改协议。**甲只改车载端怎么取号，版本仍然单调递增、连续（补发 vN 之后快照 vN+1），服务端受理规则不变，`CV-SNAPSHOT-SAME-REVISION-CONFLICT` 不受影响。

### 甲具体怎么做

- **只在握手里真的补发过 `SafetyStateChanged` 时，握手快照取「已接受版本 + 1」；没有补发时照旧取已接受版本。**快照被确认后把已接受版本推进到它，服务端在就绪里回报的也是它（`OnboardMessageProcessor.cs:749` 回的是 `state.SafetyRevision`），车载端 `ApplySessionReadiness` 的「必须恰好相等」检查照常成立。
- 为什么不改成「握手快照一律 +1」：那样也能顺带消除跨代复用同号（上面第 2 条的形状），但会改变每一次握手的编号。现有 G2 里有直接调 `SendSafetyStateChangedAsync` 并写死版本号的用例，它们默认握手快照等于配置的版本；一律 +1 会让这些用例在真服务端语义下自相冲突，改动面超出本票。跨代复用今天没有后果（服务端每代清空、业务服务每换一代都会再发一条更高号的变化），记为剩余风险。（推的）
- 与 hmi#204 同文件（`WireToGateSessionClient.cs`），实现提交等 PR #205 合入后 merge 进来再做；先写 L1。

### 从 bisect-cs323 库核实：冲突会出在同一代之内

- 读到的：第 2 代里补发 v5 之后，这一代只到了 `CapabilitySnapshot`，没有 `SafetyStateSnapshot`。与 cs#340 的解释一致：补发 v5 的确认之后多附的那行就绪被当成能力快照的答复读走，握手在那里就断了。
- 推的：cs#340 修好之后，第 2 代会接着发自己的握手快照，号取刚被推进到的 v5，于是在**第 2 代之内**与刚写入的 v5 同号不同哈希，服务端在 `WireToGateStore.cs:3333` 抛 `conflicting content`。与票面推断一致，没有不符之处。
