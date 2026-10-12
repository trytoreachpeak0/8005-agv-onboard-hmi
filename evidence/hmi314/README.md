# hmi#314 证据：屏幕状态快照 T1b

本目录是 hmi#314（屏幕状态快照 T1b：横幅与入口背后的输入）的证据。重点有两部分：
- 验收标准最后一条「发出的消息内容与时序不变」的对照；
- 研究文档第 4.1 节改动所依据的实测。

每条结论都标明「读到的」或「推的」：前者是从代码或运行输出直接读出的，后者是推断、没有实测。

## 1. 结论先说

- 读到的：在同一种跑法下逐字节比较，本票改动之后假对端收到的安全类消息，绝大多数用例与基线相同；不同的都只是多一条或少一条消息，没有一处是同一位置、同样条数、内容却不同。
- 读到的：**同一个 G2 用例和多少条、哪些用例一起跑，会改变它的时序变体分布**（第 7 节）。这是这张票最重要的方法教训：不同跑法录的基线不能混成一个「出现过」的集合。前面几轮我混用了跑法，把一个常见变体误判成了「基线从没出现过」，还据此推了两次机理，两次都已撤回（第 8 节）。
- 读到的：组合跑法里所有「候选独有」的序列，都已在同一种跑法（单条交错）下验证过。候选的每一种序列都在基线里出现过；最后三个用例里，两边每种序列的次数完全相同（第 7、9 节）。按事先定的判法，结案为与本票无关。
- 读到的：研究文档原来的「只升不降」规则在三种情形下会登记错摘要，三种都已用 G2 用例跑出来（第 10 节）。其中「就绪消息先于确认登记」原先是推的，实测是常态：40 轮里 40 轮。

## 2. 提交

| 角色 | 提交 | 说明 |
| --- | --- | --- |
| 基线 | `af19f3e06` | 集成分支（hmi#315 合入）。detached worktree，只在测试侧加了探针钩子；每次运行前后 `git diff -- src` 都为空 |
| 第一版候选 | `d7cd011` | 底座同为 `af19f3e06`；`git diff 113776c d7cd011 -- src` 为空 |
| 修好的候选 | `41a6d31` | 在 `d7cd011` 上 cherry-pick 最终分支的 `41295b0` 得到（不推送）；`git diff 41a6d31 41295b0 -- src` 为空，产品代码就是最终要合入的那一份 |
| 最终分支 | `41295b0` 起 | 含 merge hmi#323（`713f45a`） |

为什么候选要另起：最终分支已经 merge 了 hmi#323，而 #323 改了对照集合里几个用例的测试代码和夹具，拿它和 `af19f3e06` 比就不是同一批测试。所以对照一律在 merge 之前的底座上做；merge 之后只跑定向测试和整个 `SQCD.Agv.UnitTests`。

## 3. 方法

- **探针钩子**：`tests/SQCD.Agv.WireToGateG2Tests/FakeControlServer.cs` 的 `WriteSafetyMessageProbe`。设了环境变量 `HMI314_PROBE_DIR` 时，假对端把收到的 `SafetyStateSnapshot`、`SafetyStateChanged`、`PreDepartureSafetyCheckResult` 原文按顺序写进文件，每个测试每台假对端一个文件；没设时什么都不做。
- **规范化**：只抹六个键，其余逐字节比较，含顺序和条数。
  - 四个由真实时钟决定：`sentAt`、`payload.observedAt`、`payload.validUntil`，以及 `messageId`（含 `observedAt` 的去重键的 StableUuid）。
  - 两个是测试自己随机生成的：`correlationId`、`payload.preDepartureSafetyCheckId`。
  - 读到的：在 `b2f602e` 上两遍会话层运行之间，原文不同的键只有前四个。
- **对比脚本**：`scripts/compare-g2-safety-messages.ps1`。默认只比较所有基线遍都一致的用例；`-AllowBaselineVariants` 要求候选的每段序列逐字节等于基线出现过的某一段。
- **工具自检**，读到的：
  - 同一份数据，pwsh 脚本和独立写的 Python 实现结论相同（`b2f602e` 两遍：128 个稳定、9 个不稳定，名单一致；变体比对：9 处，逐条一致）。
  - 篡改一条原因码后，脚本报 `RESULT: 1 differences`、退出码 1。

## 4. 运行、作废与负载记录

汇总行原文见 `runs.txt`（全量与组合跑法）和 `exp-runs.txt`（区分实验）。每段都核对退出码和用例数：
- 会话层 85 条（基线 76 条）；业务层 137 条（基线 136 条，多了本票新加的用例）；
- 17 用例组 19 条：两个 theory 各展开两组参数，`ARefusedCancellationEnded…`（`doorLeftOpen`）和 `AnUnknownHandoff…`（`ackAfterHandoffEnded`）；
- 补跑组 11 条：用 `--list-tests` 实列核对过。

