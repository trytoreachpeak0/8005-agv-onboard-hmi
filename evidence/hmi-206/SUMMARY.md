# hmi#206 证据：握手里补发过安全变化时，同代握手快照取已接受版本的下一版

追踪票 https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/206 。配对的服务端模型 PR：trytoreachpeak0/8005-agv-control-server#347。

| 文件 | 代码状态 | 内容 |
| --- | --- | --- |
| `00-step1-conclusion-as-posted.md` | — | 第一步结论（甲）与依据，与票上评论 issuecomment-5790969954 同文 |
| `01-red-on-test-commit.txt` | 测试提交 `1e921bd8`（基于 `1184bb07`），`--no-incremental` | 新用例两格红：`the server refused: connection 2, generation 2, SafetyStateSnapshot: safety revision 2 has conflicting content.` |
| `02-red-probe-default-double.txt` | 同上，临时把 `ShareSafetyRevisionAcrossChangeAndSnapshot` 关掉 | 默认假服务端下握手照样走完，红由车载端这一侧的断言给：`the handshake snapshot carries revision 2, not above the change it resent at 2` |
| `03-reach-probe-pre-merge.txt`、`04-full-with-fix-pre-merge.txt`、`05-reverse-verification-pre-merge.txt` | merge #205 之前（`1184bb07` + 测试 + 修法） | 保留作对照；以下最终头部的几份为准 |
| `06-reverse-verification-final-head.txt` | `bde31a1`（已 merge #205 即 `f31ca2b7`）；脚本 `hmi206-mutations.sh`：每种状态只替换一处（匹配数非 1 即退出）、`--no-incremental` 重编要求 `0 Error(s)`、新用例两格各跑 10 遍、从备份还原并核 SHA-256 | 修后 10/10 绿；M1 去掉修法 10/10 红在 `safety revision 2 has conflicting content`；M2 去掉「快照确认后推进已接受版本」10/10 红在 `the handshake failed: InvalidDataException: HANDSHAKE_SEQUENCE_INVALID`；M3 标志永不置位 10/10 红在 `conflicting content` |
| `07-reach-probe-final-head.txt` | `bde31a1`，把新分支临时换成抛 `HMI206-PROBE-REACHED`，全量 G2 | 除两条新用例外，走到新分支的既有用例 6 条：`LostSafetyStateChangedAckReplaysSameIdentityAndBusinessContentFromJournal`、`BusinessResendsSafetyStateAfterSessionGenerationChangeWhileVehicleIdle`、`BusinessSafetySendFailureDisconnectsAndReplaysPendingRevision`、`ASafetyReportJudgedWhileTheConnectionIsClosingIsNotWrittenIntoIt`、`ASafetyReportWhoseConnectionClosedUnderItDoesNotDropTheNextSession`（两格）。它们在修后全绿（`08`） |
| `08-full-final-head.txt` | `bde31a1`，`dotnet build SQCD_8005AGV.sln --no-incremental` 后经 `Invoke-HeavyLocal` 跑 `dotnet test --no-build` | `SQCD.Agv.UnitTests` 553/553，`SQCD.Agv.WireToGateG2Tests` 438/438 |
| `09-bisect-cs323-inbox-extract.txt` | `C:\w2g\bisect-cs323\hmi-7cf1dcba\stage-root\controlserver.db`（含 wal），拷出只读查询 | 现场读数：第 2 代 `SessionHello` → 补发的 `SafetyStateChanged` v5（`departureSafe=false`，生成于第 1 代）→ `CapabilitySnapshot`，之后本代再无报文；第 3 代 `SafetyStateSnapshot` v5（`departureSafe=true`）→ 恢复报告 → `SafetyStateChanged` v6 |

## 读法

- 01 与 02 合起来说明两层都有判别力：服务端那一层（假服务端照真服务端的做法共用一个安全版本号、按整行哈希比）红在拒收；关掉它，车载端这一层（快照的号必须大于刚补发的那条）照样红。
- M2 红在 `HANDSHAKE_SEQUENCE_INVALID`：服务端在握手末尾回报的是快照的号，车载端若不把已接受版本推进到它，`ApplySessionReadiness` 的「恰好相等」检查不过。那一句不是装饰。
- 走到新分支的既有用例在修前修后都绿，它们断言的是补发的身份与内容、断开与重连，不断言快照的号；这一格由本票新用例钉住。
- 09 里 v5 在第 2 代与第 3 代对应了两份不同的安全摘要。跨代没被服务端发现（它每代清空，`WireToGateStore.cs:157-158`）；第 2 代在补发 v5 之后只到了能力快照，与 control-server#340 的解释一致。cs#340 修好之后第 2 代会接着发握手快照、在同一代内冲突，这一步是按代码推的，库里没有（那次运行里还走不到）。
