# hmi#417 证据：清单机台号 eqp（协议 7.0.0 候选）

提交：`62cbc38`（改钉＋测试数据带 eqp，取红）、`6032fd7`（实现＋向量同名测试）。

## 取红与取绿

| 提交 | 跑了什么 | 结果 |
| --- | --- | --- |
| 只改钉（未提交的中间态） | `SQCD.Agv.UnitTests` 架构测试 | 2 红：向量条目 61→62、`CV-WORKLIST-EQP-DISPLAY-ONLY` 无同名测试（`red-unit-62cbc38.txt`） |
| `62cbc38` | `InboundPayloadSchemaBoundaryTests` | 清单类全红：`The JSON property 'eqp' could not be mapped ... WireToGateWorklistItem`（同上文件） |
| `62cbc38` | G2 `AtAMachinePickupEveryUnloadedRowHasItsOwnCancellation` | 2 红：清单整张被拒，等清单超时（`red-g2-62cbc38.txt`） |
| `6032fd7` | `SQCD.Agv.UnitTests` 全量 | 1340/1340 |
| `6032fd7` | `SQCD.Agv.WireToGateG2Tests` 全量 | 两轮各 1141/1144、1142/1144，见下「全量里的超时」 |
| `6032fd7` | 新类 5 条 | 5/5 |
| `6032fd7` | `dotnet format ./SQCD_8005AGV.sln --verify-no-changes --no-restore` | exit 0（`format-verify.txt`） |

### 全量里的超时

两轮 G2 全量失败的是 5 条不同的老测试（每轮 2～3 条，没有一条重复），都是 5 秒等待超时或时序断言；
每条单独重跑两次都绿：

- 第一轮：`LoadCancellationBeforeSublotG2Tests.TheEntryRequestAndThePendingCancellationGoOnlyOnceTheResultIsAcknowledged`、
  `MultiDemandJourneyG2Tests.ALoneAttemptStillCarriesItsSnapshotAfterGivingTheDisplayUp`、
  `RecoveryVectorG2Tests.TheSameCompensationCommandDeliveredTwicePulsesTheSlotsOnce`
- 第二轮：`StationDeadlineExpiredG2Tests.AResendDuringAnInFlightCancellationLeavesTheOperationProjectionAlone`、
  `LoadCancellationBeforeSublotG2Tests.AnAuthorizationNamingASlotIsRefusedWithoutSlotIo`

集成分支基线上的对照全量见 `g2-base-compare.txt`。

## 变异（`mutations.txt`）

每次只改一个产品文件，跑新类 5 条，跑完 `git checkout` 还原并核对工作区干净。

| 变异 | 抓到它的测试 |
| --- | --- |
| M1 会话客户端不转 eqp | 原样显示、只显示 |
| M2 行里 `Trim()` | 原样显示（带空格的机台号） |
| M3 null 显示成 `-` | null 取货、null 卸货、只显示 |
| M4 扫码可按机台号匹配 | 只显示（扫 B 的机台号 `SUBLOT-Z` 在两轮里裁决不同） |
| M5 没机台号的行不给「取消」 | null 取货、只显示 |
| M6 XAML 不绑定 eqp | XAML 位置测试 |

## 核对项（protocol#29 审查 M1）

### 1. 车载端遇到 `SNAPSHOT_REVISION_CONTENT_CONFLICT` 时有没有不改库的出口

**没有车载端出口。** 路径（`WireToGateSessionClient.cs`）：

- 收到清单快照先过内存修订号表（`ApplyJourneyRevision`），再写日志库（`SqliteWireToGateJournal.SaveAppliedJourneySnapshotAsync`，
  每种快照只存最新一行）。同修订号、内容哈希不同，两处都抛 `SNAPSHOT_REVISION_CONTENT_CONFLICT`。
- `ApplyJourneySnapshotAsync` 回 `ProtocolProblem` 后重抛，接收循环失败，`FinishFailedSession` 把会话置为 Disconnected、
  `SESSION_RECOVERY_REQUIRED`，清空内存投影。
- 重连时 `RestorePersistedJourneyProjectionAsync` 从日志库重新载入那一行旧清单；服务端若再发同号新内容，同样失败。
  日志库在 `%LOCALAPPDATA%\SQCD\8005AGV\onboard-journal.db`，重装不清；仓库里没有删 `WireToGateAppliedJourneySnapshots` 行的界面、
  脚本或代码（`grep` 全仓只有建表、读、写）。
- 所以操作员和维护人员在车上都没有不改库的出口。能解开的只有服务端发**更高修订号**的清单（日志库探针第 2 种情况：旧行被替换），
  或者删车载日志库（改库）。`ManualStationClearanceG2Tests.cs:1232` 的注释早已写明同号异内容会「正当地让新会话失败」。

### 2. 「关闸且所有旅程 Completed 才换装」前提下，车载库里会不会留着非空清单快照

**按前提加「两端发件箱已清空」，不会。** 依据：

- 服务端每趟旅程收尾都发一张空清单（`items: []`，带 `stopEndedReason`），号比此前都大（cs#896 PR 正文；
  `JourneyRuntimeEngine.cs` 收尾处以 `[]` 发清单）。车载端确认后它就是日志库里那一行。