作废或不计的运行，以及原因：

| 运行 | 原因 |
| --- | --- |
| 候选 `113776c` 第 2 遍（两次） | 一次红在 `QueuedSlotCommandDisplay.cs:160`，与 hmi#323 提交 `e4c2835` 写明的根因相同；另一次是**本票新断言的时序缺陷**，修法见第 11 节 |
| 候选 `113776c` 第 1、3 遍 | 不计入结论。候选一侧改为全部出自同一提交 |
| 基线 17 用例组第 9 遍 | 红在 `QueuedSlotCommandDisplay.cs:160`（#323 那类），这一遍跑在没有本票改动的 `af19f3e06` 上 |
| 候选 `41a6d31` 17 用例组第 2、4 遍 | 同上，#323 那类（这个候选建在 #323 之前，预期内）；已补跑两遍 |
| 候选 `41a6d31` 17 用例组第 6 遍里的一条 | `AnUnknownHandoff…(ackAfterHandoffEnded: False)` 这一条的探针文件标为「中断不计」，其余照用（调度裁定）。读到的：期望 `HANDED_OFF`、实际 `UNKNOWN`（`HandoffOverUnrecordedLoad.cs:461`）；夹具给操作员 2 秒真实时限，测试线程醒得晚就会超时，与 #323 修掉的另两条同形状。这一条在候选侧因此只有 9 遍数据；已另开 hmi#331 |
| 第一轮压力连跑 13 轮 | 编译报 `MSB3021`（DLL 被刚中止的测试占着），命令把编译失败吞掉，13 轮跑的都是旧二进制 |
| 一次中止的候选第 2 遍 | 按调度要求扩大比较集合时中止，不是测试失败 |

本机负载，按调度告知与我的核对：
- 19:10 至 19:40，hmi#316 的单脚本自检，与基线补跑组重叠。
- 20:04 至 20:10:46，hmi#316 的 pwsh 自检，与 `41a6d31` 的编译和 17 用例组第 1 遍重叠。
- 更早一段，cs#645 误跑了一次服务端整套测试，约 8 分半。
- `41a6d31` 开跑前，我等到本机连续 60 秒既没有 testhost 也没有 `dotnet test` 进程（20:09:46）才开始。此前发现过 cs#646、cs#660 审查的测试进程，都没有动，由调度让它们暂停。
- 区分实验时 cs#531 与几个审查同时在跑，三组交错，负载在三组之间均摊。

## 5. 组合跑法下的对照（已被第 7 节限定，不单独作为结论）

- 严格比对：基线全量 3 遍有 196 个稳定用例（511 条消息）。候选 `d7cd011` 全量 3 遍与之相比有 5 处差异（`compare-stable.txt`）。
- 变体比对：17 个用例（19 条），基线、候选各 10 遍（`compare-variants.txt`）。
- 补跑组：11 条，基线 10 遍（`supp-compare.txt`、`supp-shapes.txt`）。
- `41a6d31`：17 用例组 10 遍，加补跑组 10 遍（`cand2-compare.txt`）。

按请求快照这条发送路，读到的：集成分支上只有三个用例真正发出了按请求快照（判据：同一连接里出现第二份 `SafetyStateSnapshot`），分别是 `ARefusedSafetyChangeIsGivenUp…`、`ARefusedSafetyChangeWithNothingAfterIt…`、`ASafetyChangeRefusedInTheHandshakeReplay…`。三个都不稳定，原因是快照之前多一条或少一条 `SafetyStateChanged`。快照本身的 `departureSafe`、原因码、`slotStates` 在各遍之间相同。在基线 17 用例组 10 遍里，它们分别有 2、3、2 种变体。

## 6. 差异的形状

读到的，所有差异都属于下面两类：
- **A 类**：末尾一条与前一条同内容、只升版本号的 `SafetyStateChanged` 有或没有；或在某个时刻之前多发一条变化。
- **B 类**：断线或重启前最后一条变化的确认有没有回来，对应握手 `WireToGateSessionClient.cs:906-956` 的两个分支：
  - 确认回来了：没有要补发的，快照沿用已接受的版本号 vN（`:954-956`）。
  - 确认没回来：先补发 vN，再发快照 vN+1（`:919-922`）。
  - 两个分支都在本票没改过的代码里。

推的：A 类对应「一次 IO 变化引出两次安全上报请求」的机理（工作区记忆 `hmi-safety-pass-double-request`）。

