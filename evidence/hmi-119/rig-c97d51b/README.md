# hmi#119 真装置重跑证据（车载端 `c97d51b`）

2026-09-19 16:55 起的真装置时段，按新的 Real-rig L2 规则用 worktree 当对端，`repos/` 下克隆没有动。三个提交：服务端 `aa17b059`（detached worktree），车载端 `c97d51b`（本票 worktree），模拟器 `fb5f7c5`（detached worktree）。车载端在申请时段前已预发布进 `%LOCALAPPDATA%\8005-l2-peers`。

| 目录 | 内容 | 结论 |
| --- | --- | --- |
| `compensate-then-reconnect/` | `real-onboard-compensate-then-reconnect` | PASS（9/9） |
| `pairing-cs187-002/` | 与 control-server#187 的配对验证，场景脚本与复现步骤同 `../rig-4e5cfcd/pairing-scenario/`、`../rig-4e5cfcd/README.md` | PASS（P-00～P-07） |

与 `../rig-4e5cfcd/` 的区别：那次 P-07「同车开新会话」为红（`RECOVERY_SESSION_STATE_PENDING`），本分支随后补了拒绝续行时清会话记录的修复。这次 P-07 为绿，第二次申请被 `ExceptionRecoverySessionOpened` 受理。拒绝仍经「模拟器 `lock-feedback-override FIXED_0` → 车载端门禁 `LOCK_NOT_CLOSED`」这条路径触发。
