# hmi#385 审查返工（PR #390 审查 M1、M3、L1、L4）

- `unit-tests.txt`：单元测试全量 1294 条全绿。
- `g2-targeted.txt`：新 G2 类、改写的旧 G2、屏幕快照、架构守卫、提示文案共 73 条全绿。
- `mutations/`：先提交 `0221967`，再逐个只改一个文件做变异，每个都由对应的新测试变红，跑完还原：
  - m1 去掉「取消在途时不给『重新开门』」→ `APendingLoadCancellationTakesTheReopenAway` 红；
  - m2 等按键期间仓位读不到不判 UNKNOWN → `ASlotThatTurnsUnreadableWhileHeldIsUnknown` 红；
  - m3 退回等按键前的稳定窗口改成 0 → `AReopenWhoseLockKeepsBouncingIsStillUnknown` 红；
  - m4 安全暂缓结束后照旧自动开锁 → `APressSpentOnTheSafetyHoldOpensNothingWhenTheHoldEndsAndTheButtonComesBack` 红。
- `g2-full-summary.txt`：G2 全量 1061 条，1056 过、5 败（本机同时有别的重负载）。其中
  `APendingLoadCancellationTakesTheReopenAway` 败在测试自己多写的最后一句断言（替身服务端不答复，取消请求在 2 秒消息超时后
  返回，负载下断言赶上了这一刻），已删去这句；其余 4 条是旧用例的等待超时。`g2-rerun-of-failed.txt`：这 5 条连跑 3 遍全过。