## 7. 跑法决定变体分布（区分实验，`exp-count.txt`）

只跑 `EveryLoadedDemandCanStillBeHandedOffAfterAReconnectAndARestart` 一条，三组交错 30 轮（基线 `af19f3e06`、候选 `41a6d31`、变体），20:41:21 至 22:25:07。90 遍全部退出码 0、每遍 1 条。读到的，「第 2 条连接第一条就是快照 v3」这种序列的出现次数：

| 组 | 次数 |
| --- | --- |
| 基线 `af19f3e06` | 10 / 30 |
| 候选 `41a6d31` | 3 / 30 |
| 变体（视图模型不记录） | 5 / 30 |

三组一共见到 4 种序列，每一种在三组里都出现过。而在组合跑法（整类 136 条、19 条、11 条一起跑）录的 23 遍基线里，这种序列一次都没出现。

结论：同一个用例的变体分布取决于它和多少别的测试一起跑。所以「候选序列是否在基线里出现过」只能在同一种跑法内部比较，不能把不同跑法的基线合成一个集合。

变体相对 `41a6d31` 的 `src` 差异见 `variant-src.diff`：`MainViewModel.cs` 恢复为 `af19f3e06` 原样；`Offer` 只返回参数、不做记录；文件头的 `#pragma` 只在变体里，用来关掉由此产生的两条警告。变体只放在 scratch 里，没有提交。

## 8. 撤回的推测

两次推测都建立在「基线 0/23、候选 6/33」这个被第 7 节推翻的前提上，已撤回：
1. 「`:778` 确认后回读存档行的解析开销改变了竞态先后」。按这个推测做了修改（第 12 节）：同口径下候选独有的运行只从 4 次降到 3 次，机理不成立。这次修改本身让发送路回到了改动前的原样，保留。
2. 「G2 里视图模型的同步刷新拖慢了会话线程」。区分实验里候选反而比基线少，这个实验也区分不了它，按调度规则不再追查。

## 9. 剩下三个用例：单条交错验证（`exp-a-count.txt`、`exp-a-runs.txt`）

调度裁定对 `41a6d31` 在组合跑法下仍有候选独有序列的三个 A 类用例，用单条交错法验证。
- 跑法：基线 `af19f3e060b643d063f1a8a55f946237bf58f449` 与候选 `41a6d31c3c9fabb6f3a8490b9f1d75fcbec09035` 两组，SDK 都是 8.0.425，同一脚本、同一过滤（每段只跑一个用例方法）。15 轮，三个用例轮流，两组交错，共 90 段。
- 时间：23:44:04 起跑（本机连续 60 秒无 testhost、无 `dotnet test` 进程之后）到 00:41:11。90 段全部退出码 0，条数全对：`ARefusedCancellationEnded…` 是 theory，每段 2 条，另两个每段 1 条。
- 作废轮：0。第 1 轮（23:44:04 至约 23:50）落在 cs#664 的变异与定向测试窗口里（调度告知，约 23:35 至 23:50），我的进程检查没看到它，所以在此注明受了干扰。第 1 轮的 6 段都是各自用例的多数序列，看不出异常，保留；去掉第 1 轮，结论不变。

计数（读到的，规范化六键后逐字节判定）：

| 用例 | 序列（简写） | 基线 | 候选 |
| --- | --- | --- | --- |
| `ARefusedSafetyChangeIsGivenUp…` | `c1 Snapshot v1 → Changed v2 → v3 U → v4 U → v5 S → Snapshot v6` | 15 | 15 |
| `ARefusedSafetyChangeWithNothingAfterIt…` | `… Changed v4 U → Snapshot v5 U` | 14 | 14 |
| 同上 | `… Changed v4 U → Changed v5 U → Snapshot v6 U` | 1（第 10 轮） | 1（第 11 轮） |
| `ARefusedCancellationEnded…(doorLeftOpen: False)` | `c1 Snapshot v1 → Changed v2 → c2 Snapshot v2 → Changed v3` | 15 | 15 |
| `ARefusedCancellationEnded…(doorLeftOpen: True)` | `c1 Snapshot v1 → Changed v2 → c2 Changed v3 U → Snapshot v4 U → Changed v5 U` | 15 | 15 |

候选独有序列：0。按事先定的判法，结案为与本票无关。

## 10. 「只升不降」规则的三种失效情形：G2 用例

用例在 `tests/SQCD.Agv.WireToGateG2Tests/WireToGateG2Tests.AcceptedSafety.cs`。