- 空清单在 v6 与 v7 里逐字相同（eqp 只在条目里），所以即使服务端换装后以同号重发，内容哈希也相同，车载端照常确认
  （日志库探针第 3 种情况）。
- 新旅程的清单号一定更大，旧行直接被替换（探针第 4 种情况）。
- 遗留情况：某车在最后一趟收尾时不在线，收尾空清单没被确认，车载库里留着那趟的最后一张非空清单。此时服务端发件箱未清空，
  cs#896 已查明服务端换装后会以 `OUTBOX_PROTOCOL_IDENTITY_MISMATCH` 拒绝启动，不会把同号新内容发给车；
  而且服务端不会重新生成已完成旅程的清单。即便之后发的是新旅程（更大的号），旧行也会被替换。所以这条路径不会触发冲突，
  只是部署前提要写全「各车发件箱已确认」。
- 留着的 v6 非空行在 v7 构建上能正常载入：缺 `eqp` 读作 null，不抛错（探针「restore of the kept row」各行）。

**没有发现前提挡不住的路径。**前提被违反（旅程在途就换装）时，cs#896 已查明服务端自己在 `RefreshOutboundEnvelopeAsync`
抛冲突、不发出那一行，车上不会收到同号新内容。

### 探针（`probe/`，不入库为测试）

- `journal-probe.cs.txt`／`.log`：直接对 `SqliteWireToGateJournal` 存两次清单，四种情况：
  1. v6 非空 → 同号带 eqp：`SNAPSHOT_REVISION_CONTENT_CONFLICT`，旧行保留；
  2. v6 非空 → 更高号带 eqp：接受，替换；
  3. v6 空收尾 → 同号空收尾（v7 服务端重发）：接受，无冲突；
  4. v6 空收尾 → 下一趟第一张带 eqp：接受，替换。
- `session-restart-probe.cs.txt`／`.log`：想用整套 G2 夹具重启会话来看同一件事，但替身在第二次握手时报
  `HANDSHAKE_SEQUENCE_INVALID`（安全状态版本基线，和清单无关：更高修订号那组也时有时无），没能观察到冲突本身，
  只作为未成的尝试留档；结论以日志库探针和代码路径为准。

## 偏离 wire notes 的一处

wire notes 写「缺 `eqp` 字段是坏消息，到不了屏上」。车载端照本线一贯做法不在入站逐字段校验 schema：缺字段读作 null、显示为空。
没有改成拒收，原因是：日志库里留着的 v6 非空行（上面的遗留情况）也缺这个字段，严格拒收会让车在启动恢复投影时就失败，
比显示为空更糟。缺字段的发送方是服务端，由服务端 G2 的出站 schema 校验把关。

## 审查返工（M1、L1～L3，`b9253ca`、`bbbe01c`）

| 项 | 改了什么 | 守它的测试 |
| --- | --- | --- |
| M1 | 新测试：日志库预置 v6 修订号 1 非空行（无 eqp），替身发修订号 2 带 eqp；启动 Ready、无 ProtocolProblem、行显示 `N1-03` | `AVehicleWithAPre700WorklistInItsJournalStartsAndShowsTheNextRevisionsMachineNumber` |
| L1 | 子批列 `*`、机台号列 `Auto`，机台号 `MaxWidth=72`、间距 8 | `WorklistScrollingLayoutTests.ALongSublotIsEllipsizedAndTheMachineNumberBesideItStaysOnScreen` |
| L2 | XAML 测试断言机台号到行模板之间无 Visibility／Opacity | `TheMachineNumberSitsBesideTheSublotInTheWorklistRowAndNowhereElse` |
| L3 | 序列化器摘要去掉过时的 `protocol-v5.0.0` | — |

L1 实测（车载屏客户区 1004×660，左栏清单每行约 284 像素，子批那一格约 212）：

- 现场形状的子批 `Q26095466-13`（约 134 像素）与机台号 `2ZPB97`（约 54）都整串显示；现场 EQP 最长形如 `2ZPB101`，约 63，在 72 以内。
- 60 字符的子批省略号截断、不压到机台号，机台号整串露出。
- 超长机台号在自己那格省略，最多占 72。
- 第一版用列上的 `MaxWidth` 只会裁切、不出省略号；审查建议的「机台号 Auto＋MaxWidth」落在文本自身上才生效。

变异（`rework/mutations.txt`，在 `b9253ca` 上每次改一个文件、跑完还原）：入站把 `eqp` 设为必填（`[property: JsonRequired]`）→ M1 新测试红；
机台号 `Visibility="Collapsed"` → XAML 测试与排版测试红；`Opacity="0"` → XAML 测试红；退回原布局（子批 Auto、机台号 *）→ 排版测试红；
去掉 72 的限宽 → 排版测试红。

推送前本机（`rework/`）：单元 1340/1340；G2 向量 `CV-WORKLIST-EQP-DISPLAY-ONLY`、清单与横幅排版、屏幕状态 XAML 覆盖共 20/20；
`check-ui-layout.ps1` PASS（`ui-layout.json`）；`dotnet format --verify-no-changes` 第一次 exit 2（新测试三处 WHITESPACE），修后 exit 0。