| 情形 | 用例 | 跑出来的事实 |
| --- | --- | --- |
| 新一代握手同号换内容（`:946-955`） | `ANewGenerationsHandshakeAtTheSameVersionReplacesTheOldGenerationsSummary` | 同号、代号变大、摘要不同（用例先断言这三个前提） |
| 初值没有摘要（`:129`、`:969`） | `AcceptedSafetyAfterTheHandshakeIsTheSnapshotTheServerAcknowledged` | 握手快照版本号等于配置值 1，来源从 `Configured` 变成 `HandshakeSnapshot` |
| 就绪消息先于确认登记（`:778` 对 `:4957`） | `TheNaturalOrderOfAcknowledgementAndReadinessAlwaysEndsWithTheChangesSummary`、`AcceptedSafetyAfterARequestedSnapshotIsTheSnapshotTheServerAcknowledged` | 按真服务端的顺序（先确认、紧跟就绪），前者 5 次运行里每次都是 40 轮中 40 轮就绪消息先登记；后者 5 次全是 |

## 11. 新断言的时序缺陷与修法

- 读到的：原断言写死「锁存时取消装货的业务层为真」。业务层放不放出这个入口，取决于取快照时失败结果有没有已经报出，机器负载重就会红。
- 修法：
  - 原用例改为对十个入口断言一个关系：最终值等于业务值经过锁存闸门，取消装货再加上「扫码之前」那一半。
  - 新增确定时刻的用例 `ALatchOverAnOpenDoorClosesTheCancellationTheBusinessStillOffers`。
- 读到的：在 `d7cd011` 上把这两个用例压力连跑 20 轮，每轮 2 条，0 红。

## 12. `:778` 改为用参数登记（调度批准的偏离）

研究文档原要求 `:778` 在「此前已送达」分支登记存档那一行。实现改为登记本次调用的参数，理由与依据写在研究文档第 4.1 节：
- 两者恒等：同一去重键下载荷不同会被拒（`BUSINESS_ID_CONTENT_CONFLICT`），由 `AResendOfAnAcknowledgedChangeRegistersWhatTheServerTookAndADifferentSummaryUnderItsKeyIsRefused` 钉住。
- 发送路回到原样：`SendDurableCoreAsync` 恢复，`git diff af19f3e06 HEAD -- WireToGateSessionClient.cs` 里与 `SendDurable`、`return stored`、`WireLine` 相关的改动行数为 0。
- 登记留在原位，不挪到发布之后：会话里的判断只读 `_acceptedSafetyStateVersion`，挪到后面不会改变会话行为；但挪到后面会出现一个窗口，窗口里构造的快照会读到旧记录，而登记本身不置脏标记，要等心跳才更新。

## 13. 变异取红

| 变异 | 结果（读到的） |
| --- | --- |
| M1 就绪消息不按版本号查摘要 | 红 1 条：`AReadinessThatOvertakesTheChangesOwnRegistrationFindsTheChangesSummary` |
| M2 严格只升不降 | 红 2 条：`ANewGenerationsHandshake…`、`AcceptedSafetyAfterTheHandshake…` |
| M3 去掉新一代握手同号替换 | 红 1 条：`ANewGenerationsHandshake…` |
| M4 不讲规则直接覆盖 | G2 红 4 条；单元测试红 7 条（含并发用例） |
| M5 锁存前不记取消装货业务值（在新用例上重做） | 新确定用例与改过的原锁存用例都红 |
| M6 监视器不订阅会话 | 红 `SwitchedOnWithASessionItWritesTheAcceptedSafetyAndASessionChangeAloneIsWritten` |
| M7 构造器忽略已接受安全摘要 | 红锁存用例与接线用例 |
| M8 `:778` 登记成未知（新代码） | 红 2 条 |
| M9 迟到确认登记成未知（新代码） | 红 1 条：`ALateAcknowledgement…` |

## 14. 更正记录

- 我曾报告「按请求快照在一致的用例集合里」，这是错的：当时只核了用例调用了 `RequestSafetyStateSnapshotAsync`，没核消息层面真的发出了第二份快照。
- 排查时我把输出截到前 80 或 190 个字符，theory 的参数后缀被截掉，一度看错文件。
- 我曾在 PR 正文里写「不涉及恢复入口」。实际上十个恢复入口的写入语句都改了写法，见 PR 正文「真装置」一节。
- 「基线 0/23、候选 6/33，不像偶然」以及据此做的两次推测，已撤回（第 7、8 节）。

## 15. 原始数据位置

探针原始输出和每段日志留在本机 `F:/8005-scratch/hmi314-baseline/`，不入库：`af19f3e*/`、`d7cd011*/`、`41a6d31*/`、`exp/`，以及 `*-void` 作废数据。本目录只放汇总与比对输出。
